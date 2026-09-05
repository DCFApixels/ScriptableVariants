using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Callbacks;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    [ScriptedImporter(5, VariantSourceDatabase.Extension)]
    internal sealed class ScriptableVariantImporter : ScriptedImporter
    {
        private const int MaximumParentDepth = 512;
        private static readonly Dictionary<string, Type> ScriptTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        public override void OnImportAsset(AssetImportContext context)
        {
            VariantSourceDatabase.Invalidate(context.assetPath);
            if (!TryCreateVariant(context.assetPath, out var output, out var error, context))
            {
                context.LogImportError(error);
                return;
            }
            context.AddObjectToAsset("main", output);
            context.SetMainObject(output);
        }

        // May only inspect source files/GUIDs, never load Unity objects.
        private static string[] GatherDependenciesFromSourceFile(string path)
        {
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            if (VariantSourceDatabase.TryLoadUncached(path, out var document, out _))
            {
                if (!string.IsNullOrEmpty(document.ParentGuid))
                {
                    var parent = AssetDatabase.GUIDToAssetPath(document.ParentGuid);
                    if (!string.IsNullOrEmpty(parent) && parent != path) dependencies.Add(parent);
                }
                foreach (var record in document.Values)
                    VariantValueSerializer.AddObjectDependencies(record.Value, dependency =>
                    {
                        if (dependency != path) dependencies.Add(dependency);
                    });
            }
            return dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        internal sealed class ResolutionScope
        {
            private readonly bool _useCache;
            internal ResolutionScope(bool useCache = false) { _useCache = useCache; }
            internal readonly Dictionary<string, VariantSourceDocument> Documents =
                new Dictionary<string, VariantSourceDocument>(StringComparer.Ordinal);

            internal VariantSourceDocument Read(string path)
            {
                if (Documents.TryGetValue(path, out var document)) return document;
                string error;
                var loaded = _useCache ? VariantSourceDatabase.TryLoad(path, out document, out error)
                    : VariantSourceDatabase.TryLoadUncached(path, out document, out error);
                if (!loaded)
                    throw new InvalidOperationException(error);
                document = document.Clone(); // remapping must never modify the database cache
                Documents.Add(path, document);
                return document;
            }
        }

        // Editing reads source revisions directly. Imports use the declared parent artifact, so
        // importing a long chain no longer constructs every ancestor separately for each asset.
        internal static bool TryCreateVariant(string assetPath, out ScriptableVariant output, out string error,
            AssetImportContext context = null, ResolutionScope scope = null,
            Dictionary<string, string> revisions = null)
        {
            output = null;
            ScriptableVariant defaults = null;
            try
            {
                scope = scope ?? new ResolutionScope();
                var chain = ReadChain(assetPath, scope, context, revisions);
                var document = chain[0].Value;
                if (!TryResolveVariantType(document, out var type, out var scriptPath, out var awaiting, out error))
                {
                    RegisterScriptDependency(context, document.ScriptGuid, scriptPath);
                    if (awaiting && context != null) VariantImportRetry.Schedule(assetPath);
                    return false;
                }
                RegisterScriptDependency(context, document.ScriptGuid, scriptPath);
                foreach (var entry in chain)
                {
                    var valid = TryResolveVariantType(entry.Value, out var ancestorType, out var ancestorScript, out _, out error);
                    RegisterScriptDependency(context, entry.Value.ScriptGuid, ancestorScript);
                    if (!valid) return false;
                    if (ancestorType != type) throw new InvalidOperationException("Parent and child must have the same concrete type.");
                    RemapFormerPaths(entry.Value, type);
                }

                output = ScriptableObject.CreateInstance(type) as ScriptableVariant;
                if (output == null) throw new InvalidOperationException($"Cannot create variant '{type.FullName}'.");
                ScriptableVariantAssetUtility.ValidateUnityFields(output);
                output.name = Path.GetFileNameWithoutExtension(assetPath);
                if (context != null && chain.Count > 1)
                {
                    var parentPath = chain[1].Key;
                    context.DependsOnArtifact(parentPath);
                    var parent = AssetDatabase.LoadAssetAtPath<ScriptableVariant>(parentPath);
                    if (parent == null || parent.GetType() != type)
                        throw new InvalidOperationException($"Parent artifact is unavailable: '{parentPath}'. Fix its import errors first.");
                    VariantSerialization.ApplyParent(parent, output, new HashSet<string>(StringComparer.Ordinal));
                    ApplyStoredValues(context, output, document, true, assetPath, assetPath);
                }
                else
                {
                    // Reuse one result, resetting only local fields at each layer. No recursive
                    // calls or one-SO-per-ancestor allocations are needed for source resolution.
                    var locals = VariantSerialization.GetLocalPaths(type);
                    if (chain.Count > 1 && locals.Length > 0) defaults = ScriptableObject.CreateInstance(type) as ScriptableVariant;
                    for (var i = chain.Count - 1; i >= 0; i--)
                    {
                        if (i != chain.Count - 1)
                            VariantSerialization.ResetLocalValues(defaults, output);
                        ApplyStoredValues(context, output, chain[i].Value, i != chain.Count - 1, chain[i].Key, assetPath);
                    }
                }
                VariantSerialization.ValidateLocalValues(output);
                output.name = Path.GetFileNameWithoutExtension(assetPath);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                if (output != null) UnityEngine.Object.DestroyImmediate(output);
                output = null;
                error = $"Could not resolve '{assetPath}': {exception.Message} The source has not been modified.";
                return false;
            }
            finally
            {
                if (defaults != null) UnityEngine.Object.DestroyImmediate(defaults);
            }
        }

        private static List<KeyValuePair<string, VariantSourceDocument>> ReadChain(string path,
            ResolutionScope scope, AssetImportContext context, Dictionary<string, string> revisions)
        {
            var chain = new List<KeyValuePair<string, VariantSourceDocument>>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(path))
            {
                if (chain.Count >= MaximumParentDepth) throw new InvalidOperationException("Parent chain exceeds the 512-level safety limit.");
                if (!visited.Add(path)) throw new InvalidOperationException("Scriptable Variant parent cycle detected.");
                var document = scope.Read(path);
                chain.Add(new KeyValuePair<string, VariantSourceDocument>(path, document));
                if (revisions != null) revisions[path] = document.SourceJson;
                if (string.IsNullOrEmpty(document.ParentGuid)) break;
                if (!GUID.TryParse(document.ParentGuid, out var guid) || guid.Empty())
                    throw new InvalidOperationException($"Invalid parent GUID '{document.ParentGuid}'.");
                context?.DependsOnSourceAsset(guid);
                path = AssetDatabase.GUIDToAssetPath(guid.ToString());
                if (!VariantSourceDatabase.IsVariantSourcePath(path))
                    throw new InvalidOperationException($"Parent '{document.ParentGuid}' is missing or is not a .svariant source.");
            }
            return chain;
        }

        internal static bool TryResolveVariantType(VariantSourceDocument document, out Type variantType,
            out string scriptPath, out bool awaitingCompilation, out string error)
        {
            variantType = null;
            scriptPath = null;
            awaitingCompilation = false;
            error = null;
            if (!GUID.TryParse(document.ScriptGuid, out var scriptGuid) || scriptGuid.Empty())
            { error = "Variant source has an invalid scriptGuid."; return false; }
            scriptPath = AssetDatabase.GUIDToAssetPath(scriptGuid.ToString());
            var key = document.ScriptGuid + ":" + document.TypeName;
            if (!string.IsNullOrEmpty(scriptPath) && ScriptTypes.TryGetValue(key, out variantType)) return true;
            var script = !string.IsNullOrEmpty(scriptPath) ? AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath) : null;
            if (script == null) { error = $"Script is missing: '{document.ScriptGuid}'."; return false; }
            variantType = ResolveStoredType(document.TypeName) ?? script.GetClass();
            if (variantType == null)
            { awaitingCompilation = true; error = $"Could not resolve a compiled class from script GUID '{document.ScriptGuid}'."; return false; }
            if (!typeof(ScriptableVariant).IsAssignableFrom(variantType) || variantType.IsAbstract || variantType.ContainsGenericParameters)
            { error = $"'{variantType.FullName}' must be a concrete ScriptableVariant."; return false; }
            try { VariantSerialization.GetLocalPaths(variantType); }
            catch (Exception exception) { error = exception.Message; return false; }
            if (script.GetClass() != variantType)
            {
                // Multi-class script files need Unity's type-to-MonoScript mapping. Probe only
                // once per domain, not once per ancestor and every Inspector refresh.
                var probe = ScriptableObject.CreateInstance(variantType) as ScriptableVariant;
                try
                {
                    if (probe == null || MonoScript.FromScriptableObject(probe) != script)
                    { error = $"Stored type '{variantType.FullName}' does not belong to '{document.ScriptGuid}'."; return false; }
                }
                finally { if (probe != null) UnityEngine.Object.DestroyImmediate(probe); }
            }
            ScriptTypes[key] = variantType;
            return true;
        }

        private static Type ResolveStoredType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var separator = name.IndexOf(',');
            var typeName = separator < 0 ? name : name.Substring(0, separator).Trim();
            var assemblyName = separator < 0 ? null : name.Substring(separator + 1).Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assemblyName != null && assembly.GetName().Name != assemblyName && assembly.FullName != assemblyName) continue;
                var type = assembly.GetType(typeName, false);
                if (type != null) return type;
            }
            return null; // never load an assembly named by source JSON
        }

        private static void ApplyStoredValues(AssetImportContext context, ScriptableVariant output,
            VariantSourceDocument document, bool hasParent, string documentPath, string outputPath)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            if (hasParent) expected.UnionWith(document.OverridePaths);
            else expected.UnionWith(VariantSerialization.GetRootFields(output.GetType()).Select(field => field.Name));
            expected.UnionWith(VariantSerialization.GetLocalPaths(output.GetType()));
            foreach (var path in expected)
            {
                if (!VariantSerialization.TryGetPathType(output.GetType(), path, out _))
                { context?.LogImportWarning($"Unknown stored path '{path}' is retained until explicitly removed."); continue; }
                var record = document.FindValue(path);
                if (record == null)
                {
                    if (hasParent && document.OverridePaths.Contains(path))
                        throw new InvalidOperationException($"Missing stored override '{path}'.");
                    continue; // new fields retain their constructor defaults / inherited values
                }
                VariantValueSerializer.AddObjectDependencyGuids(record.Value, guid =>
                {
                    if (context == null || AssetDatabase.AssetPathToGUID(context.assetPath) == guid.ToString()) return;
                    context.DependsOnSourceAsset(guid);
                    var dependencyPath = AssetDatabase.GUIDToAssetPath(guid.ToString());
                    if (!string.IsNullOrEmpty(dependencyPath)) context.DependsOnArtifact(dependencyPath);
                });
            }
            var values = VariantValueSerializer.ReadValues(document, output.GetType(), expected,
                documentPath == outputPath ? output : null, documentPath);
            foreach (var pair in values)
            {
                if (VariantSerialization.IsAtomicOverridePath(output.GetType(), pair.Key))
                    VariantSerialization.ValidateAtomicLocalValue(pair.Value, pair.Key);
                if (!VariantSerialization.TrySetPathValue(output, pair.Key, pair.Value))
                    throw new InvalidOperationException($"Could not assign stored property '{pair.Key}'.");
            }
        }

        internal static void RemapFormerPaths(VariantSourceDocument document, Type type)
        {
            for (var i = 0; i < document.OverridePaths.Count; i++)
                if (!VariantSerialization.IsKnownPath(type, document.OverridePaths[i]) &&
                    VariantSerialization.TryRemapFormerPath(type, document.OverridePaths[i], out var mapped))
                    document.OverridePaths[i] = mapped;
            foreach (var record in document.Values)
                if (!VariantSerialization.TryGetPathType(type, record.Path, out _) &&
                    VariantSerialization.TryRemapFormerPath(type, record.Path, out var mapped)) record.Path = mapped;
            document.Normalize();
        }

        private static void RegisterScriptDependency(AssetImportContext context, string guid, string path)
        {
            if (GUID.TryParse(guid, out var id) && !id.Empty()) context?.DependsOnSourceAsset(id);
            if (!string.IsNullOrEmpty(path)) context?.DependsOnSourceAsset(path);
        }
    }

    internal static class VariantImportRetry
    {
        private const string SessionKey = "DCFApixels.ScriptableVariants.PendingImports";
        internal static void Schedule(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsAssetImportWorkerProcess()) return;
            var pending = new HashSet<string>(SessionState.GetString(SessionKey, "").Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries));
            pending.Add(path);
            SessionState.SetString(SessionKey, string.Join("\n", pending));
        }

        [DidReloadScripts]
        private static void RetryAfterScriptsReload() => EditorApplication.delayCall += Retry;

        private static void Retry()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            { EditorApplication.delayCall += Retry; return; }
            var paths = SessionState.GetString(SessionKey, "").Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries);
            SessionState.EraseString(SessionKey);
            foreach (var path in paths)
                if (File.Exists(FileUtil.GetPhysicalPath(path))) AssetDatabase.ImportAsset(path);
        }
    }
}
