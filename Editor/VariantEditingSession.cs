using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    // Shared by Inspectors of the same source. Published imported objects are never editing targets.
    internal sealed class VariantEditingSession : IDisposable
    {
        private const HideFlags EditingFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        private static readonly Dictionary<string, VariantEditingSession> Sessions =
            new Dictionary<string, VariantEditingSession>(StringComparer.Ordinal);
        private static readonly Dictionary<ScriptableVariant, VariantEditingSession> WorkingCopies =
            new Dictionary<ScriptableVariant, VariantEditingSession>();

        private readonly string _guid;
        private readonly ScriptableVariant _baseline;
        private string _baselineJson;
        private int _references;
        private bool _saving;

        internal ScriptableVariant WorkingCopy { get; }
        internal string AssetPath => AssetDatabase.GUIDToAssetPath(_guid);
        internal event Action Reloaded;

        private VariantEditingSession(string guid, ScriptableVariant workingCopy)
        {
            _guid = guid;
            WorkingCopy = workingCopy;
            WorkingCopy.hideFlags = EditingFlags;
            _baseline = ScriptableObject.CreateInstance(workingCopy.GetType()) as ScriptableVariant;
            AcceptValues();
        }

        internal static VariantEditingSession Acquire(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException($"Variant source does not exist: {assetPath}");
            }

            if (!Sessions.TryGetValue(guid, out var session))
            {
                if (!ScriptableVariantImporter.TryCreateVariant(assetPath, out var copy, out var error))
                {
                    throw new InvalidOperationException(error);
                }

                session = new VariantEditingSession(guid, copy);
                Sessions.Add(guid, session);
                WorkingCopies.Add(copy, session);
            }

            session._references++;
            return session;
        }

        internal static string GetAssetPath(ScriptableVariant variant)
        {
            if (variant == null)
            {
                return string.Empty;
            }

            return WorkingCopies.TryGetValue(variant, out var session)
                ? session.AssetPath
                : AssetDatabase.GetAssetPath(variant);
        }

        internal static bool IsWorkingCopy(ScriptableVariant variant)
        {
            return variant != null && WorkingCopies.ContainsKey(variant);
        }

        internal static void CommitValues(ScriptableVariant variant)
        {
            if (variant != null && WorkingCopies.TryGetValue(variant, out var session))
            {
                session.CommitValues();
            }
        }

        internal void CommitValues()
        {
            if (_saving || WorkingCopy == null ||
                string.Equals(EditorJsonUtility.ToJson(WorkingCopy), _baselineJson, StringComparison.Ordinal))
            {
                return;
            }

            _saving = true;
            try
            {
                ScriptableVariantAssetUtility.SaveWorkingCopy(WorkingCopy, _baseline);
                AcceptValues();
            }
            finally
            {
                _saving = false;
            }
        }

        private void AcceptValues()
        {
            EditorUtility.CopySerialized(WorkingCopy, _baseline);
            _baseline.hideFlags = EditingFlags;
            _baselineJson = EditorJsonUtility.ToJson(WorkingCopy);
        }

        internal static void SourceSaved(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (Sessions.TryGetValue(guid, out var session) && !session._saving)
            {
                // Header/context actions have already changed the working values. Accept these
                // before UI bindings observe them, so Revert/Undo cannot create fresh overrides.
                session.AcceptValues();
            }

            RequestReload();
        }

        internal static void RequestReload()
        {
            if (Sessions.Count == 0)
            {
                return;
            }

            EditorApplication.delayCall -= ReloadOpenSessions;
            EditorApplication.delayCall += ReloadOpenSessions;
        }

        internal static void ReloadOpenSessions()
        {
            foreach (var session in Sessions.Values.ToArray())
            {
                session.Reload();
            }
        }

        private void Reload()
        {
            var path = AssetPath;
            if (WorkingCopy == null || string.IsNullOrEmpty(path) ||
                !ScriptableVariantImporter.TryCreateVariant(path, out var resolved, out _))
            {
                return;
            }

            try
            {
                if (resolved.GetType() != WorkingCopy.GetType())
                {
                    // Unity recreates the importer inspector after the type-changing import.
                    return;
                }

                resolved.hideFlags = EditingFlags;
                if (!string.Equals(EditorJsonUtility.ToJson(resolved), _baselineJson, StringComparison.Ordinal))
                {
                    EditorUtility.CopySerialized(resolved, WorkingCopy);
                    AcceptValues();
                    Reloaded?.Invoke();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(resolved);
            }
        }

        public void Dispose()
        {
            if (--_references > 0)
            {
                return;
            }

            Sessions.Remove(_guid);
            WorkingCopies.Remove(WorkingCopy);
            Undo.ClearUndo(WorkingCopy);
            UnityEngine.Object.DestroyImmediate(WorkingCopy);
            UnityEngine.Object.DestroyImmediate(_baseline);
        }
    }

    internal sealed class VariantEditingSessionPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (importedAssets.Any(VariantSourceDatabase.IsVariantSourcePath) ||
                deletedAssets.Any(VariantSourceDatabase.IsVariantSourcePath) ||
                movedAssets.Any(VariantSourceDatabase.IsVariantSourcePath))
            {
                // Only open editing sessions are refreshed; no project-wide asset search.
                VariantEditingSession.RequestReload();
            }
        }
    }
}
