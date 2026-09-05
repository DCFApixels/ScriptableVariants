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
    [ScriptedImporter(2, VariantSourceDatabase.Extension)]
    internal sealed class ScriptableVariantImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            VariantSourceDatabase.Invalidate(context.assetPath);
            if (!TryCreateVariant(context.assetPath, out var output, out var error, context))
            {
                if (!string.IsNullOrEmpty(error))
                {
                    context.LogImportError(error);
                }

                return;
            }

            context.AddObjectToAsset("main", output);
            context.SetMainObject(output);
        }

        // Both the importer and the source inspector resolve through this path. A null context
        // creates a temporary editing object without importing or changing the published asset.
        internal static bool TryCreateVariant(
            string assetPath,
            out ScriptableVariant output,
            out string error,
            AssetImportContext context = null)
        {
            output = null;
            if (!VariantSourceDatabase.TryLoadUncached(assetPath, out var document, out error))
            {
                return false;
            }

            if (!TryResolveVariantType(
                    document,
                    out var variantType,
                    out var scriptPath,
                    out var awaitingCompilation,
                    out error))
            {
                RegisterScriptDependency(context, scriptPath);
                if (awaitingCompilation && context != null)
                {
                    VariantImportRetry.Schedule(assetPath);
                    context.LogImportWarning($"{error} The asset will be reimported after scripts reload.");
                    error = null;
                }

                return false;
            }

            RegisterScriptDependency(context, scriptPath);
            RemapFormerPaths(document, variantType);
            if (!ValidateParentChain(assetPath, document, out error))
            {
                return false;
            }

            output = ScriptableObject.CreateInstance(variantType) as ScriptableVariant;
            if (output == null)
            {
                error = $"Could not instantiate Scriptable Variant type '{variantType.FullName}'.";
                return false;
            }

            output.name = Path.GetFileNameWithoutExtension(assetPath);
            var parent = LoadParent(context, document, variantType, out error);
            if (!string.IsNullOrEmpty(error))
            {
                UnityEngine.Object.DestroyImmediate(output);
                output = null;
                return false;
            }

            var hasParent = parent != null;
            if (hasParent)
            {
                VariantSerialization.ApplyParent(parent, output, new HashSet<string>(StringComparer.Ordinal));
                UnityEngine.Object.DestroyImmediate(parent);
            }

            ApplyStoredValues(context, output, document, hasParent);
            output.name = Path.GetFileNameWithoutExtension(assetPath);
            return true;
        }

        internal static bool TryResolveVariantType(
            VariantSourceDocument document,
            out Type variantType,
            out string scriptPath,
            out bool awaitingCompilation,
            out string error)
        {
            variantType = null;
            scriptPath = null;
            awaitingCompilation = false;
            if (string.IsNullOrEmpty(document.ScriptGuid))
            {
                error = "Variant source has no scriptGuid.";
                return false;
            }

            scriptPath = AssetDatabase.GUIDToAssetPath(document.ScriptGuid);
            var script = !string.IsNullOrEmpty(scriptPath)
                ? AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath)
                : null;
            variantType = ResolveStoredType(document.TypeName) ?? (script != null ? script.GetClass() : null);
            if (variantType == null)
            {
                awaitingCompilation = script != null;
                error = $"Could not resolve a compiled class from script GUID '{document.ScriptGuid}'.";
                return false;
            }

            if (!typeof(ScriptableVariant).IsAssignableFrom(variantType) || variantType.IsAbstract ||
                variantType.ContainsGenericParameters)
            {
                error = $"'{variantType.FullName}' must be a concrete, non-generic ScriptableVariant type.";
                return false;
            }

            var probe = ScriptableObject.CreateInstance(variantType) as ScriptableVariant;
            var typeScript = probe != null ? MonoScript.FromScriptableObject(probe) : null;
            var typeScriptPath = typeScript != null ? AssetDatabase.GetAssetPath(typeScript) : null;
            if (probe != null)
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }

            if (!string.Equals(
                    AssetDatabase.AssetPathToGUID(typeScriptPath),
                    document.ScriptGuid,
                    StringComparison.Ordinal))
            {
                error = $"Stored type '{variantType.FullName}' does not belong to script GUID " +
                        $"'{document.ScriptGuid}'.";
                return false;
            }

            error = null;
            return true;
        }

        private static Type ResolveStoredType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var type = Type.GetType(typeName, false);
            if (type != null)
            {
                return type;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(typeName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static ScriptableVariant LoadParent(
            AssetImportContext context,
            VariantSourceDocument document,
            Type variantType,
            out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(document.ParentGuid))
            {
                return null;
            }

            var parentPath = AssetDatabase.GUIDToAssetPath(document.ParentGuid);
            if (!VariantSourceDatabase.IsVariantSourcePath(parentPath))
            {
                error = $"Parent GUID '{document.ParentGuid}' does not resolve to a .svariant asset.";
                return null;
            }

            context?.DependsOnSourceAsset(parentPath);
            if (!VariantSourceDatabase.TryLoadUncached(parentPath, out var parentDocument, out error))
            {
                return null;
            }

            if (!TryResolveVariantType(
                    parentDocument,
                    out var parentType,
                    out var scriptPath,
                    out var awaitingCompilation,
                    out error))
            {
                RegisterScriptDependency(context, scriptPath);
                if (awaitingCompilation && context != null)
                {
                    VariantImportRetry.Schedule(context.assetPath);
                }

                return null;
            }

            RegisterScriptDependency(context, scriptPath);
            if (parentType != variantType)
            {
                error = $"Parent type '{parentType.FullName}' does not match child type " +
                        $"'{variantType.FullName}'.";
                return null;
            }

            RemapFormerPaths(parentDocument, parentType);
            var parent = ScriptableObject.CreateInstance(parentType) as ScriptableVariant;
            if (parent == null)
            {
                error = $"Could not instantiate parent type '{parentType.FullName}'.";
                return null;
            }

            parent.name = Path.GetFileNameWithoutExtension(parentPath);
            var grandParent = LoadParent(context, parentDocument, parentType, out error);
            if (!string.IsNullOrEmpty(error))
            {
                UnityEngine.Object.DestroyImmediate(parent);
                return null;
            }

            var hasGrandParent = grandParent != null;
            if (hasGrandParent)
            {
                VariantSerialization.ApplyParent(
                    grandParent,
                    parent,
                    new HashSet<string>(StringComparer.Ordinal));
                UnityEngine.Object.DestroyImmediate(grandParent);
            }

            ApplyStoredValues(context, parent, parentDocument, hasGrandParent);
            return parent;
        }

        private static void ApplyStoredValues(
            AssetImportContext context,
            ScriptableVariant output,
            VariantSourceDocument document,
            bool hasParent)
        {
            var expectedPaths = new List<string>();
            if (hasParent)
            {
                expectedPaths.AddRange(document.OverridePaths);
            }
            else
            {
                var rootFields = VariantSerialization.GetRootFields(output.GetType());
                for (var i = 0; i < rootFields.Length; i++)
                {
                    expectedPaths.Add(rootFields[i].Name);
                }
            }

            var localPaths = VariantSerialization.GetLocalPaths(output.GetType());
            for (var i = 0; i < localPaths.Length; i++)
            {
                if (!expectedPaths.Contains(localPaths[i]))
                {
                    expectedPaths.Add(localPaths[i]);
                }
            }

            for (var i = 0; i < expectedPaths.Count; i++)
            {
                var path = expectedPaths[i];
                var record = document.FindValue(path);
                if (record == null)
                {
                    if (hasParent && document.OverridePaths.Contains(path))
                    {
                        context?.LogImportWarning(
                            $"Stored override value for '{path}' is missing; using the inherited value.");
                    }

                    continue;
                }

                if (!VariantSerialization.TryGetPathType(output.GetType(), path, out var declaredType))
                {
                    context?.LogImportWarning($"Stored property path '{path}' no longer exists on {output.GetType().Name}.");
                    continue;
                }

                try
                {
                    VariantValueSerializer.AddObjectDependencies(
                        record.Value,
                        dependencyPath => context?.DependsOnSourceAsset(dependencyPath));
                    var value = VariantValueSerializer.Deserialize(record.Value, declaredType);
                    if (!VariantSerialization.TrySetPathValue(output, path, value))
                    {
                        context?.LogImportWarning($"Could not assign stored property '{path}'.");
                    }
                }
                catch (Exception exception)
                {
                    context?.LogImportWarning($"Could not deserialize '{path}': {exception.Message}");
                }
            }
        }

        private static bool ValidateParentChain(
            string sourcePath,
            VariantSourceDocument sourceDocument,
            out string error)
        {
            var ownGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            var visited = new HashSet<string>(StringComparer.Ordinal) {ownGuid};
            var parentGuid = sourceDocument.ParentGuid;
            while (!string.IsNullOrEmpty(parentGuid))
            {
                if (!visited.Add(parentGuid))
                {
                    error = "Scriptable Variant parent cycle detected.";
                    return false;
                }

                var parentPath = AssetDatabase.GUIDToAssetPath(parentGuid);
                if (!VariantSourceDatabase.TryLoadUncached(parentPath, out var parentDocument, out var loadError))
                {
                    error = $"Could not read parent '{parentPath}': {loadError}";
                    return false;
                }

                parentGuid = parentDocument.ParentGuid;
            }

            error = null;
            return true;
        }

        private static void RemapFormerPaths(VariantSourceDocument document, Type variantType)
        {
            for (var i = 0; i < document.OverridePaths.Count; i++)
            {
                var path = document.OverridePaths[i];
                if (!VariantSerialization.IsKnownPath(variantType, path) &&
                    VariantSerialization.TryRemapFormerPath(variantType, path, out var remappedPath))
                {
                    document.OverridePaths[i] = remappedPath;
                }
            }

            for (var i = 0; i < document.Values.Count; i++)
            {
                var record = document.Values[i];
                if (!VariantSerialization.TryGetPathType(variantType, record.Path, out _) &&
                    VariantSerialization.TryRemapFormerPath(variantType, record.Path, out var remappedPath))
                {
                    record.Path = remappedPath;
                }
            }

            document.Normalize();
        }

        private static void RegisterScriptDependency(AssetImportContext context, string scriptPath)
        {
            if (!string.IsNullOrEmpty(scriptPath))
            {
                context?.DependsOnSourceAsset(scriptPath);
            }
        }
    }

    internal static class VariantImportRetry
    {
        private const string SessionKey = "DCFApixels.ScriptableVariants.PendingImports";
        private const char PathSeparator = '\n';

        internal static void Schedule(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            var pending = ReadPendingPaths();
            if (!pending.Add(assetPath))
            {
                return;
            }

            SessionState.SetString(SessionKey, string.Join(PathSeparator.ToString(), pending.OrderBy(path => path)));
        }

        [DidReloadScripts]
        private static void RetryAfterScriptsReload()
        {
            EditorApplication.delayCall -= RetryPendingImports;
            EditorApplication.delayCall += RetryPendingImports;
        }

        private static void RetryPendingImports()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall -= RetryPendingImports;
                EditorApplication.delayCall += RetryPendingImports;
                return;
            }

            var pending = ReadPendingPaths();
            SessionState.EraseString(SessionKey);
            foreach (var assetPath in pending.OrderBy(path => path))
            {
                var fullPath = FileUtil.GetPhysicalPath(assetPath);
                if (File.Exists(fullPath))
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }
        }

        private static HashSet<string> ReadPendingPaths()
        {
            var serialized = SessionState.GetString(SessionKey, string.Empty);
            return new HashSet<string>(
                serialized.Split(new[] {PathSeparator}, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
