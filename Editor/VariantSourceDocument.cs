using System;
using System.Collections.Generic;
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
        internal const int CurrentFormatVersion = 1;

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

        public void Normalize()
        {
            FormatVersion = CurrentFormatVersion;
            ParentGuid = string.IsNullOrWhiteSpace(ParentGuid) ? null : ParentGuid.Trim();
            var normalizedOverrides = (OverridePaths ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path.Count(character => character == '.'))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
            OverridePaths = new List<string>(normalizedOverrides.Count);
            for (var i = 0; i < normalizedOverrides.Count; i++)
            {
                var candidate = normalizedOverrides[i];
                if (!OverridePaths.Any(
                        ancestor => candidate.StartsWith(ancestor + ".", StringComparison.Ordinal)))
                {
                    OverridePaths.Add(candidate);
                }
            }

            OverridePaths.Sort(StringComparer.Ordinal);

            var records = new Dictionary<string, VariantValueRecord>(StringComparer.Ordinal);
            foreach (var record in Values ?? new List<VariantValueRecord>())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Path))
                {
                    continue;
                }

                record.Path = record.Path.Trim();
                records[record.Path] = record;
            }

            Values = records.Values.OrderBy(record => record.Path, StringComparer.Ordinal).ToList();
        }

        public VariantValueRecord FindValue(string path)
        {
            for (var i = 0; i < Values.Count; i++)
            {
                if (string.Equals(Values[i].Path, path, StringComparison.Ordinal))
                {
                    return Values[i];
                }
            }

            return null;
        }

        public void SetValue(VariantValueRecord record)
        {
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
            Values.RemoveAll(record => string.Equals(record.Path, path, StringComparison.Ordinal));
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class VariantValueRecord
    {
        [JsonProperty("path", Order = 0)]
        public string Path;

        [JsonProperty("value", Order = 1)]
        public JToken Value;
    }

    [InitializeOnLoad]
    internal static class VariantSourceDatabase
    {
        internal const string Extension = "svariant";

        private const double ImportDelaySeconds = 0.15d;
        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, double> PendingImports =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerSettings DocumentSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        };

        static VariantSourceDatabase()
        {
            EditorApplication.update -= FlushPendingImports;
            EditorApplication.update += FlushPendingImports;
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
            var fullPath = FileUtil.GetPhysicalPath(assetPath);
            var fileInfo = new FileInfo(fullPath);
            if (Cache.TryGetValue(assetPath, out var cached) && fileInfo.Exists &&
                cached.Length == fileInfo.Length && cached.WriteTimeUtc == fileInfo.LastWriteTimeUtc.Ticks)
            {
                document = cached.Document;
                error = null;
                return true;
            }

            return TryLoadUncached(assetPath, out document, out error);
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

                document = DeserializeDocument(File.ReadAllText(fullPath));
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
                Cache[assetPath] = CreateCacheEntry(fullPath, document);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not read variant source: {exception.Message}";
                return false;
            }
        }

        internal static string SerializeDocument(VariantSourceDocument document)
        {
            document.Normalize();
            return JsonConvert.SerializeObject(document, DocumentSettings) + "\n";
        }

        internal static VariantSourceDocument DeserializeDocument(string json)
        {
            return JsonConvert.DeserializeObject<VariantSourceDocument>(json, DocumentSettings);
        }

        internal static void Save(
            string assetPath, VariantSourceDocument document, bool importImmediately = false, bool recordUndo = true)
        {
            if (!IsVariantSourcePath(assetPath))
            {
                throw new ArgumentException("Variant source path must end with .svariant.", nameof(assetPath));
            }

            var json = SerializeDocument(document);
            var fullPath = FileUtil.GetPhysicalPath(assetPath);
            var previousJson = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            if (string.Equals(previousJson, json, StringComparison.Ordinal))
            {
                Cache[assetPath] = CreateCacheEntry(fullPath, document);
                return;
            }

            if (File.Exists(fullPath) && !AssetDatabase.MakeEditable(assetPath))
            {
                throw new IOException($"Variant source is not editable: {assetPath}");
            }

            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
            if (recordUndo)
            {
                VariantSourceUndo.Record(assetPath, previousJson, json);
            }

            Cache[assetPath] = CreateCacheEntry(fullPath, document);
            VariantEditingSession.SourceSaved(assetPath);

            if (importImmediately)
            {
                PendingImports.Remove(assetPath);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
            else
            {
                PendingImports[assetPath] = EditorApplication.timeSinceStartup;
            }
        }

        internal static void Invalidate(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                Cache.Remove(assetPath);
            }
        }

        internal static void ImportNow(string assetPath)
        {
            PendingImports.Remove(assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
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
                AssetDatabase.ImportAsset(ready[i], ImportAssetOptions.ForceUpdate);
            }
        }

        private static CacheEntry CreateCacheEntry(string fullPath, VariantSourceDocument document)
        {
            var fileInfo = new FileInfo(fullPath);
            return new CacheEntry
            {
                Document = document,
                Length = fileInfo.Exists ? fileInfo.Length : 0L,
                WriteTimeUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0L,
            };
        }

        private sealed class CacheEntry
        {
            public VariantSourceDocument Document;
            public long Length;
            public long WriteTimeUtc;
        }
    }
}
