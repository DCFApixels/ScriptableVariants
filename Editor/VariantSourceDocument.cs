using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class VariantSourceDocument
    {
        internal const int CurrentFormatVersion = 3;

        // Exact revision read from disk. Never serialize it into the authoring format.
        [JsonIgnore] internal string SourceJson;
        private Dictionary<string, VariantValueRecord> _valueIndex;

        [JsonProperty("formatVersion", Order = 0)]
        public int FormatVersion = CurrentFormatVersion;

        [JsonProperty("scriptGuid", Order = 1)]
        public string ScriptGuid;

        [JsonProperty("typeName", Order = 2)]
        public string TypeName;

        [JsonProperty("parentGuid", Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public string ParentGuid;

        [JsonProperty("overridePaths", Order = 4)]
        public List<string> OverridePaths = new List<string>();

        [JsonProperty("values", Order = 5)]
        public List<VariantValueRecord> Values = new List<VariantValueRecord>();

        internal VariantSourceDocument Clone()
        {
            return new VariantSourceDocument
            {
                FormatVersion = FormatVersion,
                SourceJson = SourceJson,
                ScriptGuid = ScriptGuid,
                TypeName = TypeName,
                ParentGuid = ParentGuid,
                OverridePaths = new List<string>(OverridePaths),
                Values = Values.Select(record => new VariantValueRecord
                {
                    Path = record.Path,
                    Value = record.Value?.DeepClone(),
                }).ToList(),
            };
        }

        internal void ValidateFormat()
        {
            if (FormatVersion != CurrentFormatVersion)
                throw new JsonSerializationException($"Unsupported variant format version {FormatVersion}. " +
                    $"Expected {CurrentFormatVersion}; legacy formats are not migrated.");
        }

        public void Normalize()
        {
            ValidateFormat();
            ParentGuid = string.IsNullOrWhiteSpace(ParentGuid) ? null : ParentGuid.Trim();
            var normalizedOverrides = (OverridePaths ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path.Count(character => character == '.'))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
            OverridePaths = new List<string>(normalizedOverrides.Count);
            var retainedOverrides = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < normalizedOverrides.Count; i++)
            {
                var candidate = normalizedOverrides[i];
                var hasAncestor = false;
                for (var separator = candidate.LastIndexOf('.'); separator > 0;
                     separator = candidate.LastIndexOf('.', separator - 1))
                {
                    if (retainedOverrides.Contains(candidate.Substring(0, separator)))
                    {
                        hasAncestor = true;
                        break;
                    }
                }

                if (!hasAncestor)
                {
                    OverridePaths.Add(candidate);
                    retainedOverrides.Add(candidate);
                }
            }

            OverridePaths.Sort(StringComparer.Ordinal);

            var records = new Dictionary<string, VariantValueRecord>(StringComparer.Ordinal);
            foreach (var record in Values ?? new List<VariantValueRecord>())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Path) || record.Value == null)
                {
                    throw new JsonSerializationException("A stored value must have a path and an explicit JSON value.");
                }

                record.Path = record.Path.Trim();
                if (records.ContainsKey(record.Path))
                {
                    throw new JsonSerializationException($"Duplicate stored value '{record.Path}'.");
                }
                records.Add(record.Path, record);
            }

            Values = records.Values.OrderBy(record => record.Path, StringComparer.Ordinal).ToList();
            _valueIndex = records;
        }

        public VariantValueRecord FindValue(string path)
        {
            if (_valueIndex == null || _valueIndex.Count != Values.Count)
                _valueIndex = Values.ToDictionary(record => record.Path, StringComparer.Ordinal);
            return _valueIndex.TryGetValue(path, out var value) ? value : null;
        }

        public void SetValue(VariantValueRecord record)
        {
            _valueIndex = null;
            for (var i = 0; i < Values.Count; i++)
            {
                if (!string.Equals(Values[i].Path, record.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                Values[i] = record;
                return;
            }

            Values.Add(record);
        }

        public void RemoveValue(string path)
        {
            _valueIndex = null;
            Values.RemoveAll(record => string.Equals(record.Path, path, StringComparison.Ordinal));
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class VariantValueRecord
    {
        [JsonProperty("path", Order = 0)]
        public string Path;

        [JsonProperty("value", Order = 1, NullValueHandling = NullValueHandling.Include)]
        public JToken Value;
    }

    [InitializeOnLoad]
    internal static class VariantSourceDatabase
    {
        internal const string Extension = "svariant";

        private const double ImportDelaySeconds = 0.15d;
        private const int MaximumCachedDocuments = 128;
        private const int MaximumCachedCharacters = 4 * 1024 * 1024;
        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, double> PendingImports =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerSettings DocumentSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            CheckAdditionalContent = true,
            MaxDepth = 128,
            MissingMemberHandling = MissingMemberHandling.Error,
        };

        static VariantSourceDatabase()
        {
            EditorApplication.update -= FlushPendingImports;
            EditorApplication.update += FlushPendingImports;
            EditorApplication.delayCall += VariantSourceTransaction.Recover;
        }

        internal static bool IsVariantSourcePath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) &&
                   string.Equals(Path.GetExtension(assetPath), "." + Extension, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryLoad(
            ScriptableVariant variant,
            out VariantSourceDocument document,
            out string assetPath,
            out string error)
        {
            assetPath = variant != null ? VariantEditingSession.GetAssetPath(variant) : null;
            if (VariantEditingSession.TryGetDocument(variant, out document))
            {
                error = null;
                return true;
            }
            if (!IsVariantSourcePath(assetPath))
            {
                document = null;
                error = "The object is not imported from a .svariant source file.";
                return false;
            }

            return TryLoad(assetPath, out document, out error);
        }

        internal static bool TryLoad(string assetPath, out VariantSourceDocument document, out string error)
        {
            // Inspector queries are memory-only between bounded checks; mutations always verify
            // the exact source text again, including edits that preserve timestamps and length.
            if (Cache.TryGetValue(assetPath, out var cached) &&
                EditorApplication.timeSinceStartup - cached.CheckedAt < 0.5d)
            {
                document = cached.Document;
                error = null;
                return true;
            }

            if (cached != null)
            {
                var info = new FileInfo(FileUtil.GetPhysicalPath(assetPath));
                if (info.Exists && info.Length == cached.Length && info.LastWriteTimeUtc.Ticks == cached.WriteTimeUtc)
                {
                    cached.CheckedAt = EditorApplication.timeSinceStartup;
                    document = cached.Document;
                    error = null;
                    return true;
                }
            }

            return TryLoadUncached(assetPath, out document, out error);
        }

        internal static bool TryLoadForEdit(
            ScriptableVariant variant,
            out VariantSourceDocument document,
            out string assetPath,
            out string error)
        {
            VariantEditingSession.AssertCurrent(variant);
            if (!TryLoad(variant, out document, out assetPath, out error))
            {
                return false;
            }

            // Commands must not mutate the read cache before their save succeeds.
            document = document.Clone();
            ScriptableVariantImporter.RemapFormerPaths(document, variant.GetType());
            return true;
        }

        internal static bool TryLoadUncached(
            string assetPath,
            out VariantSourceDocument document,
            out string error)
        {
            document = null;
            if (!IsVariantSourcePath(assetPath))
            {
                error = $"'{assetPath}' is not a .svariant source file.";
                return false;
            }

            try
            {
                var fullPath = FileUtil.GetPhysicalPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    error = $"Variant source file does not exist: {assetPath}";
                    return false;
                }

                if (new FileInfo(fullPath).Length > 32 * 1024 * 1024)
                    throw new IOException("Variant sources larger than 32 MiB are not supported.");
                var json = File.ReadAllText(fullPath);
                document = DeserializeDocument(json);
                if (document == null)
                {
                    error = "Variant source contains no document.";
                    return false;
                }

                if (document.FormatVersion > VariantSourceDocument.CurrentFormatVersion)
                {
                    error = $"Variant format {document.FormatVersion} is newer than supported format " +
                            $"{VariantSourceDocument.CurrentFormatVersion}.";
                    document = null;
                    return false;
                }

                document.Normalize();
                document.SourceJson = json;
                CacheDocument(assetPath, fullPath, document);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                document = null;
                Cache.Remove(assetPath);
                error = $"Could not read variant source: {exception.Message}";
                return false;
            }
        }

        internal static string SerializeDocument(VariantSourceDocument document)
        {
            document.Normalize();
            using (var text = new StringWriter(CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(text))
            {
                // Do not inherit JsonConvert.DefaultSettings from unrelated editor packages.
                JsonSerializer.Create(DocumentSettings).Serialize(writer, document);
                writer.Flush();
                return text.ToString() + "\n";
            }
        }

        internal static VariantSourceDocument DeserializeDocument(string json)
        {
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.MaxDepth = 128;
                var token = JToken.Load(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                });
                if (reader.Read()) throw new JsonReaderException("Unexpected content after the variant document.");
                if (!(token is JObject)) throw new JsonSerializationException("A variant document must be an object.");
                if (token["formatVersion"]?.Type != JTokenType.Integer)
                    throw new JsonSerializationException("Variant source must declare an integer formatVersion.");
                if (!(token["values"] is JArray) || !(token["overridePaths"] is JArray))
                    throw new JsonSerializationException("Variant source must contain values and overridePaths arrays.");
                return token.ToObject<VariantSourceDocument>(JsonSerializer.Create(DocumentSettings));
            }
        }

        internal static void Save(
            string assetPath, VariantSourceDocument document, bool importImmediately = false, bool recordUndo = true)
        {
            SaveBatch(new[] {assetPath}, new[] {document}, importImmediately, recordUndo);
        }

        internal static void SaveBatch(IReadOnlyList<string> paths, IReadOnlyList<VariantSourceDocument> documents,
            bool importImmediately = false, bool recordUndo = true)
        {
            if (paths.Count == 0) return;
            var committed = false;
            AssetDatabase.StartAssetEditing();
            try
            {
                var changes = VariantSourceTransaction.Commit(paths, documents);
                committed = true;
                // No observers, Undo states or import jobs see a half-written batch. Once disk
                // is committed, a notification failure must not tell callers to roll back memory.
                foreach (var change in changes)
                    if (recordUndo) AfterCommit(() => VariantSourceUndo.Record(change.Path, change.Before, change.After));
                for (var i = 0; i < paths.Count; i++)
                {
                    var path = paths[i];
                    var document = documents[i];
                    document.SourceJson = SerializeDocument(document);
                    AfterCommit(() => CacheDocument(path, FileUtil.GetPhysicalPath(path), document.Clone()));
                }
                foreach (var change in changes)
                {
                    QueueImport(change.Path);
                    AfterCommit(() => VariantEditingSession.SourceSaved(change.Path, recordUndo));
                }
            }
            finally
            {
                if (committed) AfterCommit(AssetDatabase.StopAssetEditing);
                else AssetDatabase.StopAssetEditing();
            }
            if (importImmediately)
                foreach (var path in paths) AfterCommit(() => ImportNow(path));
        }

        private static void AfterCommit(Action action)
        {
            try { action(); }
            catch (Exception exception)
            {
                Debug.LogError($"Variant source was saved, but an Editor update failed: {exception.Message}. " +
                    "Reopen the Inspector or reimport the source; do not assume that the file write was rolled back.");
            }
        }

        internal static void QueueImport(string path) => PendingImports[path] = EditorApplication.timeSinceStartup;

        internal static void AssertRevision(string assetPath, string expectedJson)
        {
            var fullPath = FileUtil.GetPhysicalPath(assetPath);
            var actual = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            if (!string.Equals(actual, expectedJson, StringComparison.Ordinal))
                throw new IOException($"Variant source changed outside this edit: '{assetPath}'. " +
                    "Pending edits were kept. Reload the source or resolve the conflict before saving.");
        }

        internal static void WriteSourceAtomically(string fullPath, string json)
        {
            fullPath = Path.GetFullPath(fullPath);
            // A sibling keeps replacement on the same filesystem. Unity ignores dot-prefixed files.
            var temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath),
                "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(fullPath))
                {
                    File.Replace(temporaryPath, fullPath, null);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        internal static void Invalidate(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                Cache.Remove(assetPath);
                PendingImports.Remove(assetPath);
            }
        }

        internal static void ImportNow(string assetPath)
        {
            PendingImports.Remove(assetPath);
            AssetDatabase.ImportAsset(assetPath);
        }

        private static void FlushPendingImports()
        {
            if (PendingImports.Count == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            var ready = PendingImports
                .Where(pair => now - pair.Value >= ImportDelaySeconds)
                .Select(pair => pair.Key)
                .ToArray();
            for (var i = 0; i < ready.Length; i++)
            {
                PendingImports.Remove(ready[i]);
                if (File.Exists(FileUtil.GetPhysicalPath(ready[i]))) AssetDatabase.ImportAsset(ready[i]);
            }
        }

        private static CacheEntry CreateCacheEntry(string fullPath, VariantSourceDocument document)
        {
            var info = new FileInfo(fullPath);
            return new CacheEntry
            {
                Document = document,
                CheckedAt = EditorApplication.timeSinceStartup,
                Length = info.Exists ? info.Length : 0,
                WriteTimeUtc = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            };
        }

        private sealed class CacheEntry
        {
            public VariantSourceDocument Document;
            public double CheckedAt;
            public long Length;
            public long WriteTimeUtc;
        }

        private static void CacheDocument(string path, string fullPath, VariantSourceDocument document)
        {
            Cache.Remove(path);
            var length = document.SourceJson?.Length ?? 0;
            if (length > MaximumCachedCharacters) return;
            while (Cache.Count > 0 && (Cache.Count >= MaximumCachedDocuments ||
                Cache.Values.Sum(entry => (long)(entry.Document.SourceJson?.Length ?? 0)) + length > MaximumCachedCharacters))
                Cache.Remove(Cache.OrderBy(pair => pair.Value.CheckedAt).First().Key);
            Cache[path] = CreateCacheEntry(fullPath, document);
        }
    }
}
