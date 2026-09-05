using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    // One editable object per source GUID. Closed, unsaved sessions are retained, never discarded.
    [InitializeOnLoad]
    internal sealed class VariantEditingSession : IDisposable
    {
        private const HideFlags EditingFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        private static readonly Dictionary<string, VariantEditingSession> Sessions =
            new Dictionary<string, VariantEditingSession>(StringComparer.Ordinal);
        private static readonly Dictionary<ScriptableVariant, VariantEditingSession> WorkingCopies =
            new Dictionary<ScriptableVariant, VariantEditingSession>();
        private static double _nextRevisionCheck;

        private readonly string _guid;
        private readonly ScriptableVariant _baseline;
        private VariantSourceDocument _document;
        private Dictionary<string, string> _revisions;
        private readonly HashSet<string> _dependencies = new HashSet<string>(StringComparer.Ordinal);
        private string _baselineJson;
        private int _references;
        private bool _saving;
        private bool _pending;
        private bool _needsReload;
        private bool _protectedRecovery;
        private double _saveAfter;

        internal ScriptableVariant WorkingCopy { get; }
        internal string AssetPath => AssetDatabase.GUIDToAssetPath(_guid);
        internal string Error { get; private set; }
        internal event Action Reloaded;
        internal event Action StateChanged;

        static VariantEditingSession()
        {
            EditorApplication.update += UpdateSessions;
            AssemblyReloadEvents.beforeAssemblyReload += BackUpPendingSessions;
            EditorApplication.wantsToQuit += PrepareToQuit;
        }

        private VariantEditingSession(string guid, ScriptableVariant copy, VariantSourceDocument document,
            Dictionary<string, string> revisions)
        {
            _guid = guid;
            WorkingCopy = copy;
            copy.hideFlags = EditingFlags;
            _document = document.Clone();
            _revisions = revisions;
            _baseline = ScriptableObject.CreateInstance(copy.GetType()) as ScriptableVariant;
            try { AcceptValues(); RebuildDependencies(); }
            catch
            {
                if (_baseline != null) UnityEngine.Object.DestroyImmediate(_baseline);
                throw;
            }
        }

        internal static VariantEditingSession Acquire(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException($"Variant source does not exist: {path}");
            if (!Sessions.TryGetValue(guid, out var session))
            {
                var scope = new ScriptableVariantImporter.ResolutionScope();
                var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!ScriptableVariantImporter.TryCreateVariant(path, out var copy, out var error,
                        scope: scope, revisions: revisions)) throw new InvalidOperationException(error);
                try { session = new VariantEditingSession(guid, copy, scope.Read(path), revisions); }
                catch { UnityEngine.Object.DestroyImmediate(copy); throw; }
                Sessions.Add(guid, session);
                WorkingCopies.Add(copy, session);
                session.RestoreRecovery();
            }
            session._references++;
            return session;
        }

        internal static string GetAssetPath(ScriptableVariant variant) => variant == null ? string.Empty :
            WorkingCopies.TryGetValue(variant, out var session) ? session.AssetPath : AssetDatabase.GetAssetPath(variant);

        internal static bool IsWorkingCopy(ScriptableVariant variant) => variant != null && WorkingCopies.ContainsKey(variant);

        internal static bool TryGetSession(ScriptableVariant variant, out VariantEditingSession session)
        {
            session = null;
            return variant != null && WorkingCopies.TryGetValue(variant, out session);
        }

        internal static bool TryGetDocument(ScriptableVariant variant, out VariantSourceDocument document)
        {
            document = null;
            if (!TryGetSession(variant, out var session)) return false;
            document = session._document;
            return true;
        }

        internal static void AssertCurrent(ScriptableVariant variant)
        {
            if (!TryGetSession(variant, out var session)) return;
            if (session._protectedRecovery)
                throw new IOException("An unreadable recovery snapshot is retained. Recover it manually or explicitly reload from source before saving.");
            foreach (var revision in session._revisions) VariantSourceDatabase.AssertRevision(revision.Key, revision.Value);
        }

        internal static void CommitValues(ScriptableVariant variant)
        {
            if (TryGetSession(variant, out var session)) session.CommitValues();
        }

        internal void RequestCommit()
        {
            _pending = true;
            _saveAfter = EditorApplication.timeSinceStartup + 0.25d;
        }

        internal bool HasPendingChanges => WorkingCopy != null &&
            !string.Equals(EditorJsonUtility.ToJson(WorkingCopy), _baselineJson, StringComparison.Ordinal);

        internal void CommitValues()
        {
            if (_saving || WorkingCopy == null) return;
            _pending = false;
            if (!HasPendingChanges) return;
            _saving = true;
            try
            {
                AssertCurrent(WorkingCopy);
                ScriptableVariantAssetUtility.SaveWorkingCopy(WorkingCopy, _baseline);
                AcceptValues();
                DeleteRecovery();
                SetError(null);
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
                BackUp();
                throw;
            }
            finally { _saving = false; }
        }

        private void AcceptValues()
        {
            ScriptableVariantAssetUtility.CopyEditingValues(WorkingCopy, _baseline);
            _baseline.hideFlags = EditingFlags;
            _baselineJson = EditorJsonUtility.ToJson(WorkingCopy);
            // Committed edits use source-backed Undo. Keeping native snapshots too would introduce
            // duplicate Undo steps referring to temporary objects and stale inherited values.
            Undo.ClearUndo(WorkingCopy);
        }

        internal static void SourceSaved(string path, bool acceptWorkingValues = true)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (acceptWorkingValues && Sessions.TryGetValue(guid, out var session))
            {
                if (VariantSourceDatabase.TryLoad(path, out var document, out _))
                {
                    session._document = document.Clone();
                    session._revisions[path] = document.SourceJson;
                    session.UpdateParentRevisions();
                }
                if (!session._saving)
                {
                    session.AcceptValues();
                    session._needsReload = true; // header commands can also change the inheritance graph
                }
                session.RebuildDependencies();
                session.StateChanged?.Invoke();
            }
            RequestReload(path);
        }

        private void UpdateParentRevisions()
        {
            var revisions = new Dictionary<string, string>(StringComparer.Ordinal)
                {[AssetPath] = _document.SourceJson};
            var visited = new HashSet<string>(StringComparer.Ordinal) {_guid};
            var parent = _document.ParentGuid;
            while (!string.IsNullOrEmpty(parent) && visited.Add(parent))
            {
                var path = AssetDatabase.GUIDToAssetPath(parent);
                if (!VariantSourceDatabase.TryLoad(path, out var document, out _)) break;
                revisions[path] = document.SourceJson;
                parent = document.ParentGuid;
            }
            _revisions = revisions;
        }

        private void RebuildDependencies()
        {
            _dependencies.Clear();
            foreach (var revision in _revisions)
            {
                _dependencies.Add(revision.Key);
                var document = VariantSourceDatabase.DeserializeDocument(revision.Value);
                var script = AssetDatabase.GUIDToAssetPath(document.ScriptGuid);
                if (!string.IsNullOrEmpty(script)) _dependencies.Add(script);
                foreach (var record in document.Values)
                    VariantValueSerializer.AddObjectDependencies(record.Value, path => _dependencies.Add(path));
            }
        }

        internal static void RequestReload(string changedPath = null)
        {
            foreach (var session in Sessions.Values)
                if (changedPath == null || session._dependencies.Contains(changedPath))
                    session._needsReload |= changedPath == null || !VariantSourceDatabase.IsVariantSourcePath(changedPath);
        }

        internal static void ReloadOpenSessions(bool cached = false)
        {
            var scope = new ScriptableVariantImporter.ResolutionScope(cached);
            foreach (var session in Sessions.Values.ToArray())
                if (session._references > 0 || !cached) session.Reload(scope);
        }

        private void Reload(ScriptableVariantImporter.ResolutionScope scope, bool discard = false)
        {
            if (WorkingCopy == null || _protectedRecovery && !discard) return;
            try
            {
                var force = _needsReload || discard;
                _needsReload = false;
                var changed = false;
                foreach (var revision in _revisions)
                    if (!string.Equals(scope.Read(revision.Key).SourceJson, revision.Value, StringComparison.Ordinal))
                    { changed = true; break; }
                if (!force && !changed) return;
                if (!discard && HasPendingChanges)
                {
                    if (changed) SetError("Source or parent changed while this Inspector has pending edits. " +
                        "Your edits were retained. Resolve the conflict or reload and discard them explicitly.");
                    return;
                }
                var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!ScriptableVariantImporter.TryCreateVariant(AssetPath, out var resolved, out var error,
                        scope: scope, revisions: revisions)) { SetError(error); return; }
                try
                {
                    if (resolved.GetType() != WorkingCopy.GetType())
                    { SetError("The source type changed. Reopen this Inspector; pending edits have not been overwritten."); return; }
                    resolved.hideFlags = EditingFlags;
                    ScriptableVariantAssetUtility.CopyEditingValues(resolved, WorkingCopy);
                    _document = scope.Read(AssetPath).Clone();
                    _revisions = revisions;
                    AcceptValues();
                    RebuildDependencies();
                    _pending = false;
                    if (discard) _protectedRecovery = false;
                    DeleteRecovery();
                    SetError(null);
                    StateChanged?.Invoke();
                    Reloaded?.Invoke();
                }
                finally { UnityEngine.Object.DestroyImmediate(resolved); }
            }
            catch (Exception exception) { SetError(exception.Message); }
        }

        // Caller must obtain explicit confirmation before discarding a user's pending edits.
        internal void ReloadDiscardingChanges() => Reload(new ScriptableVariantImporter.ResolutionScope(), true);

        internal void RemoveOrphans() => ScriptableVariantAssetUtility.RemoveOrphanOverrides(WorkingCopy, _baseline);

        private void SetError(string error)
        {
            if (string.Equals(Error, error, StringComparison.Ordinal)) return;
            Error = error;
            StateChanged?.Invoke();
        }

        internal void ReportError(string error) => SetError(error);

        private static void UpdateSessions()
        {
            if (Sessions.Count == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var now = EditorApplication.timeSinceStartup;
            foreach (var session in Sessions.Values.ToArray())
            {
                if (!session._pending || now < session._saveAfter) continue;
                try { session.CommitValues(); }
                catch { /* surfaced in every owning Inspector; do not retry or spam each frame */ }
            }
            if (now < _nextRevisionCheck) return;
            _nextRevisionCheck = now + 0.75d;
            // One bounded pass per open source, never a filesystem poll per property.
            ReloadOpenSessions(true);
        }

        public void Dispose()
        {
            if (_references <= 0 || --_references > 0) return;
            if (HasPendingChanges)
            {
                BackUp();
                return; // next Inspector can recover the same object, including transient references
            }
            try { DeleteRecovery(); }
            catch (Exception exception) { SetError(exception.Message); return; }
            Sessions.Remove(_guid);
            WorkingCopies.Remove(WorkingCopy);
            Undo.ClearUndo(WorkingCopy);
            UnityEngine.Object.DestroyImmediate(WorkingCopy);
            UnityEngine.Object.DestroyImmediate(_baseline);
        }

        [Serializable]
        private sealed class Recovery
        {
            public string Source;
            public string Baseline;
            public string Working;
            public string[] Paths;
            public string[] Revisions;
        }

        private string RecoveryPath => Path.GetFullPath("Library/ScriptableVariants/Recovery/" + _guid + ".json");

        private bool BackUp()
        {
            try
            {
                if (_protectedRecovery) throw new IOException("The earlier recovery snapshot is protected and has not been overwritten.");
                using (var serialized = new SerializedObject(WorkingCopy))
                {
                    var property = serialized.GetIterator();
                    var references = new HashSet<long>();
                    var enterChildren = true;
                    while (property.Next(enterChildren))
                    {
                        enterChildren = property.propertyType != SerializedPropertyType.ManagedReference ||
                            references.Add(property.managedReferenceId);
                        if (property.propertyType == SerializedPropertyType.ObjectReference &&
                            property.objectReferenceValue != null && !EditorUtility.IsPersistent(property.objectReferenceValue))
                            throw new IOException($"'{property.propertyPath}' references a transient object. " +
                                "Save or correct that reference before closing the Editor; only the in-memory copy can retain it.");
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(RecoveryPath));
                var recovery = new Recovery
                {
                    Source = _document.SourceJson, Baseline = _baselineJson,
                    Working = EditorJsonUtility.ToJson(WorkingCopy),
                    Paths = _revisions.Keys.ToArray(), Revisions = _revisions.Values.ToArray(),
                };
                VariantSourceDatabase.WriteSourceAtomically(RecoveryPath, JsonUtility.ToJson(recovery));
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Could not back up pending variant edits '{AssetPath}': {exception.Message}. " +
                    "The working copy remains in memory; keep the Editor open.");
                return false;
            }
        }

        private void RestoreRecovery()
        {
            if (!File.Exists(RecoveryPath)) return;
            _protectedRecovery = true;
            ScriptableVariant recovered = null;
            ScriptableVariant baseline = null;
            try
            {
                var recovery = JsonUtility.FromJson<Recovery>(File.ReadAllText(RecoveryPath));
                if (recovery == null || string.IsNullOrEmpty(recovery.Working) || string.IsNullOrEmpty(recovery.Baseline))
                    throw new IOException("Incomplete recovery snapshot.");
                var source = VariantSourceDatabase.DeserializeDocument(recovery.Source);
                source.Normalize();
                if (source.TypeName != _document.TypeName || source.ScriptGuid != _document.ScriptGuid)
                    throw new IOException("Recovery belongs to a different script type; restore that type before recovering its fields.");
                recovered = ScriptableObject.CreateInstance(WorkingCopy.GetType()) as ScriptableVariant;
                baseline = ScriptableObject.CreateInstance(WorkingCopy.GetType()) as ScriptableVariant;
                EditorJsonUtility.FromJsonOverwrite(recovery.Working, recovered);
                EditorJsonUtility.FromJsonOverwrite(recovery.Baseline, baseline);
                ScriptableVariantAssetUtility.CopyEditingValues(recovered, WorkingCopy);
                ScriptableVariantAssetUtility.CopyEditingValues(baseline, _baseline);
                WorkingCopy.hideFlags = _baseline.hideFlags = EditingFlags;
                source.SourceJson = recovery.Source;
                ScriptableVariantImporter.RemapFormerPaths(source, WorkingCopy.GetType());
                _document = source;
                _baselineJson = EditorJsonUtility.ToJson(_baseline);
                // Do not silently bless an external source revision as the base of recovered edits.
                _revisions[AssetPath] = recovery.Source;
                if (recovery.Paths != null && recovery.Revisions != null && recovery.Paths.Length == recovery.Revisions.Length)
                    for (var i = 0; i < recovery.Paths.Length; i++)
                    {
                        VariantSourceTransaction.GetSourcePath(recovery.Paths[i]);
                        _revisions[recovery.Paths[i]] = recovery.Revisions[i];
                    }
                RebuildDependencies();
                SetError("Recovered unsaved edits. Review them and retry saving, or explicitly reload from source.");
                _protectedRecovery = false;
            }
            catch (Exception exception) { SetError($"Recovery snapshot retained at '{RecoveryPath}': {exception.Message}"); }
            finally
            {
                if (recovered != null) UnityEngine.Object.DestroyImmediate(recovered);
                if (baseline != null) UnityEngine.Object.DestroyImmediate(baseline);
            }
        }

        private void DeleteRecovery()
        {
            if (_protectedRecovery) return;
            if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
        }

        private static void BackUpPendingSessions()
        {
            foreach (var session in Sessions.Values)
                if (session.HasPendingChanges) session.BackUp();
        }

        private static bool PrepareToQuit()
        {
            var safe = true;
            foreach (var session in Sessions.Values)
            {
                if (session.HasPendingChanges) safe &= session.BackUp();
                else
                    try { session.DeleteRecovery(); }
                    catch (Exception exception) { session.SetError(exception.Message); safe = false; }
            }
            return safe;
        }

        internal static void SourceMoved(string before, string after)
        {
            foreach (var session in Sessions.Values)
            {
                if (session._revisions.TryGetValue(before, out var revision))
                {
                    session._revisions.Remove(before);
                    session._revisions[after] = revision;
                }
                if (session._dependencies.Remove(before)) session._dependencies.Add(after);
                session.StateChanged?.Invoke();
            }
        }
    }

    internal sealed class VariantEditingSessionPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            for (var i = 0; i < moved.Length; i++) VariantEditingSession.SourceMoved(movedFrom[i], moved[i]);
            Notify(imported);
            Notify(deleted);
            Notify(moved);
            Notify(movedFrom);
            VariantSourceUndo.PruneDeletedSources();
        }

        private static void Notify(string[] paths)
        {
            foreach (var path in paths)
            {
                if (VariantSourceDatabase.IsVariantSourcePath(path)) VariantSourceDatabase.Invalidate(path);
                VariantEditingSession.RequestReload(path);
            }
        }
    }
}
