using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    public static class ScriptableVariantAssetUtility
    {
        private static readonly HashSet<Type> ValidatedSchemas = new HashSet<Type>();

        internal static void CopyEditingValues(ScriptableVariant source, ScriptableVariant destination)
        {
            EditorUtility.CopySerialized(source, destination);
            // CopySerialized preserves object references literally. A reference to the temporary
            // source must follow the stable working object, not be destroyed with the snapshot.
            using var serialized = new SerializedObject(destination);
            var iterator = serialized.GetIterator();
            var visited = new HashSet<long>();
            var enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = iterator.propertyType != SerializedPropertyType.ManagedReference ||
                    visited.Add(iterator.managedReferenceId);
                if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue == source)
                    iterator.objectReferenceValue = destination;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void ValidateUnityFields(ScriptableVariant variant)
        {
            var type = variant.GetType();
            if (ValidatedSchemas.Contains(type)) return;
            using (var serialized = new SerializedObject(variant))
            {
                var fields = VariantSerialization.GetRootFields(type);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in fields)
                {
                    if (!names.Add(field.Name))
                        throw new NotSupportedException($"Duplicate serialized field '{field.Name}' in '{type.FullName}'.");
                    if (serialized.FindProperty(field.Name) == null)
                        throw new NotSupportedException($"Field '{type.Name}.{field.Name}' is not serialized by Unity. " +
                            "Use a supported data type or mark it [NonSerialized].");
                }
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (iterator.Next(enterChildren))
                {
                    enterChildren = false;
                    if (names.Contains(iterator.name)) continue;
                    // Ignore Unity's own native metadata, but do not silently omit a user field
                    // that Unity supports and the reflection schema failed to discover.
                    for (var owner = type; owner != typeof(ScriptableVariant); owner = owner.BaseType)
                        if (owner.GetField(iterator.name, BindingFlags.Instance | BindingFlags.Public |
                                BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null)
                            throw new NotSupportedException($"Unity field '{type.Name}.{iterator.name}' has no supported variant schema.");
                }
            }
            ValidatedSchemas.Add(type);
        }
        public static ScriptableVariant EnsureResolved(ScriptableVariant variant)
        {
            if (!IsVariantAsset(variant))
            {
                return variant;
            }

            var paths = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = variant;
            while (current != null)
            {
                var path = VariantEditingSession.GetAssetPath(current);
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (!visited.Add(guid))
                {
                    break;
                }

                paths.Add(path);
                current = GetParent(current);
            }

            for (var i = paths.Count - 1; i >= 0; i--)
            {
                VariantSourceDatabase.ImportNow(paths[i]);
            }

            return AssetDatabase.LoadAssetAtPath<ScriptableVariant>(paths[0]);
        }

        internal static bool SetParent(ScriptableVariant variant, ScriptableVariant parent, out string error)
        {
            if (!CanAssignParent(variant, parent, out error) ||
                !VariantSourceDatabase.TryLoadForEdit(variant, out var document, out var assetPath, out error))
            {
                return false;
            }

            var currentParent = GetParent(variant);
            if (VariantEditingSession.GetAssetPath(currentParent) == VariantEditingSession.GetAssetPath(parent))
            {
                error = null;
                return true;
            }

            if (parent == null)
            {
                document.ParentGuid = null;
                document.OverridePaths.Clear();
                CaptureDocumentState(document, variant, false);
                VariantSourceDatabase.Save(assetPath, document);
                error = null;
                return true;
            }

            using var parentSession = VariantEditingSession.Acquire(VariantEditingSession.GetAssetPath(parent));
            parentSession.CommitValues();
            parent = parentSession.WorkingCopy;
            var differingPaths = GetDifferingOverridePaths(variant, parent);
            var overrides = new HashSet<string>(document.OverridePaths, StringComparer.Ordinal);
            for (var i = 0; i < differingPaths.Count; i++)
            {
                overrides.Add(differingPaths[i]);
            }

            document.ParentGuid = AssetDatabase.AssetPathToGUID(VariantEditingSession.GetAssetPath(parent));
            document.OverridePaths = overrides.OrderBy(path => path, StringComparer.Ordinal).ToList();
            document.Normalize();
            CaptureDocumentState(document, variant, true);

            using var edit = new WorkingEdit(variant);
            VariantSerialization.ApplyParent(parent, variant, overrides);
            VariantSourceDatabase.Save(assetPath, document);
            edit.Complete();
            error = null;
            return true;
        }

        internal static void SetOverride(ScriptableVariant variant, string propertyPath, bool enabled)
        {
            if (!enabled)
            {
                Revert(variant, propertyPath);
                return;
            }

            if (!TryGetEditableDocument(variant, propertyPath, out var document, out var assetPath) ||
                string.IsNullOrEmpty(document.ParentGuid) || IsLocallyControlled(variant, propertyPath))
            {
                return;
            }

            var prefix = propertyPath + ".";
            document.OverridePaths.RemoveAll(path => path.StartsWith(prefix, StringComparison.Ordinal));
            document.OverridePaths.Add(propertyPath);
            CaptureValue(document, variant, propertyPath);
            PruneValues(document, variant.GetType(), true);
            VariantSourceDatabase.Save(assetPath, document);
        }

        internal static void Revert(ScriptableVariant variant, string propertyPath)
        {
            if (!TryGetEditableDocument(variant, propertyPath, out var document, out var assetPath))
            {
                return;
            }

            var affected = GetOverridesAffectingSubtree(document, propertyPath);
            if (affected.Length == 0)
            {
                return;
            }

            for (var i = 0; i < affected.Length; i++)
            {
                document.OverridePaths.Remove(affected[i]);
                document.RemoveValue(affected[i]);
            }

            var parent = GetParent(variant);
            using var edit = new WorkingEdit(variant);
            if (parent != null)
            {
                using var parentSession = VariantEditingSession.Acquire(VariantEditingSession.GetAssetPath(parent));
                parentSession.CommitValues();
                VariantSerialization.ApplyParent(
                    parentSession.WorkingCopy,
                    variant,
                    new HashSet<string>(document.OverridePaths, StringComparer.Ordinal));
            }

            CaptureDocumentState(document, variant, true);
            VariantSourceDatabase.Save(assetPath, document);
            edit.Complete();
        }

        internal static bool ApplyToParent(ScriptableVariant variant, string propertyPath)
        {
            if (!TryGetEditableDocument(variant, propertyPath, out var childDocument, out var childPath))
            {
                return false;
            }

            var parent = GetParent(variant);
            if (parent == null ||
                !VariantSourceDatabase.TryLoadForEdit(parent, out var parentDocument, out var parentPath, out _))
            {
                return false;
            }

            var affected = GetOverridesAffectingSubtree(childDocument, propertyPath);
            if (affected.Length == 0)
            {
                return false;
            }

            using var parentSession = VariantEditingSession.Acquire(parentPath);
            parentSession.CommitValues();
            parent = parentSession.WorkingCopy;
            if (!VariantSourceDatabase.TryLoadForEdit(parent, out parentDocument, out parentPath, out var loadError))
                throw new InvalidOperationException(loadError);
            using var edit = new WorkingEdit(parent, variant);
            for (var i = 0; i < affected.Length; i++)
            {
                if (!VariantSerialization.CanCopyPathValue(variant, parent, affected[i]))
                {
                    return false;
                }
            }

            if (!VariantSerialization.CopyPaths(variant, parent, affected)) return false;
            var copied = new List<string>(affected);
            for (var i = 0; i < affected.Length; i++)
            {
                var path = affected[i];
                if (!string.IsNullOrEmpty(parentDocument.ParentGuid))
                {
                    AddOverridePath(parentDocument, path);
                }
            }

            if (copied.Count == 0)
            {
                return false;
            }

            CaptureDocumentState(parentDocument, parent, !string.IsNullOrEmpty(parentDocument.ParentGuid));
            for (var i = 0; i < copied.Count; i++)
            {
                childDocument.OverridePaths.Remove(copied[i]);
                childDocument.RemoveValue(copied[i]);
            }

            CaptureDocumentState(childDocument, variant, true);
            VariantSourceDatabase.SaveBatch(new[] {parentPath, childPath}, new[] {parentDocument, childDocument});
            edit.Complete();
            return true;
        }

        internal static void RevertAll(ScriptableVariant variant)
        {
            if (!VariantSourceDatabase.TryLoadForEdit(variant, out var document, out var assetPath, out _) ||
                string.IsNullOrEmpty(document.ParentGuid))
            {
                return;
            }

            document.OverridePaths.Clear();
            var parent = GetParent(variant);
            using var edit = new WorkingEdit(variant);
            if (parent != null)
            {
                using var parentSession = VariantEditingSession.Acquire(VariantEditingSession.GetAssetPath(parent));
                parentSession.CommitValues();
                VariantSerialization.ApplyParent(
                    parentSession.WorkingCopy, variant, new HashSet<string>(StringComparer.Ordinal));
            }

            CaptureDocumentState(document, variant, true);
            VariantSourceDatabase.Save(assetPath, document);
            edit.Complete();
        }

        internal static void OverrideAll(ScriptableVariant variant)
        {
            if (!VariantSourceDatabase.TryLoadForEdit(variant, out var document, out var assetPath, out _) ||
                string.IsNullOrEmpty(document.ParentGuid))
            {
                return;
            }

            document.OverridePaths = GetOverrideablePaths(variant).ToList();
            CaptureDocumentState(document, variant, true);
            VariantSourceDatabase.Save(assetPath, document);
        }

        internal static void Flatten(ScriptableVariant variant)
        {
            if (!VariantSourceDatabase.TryLoadForEdit(variant, out var document, out var assetPath, out _) ||
                string.IsNullOrEmpty(document.ParentGuid))
            {
                return;
            }

            document.ParentGuid = null;
            document.OverridePaths.Clear();
            CaptureDocumentState(document, variant, false);
            VariantSourceDatabase.Save(assetPath, document);
        }

        internal static void RemoveOrphanOverrides(ScriptableVariant variant, ScriptableVariant baseline = null)
        {
            if (!VariantSourceDatabase.TryLoadForEdit(variant, out var document, out var assetPath, out _))
            {
                return;
            }

            var orphans = GetOrphanOverrides(variant);
            for (var i = 0; i < orphans.Length; i++)
            {
                document.OverridePaths.Remove(orphans[i]);
                document.RemoveValue(orphans[i]);
            }

            if (baseline != null && !string.IsNullOrEmpty(document.ParentGuid))
                foreach (var path in GetDifferingOverridePaths(variant, baseline)) AddOverridePath(document, path);
            CaptureDocumentState(document, variant, !string.IsNullOrEmpty(document.ParentGuid));
            VariantSourceDatabase.Save(assetPath, document);
        }

        internal static void NotifyValuesChanged(ScriptableVariant variant, string propertyPath = null)
        {
            if (!VariantSourceDatabase.TryLoadForEdit(variant, out var document, out var assetPath, out _))
            {
                return;
            }

            var hasParent = !string.IsNullOrEmpty(document.ParentGuid);
            if (string.IsNullOrEmpty(propertyPath))
            {
                CaptureDocumentState(document, variant, hasParent);
                VariantSourceDatabase.Save(assetPath, document);
                return;
            }

            string storagePath;
            if (!hasParent)
            {
                storagePath = VariantSerialization.GetRootPath(propertyPath);
            }
            else
            {
                storagePath = VariantSerialization.GetLocalControllerPath(variant.GetType(), propertyPath) ??
                              GetOverrideController(document, propertyPath);
            }

            if (string.IsNullOrEmpty(storagePath))
            {
                return;
            }

            CaptureValue(document, variant, storagePath);
            VariantSourceDatabase.Save(assetPath, document);
        }

        internal static bool IsVariantAsset(ScriptableVariant variant)
        {
            return variant != null &&
                   VariantSourceDatabase.IsVariantSourcePath(VariantEditingSession.GetAssetPath(variant));
        }

        internal static void SaveWorkingCopy(ScriptableVariant variant, ScriptableVariant baseline)
        {
            if (!VariantSourceDatabase.TryLoadForEdit(variant, out var document, out var assetPath, out var error))
            {
                throw new InvalidOperationException(error);
            }

            var hasParent = !string.IsNullOrEmpty(document.ParentGuid);
            if (hasParent)
            {
                // Compare with the last accepted working values, not the parent: only actual edits
                // create overrides. Reloading inherited values must never mark them as local.
                foreach (var path in GetDifferingOverridePaths(variant, baseline))
                {
                    AddOverridePath(document, path);
                }
            }

            CaptureDocumentState(document, variant, hasParent);
            VariantSourceDatabase.Save(assetPath, document);
        }

        internal static ScriptableVariant GetParent(ScriptableVariant variant)
        {
            if (!VariantSourceDatabase.TryLoad(variant, out var document, out _, out _) ||
                string.IsNullOrEmpty(document.ParentGuid))
            {
                return null;
            }

            var parentPath = AssetDatabase.GUIDToAssetPath(document.ParentGuid);
            return AssetDatabase.LoadAssetAtPath<ScriptableVariant>(parentPath);
        }

        internal static bool HasParent(ScriptableVariant variant)
        {
            return VariantSourceDatabase.TryLoad(variant, out var document, out _, out _) &&
                !string.IsNullOrEmpty(document.ParentGuid);
        }

        internal static IReadOnlyList<string> GetOverridePaths(ScriptableVariant variant)
        {
            return VariantSourceDatabase.TryLoad(variant, out var document, out _, out _)
                ? (IReadOnlyList<string>)document.OverridePaths
                : Array.Empty<string>();
        }

        internal static bool IsOverridden(ScriptableVariant variant, string propertyPath)
        {
            return VariantSourceDatabase.TryLoad(variant, out var document, out _, out _) &&
                   document.OverridePaths.Contains(propertyPath);
        }

        internal static bool IsLocallyControlled(ScriptableVariant variant, string propertyPath)
        {
            if (variant == null || string.IsNullOrEmpty(propertyPath))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(VariantSerialization.GetLocalControllerPath(variant.GetType(), propertyPath)))
            {
                return true;
            }

            return VariantSourceDatabase.TryLoad(variant, out var document, out _, out _) &&
                   !string.IsNullOrEmpty(GetOverrideController(document, propertyPath));
        }

        internal static bool HasOverridesBelow(ScriptableVariant variant, string propertyPath)
        {
            if (!VariantSourceDatabase.TryLoad(variant, out var document, out _, out _))
            {
                return false;
            }

            var prefix = propertyPath + ".";
            return document.OverridePaths.Any(path => path.StartsWith(prefix, StringComparison.Ordinal));
        }

        internal static ScriptableVariant GetValueSource(ScriptableVariant variant, string propertyPath)
        {
            if (variant == null)
            {
                return null;
            }

            var visited = new HashSet<ScriptableVariant>();
            for (var current = variant; current != null && visited.Add(current); current = GetParent(current))
            {
                if (current == variant &&
                    !string.IsNullOrEmpty(VariantSerialization.GetLocalControllerPath(current.GetType(), propertyPath)))
                {
                    return current;
                }

                if (!VariantSourceDatabase.TryLoad(current, out var document, out _, out _))
                {
                    return current;
                }

                if (string.IsNullOrEmpty(document.ParentGuid) ||
                    !string.IsNullOrEmpty(GetOverrideController(document, propertyPath)))
                {
                    return current;
                }
            }

            return null;
        }

        internal static string[] GetOverridesAffectingSubtree(
            ScriptableVariant variant,
            string propertyPath)
        {
            return VariantSourceDatabase.TryLoad(variant, out var document, out _, out _)
                ? GetOverridesAffectingSubtree(document, propertyPath)
                : Array.Empty<string>();
        }

        internal static string[] GetOrphanOverrides(ScriptableVariant variant)
        {
            if (variant == null || !VariantSourceDatabase.TryLoad(variant, out var document, out _, out _))
            {
                return Array.Empty<string>();
            }

            return document.OverridePaths.Where(path => !VariantSerialization.IsKnownPath(variant.GetType(), path))
                .Concat(document.Values.Where(record => !VariantSerialization.TryGetPathType(variant.GetType(), record.Path, out _))
                    .Select(record => record.Path)).Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        internal static string GetChainLabel(ScriptableVariant variant)
        {
            var names = new List<string>();
            var visited = new HashSet<ScriptableVariant>();
            for (var current = variant; current != null && visited.Add(current); current = GetParent(current))
            {
                names.Add(current.name);
            }

            names.Reverse();
            return string.Join("  →  ", names);
        }

        internal static ScriptableVariant CreateRoot(Type variantType, string path)
        {
            var document = CreateDocument(variantType, out var defaults);
            try { CaptureDocumentState(document, defaults, false); }
            finally { UnityEngine.Object.DestroyImmediate(defaults); }
            return SaveNewAsset(path, document);
        }

        internal static bool CanAssignParent(
            ScriptableVariant variant,
            ScriptableVariant parent,
            out string error)
        {
            if (variant == null || !IsVariantAsset(variant))
            {
                error = "Child must be an imported .svariant asset.";
                return false;
            }

            if (parent == null)
            {
                error = null;
                return true;
            }

            if (!IsVariantAsset(parent))
            {
                error = "Parent must be an imported .svariant asset.";
                return false;
            }

            if (variant.GetType() != parent.GetType())
            {
                error = "Parent and child must have exactly the same concrete type.";
                return false;
            }

            var ownGuid = AssetDatabase.AssetPathToGUID(VariantEditingSession.GetAssetPath(variant));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = parent;
            while (current != null)
            {
                var path = VariantEditingSession.GetAssetPath(current);
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.Equals(guid, ownGuid, StringComparison.Ordinal))
                {
                    error = "Assigning this parent would create a cycle.";
                    return false;
                }

                if (!visited.Add(guid))
                {
                    error = "The selected parent already contains a cycle.";
                    return false;
                }

                current = GetParent(current);
            }

            error = null;
            return true;
        }

        private static bool TryGetEditableDocument(
            ScriptableVariant variant,
            string propertyPath,
            out VariantSourceDocument document,
            out string assetPath)
        {
            if (variant == null || string.IsNullOrEmpty(propertyPath) ||
                !VariantSerialization.IsKnownPath(variant.GetType(), propertyPath) ||
                !VariantSourceDatabase.TryLoadForEdit(variant, out document, out assetPath, out _))
            {
                document = null;
                assetPath = null;
                return false;
            }

            return true;
        }

        private static VariantSourceDocument CreateDocument(Type variantType, out ScriptableVariant defaults)
        {
            if (variantType == null || !typeof(ScriptableVariant).IsAssignableFrom(variantType) ||
                variantType.IsAbstract || variantType.ContainsGenericParameters)
            {
                throw new ArgumentException("Type must be a concrete ScriptableVariant.", nameof(variantType));
            }

            VariantSerialization.GetLocalPaths(variantType);
            defaults = ScriptableObject.CreateInstance(variantType) as ScriptableVariant;
            var script = defaults != null ? MonoScript.FromScriptableObject(defaults) : null;
            var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            var scriptGuid = !string.IsNullOrEmpty(scriptPath) ? AssetDatabase.AssetPathToGUID(scriptPath) : null;
            if (string.IsNullOrEmpty(scriptGuid))
            {
                if (defaults != null)
                {
                    UnityEngine.Object.DestroyImmediate(defaults);
                }

                throw new InvalidOperationException($"Could not find the MonoScript for '{variantType.FullName}'.");
            }

            return new VariantSourceDocument
            {
                ScriptGuid = scriptGuid,
                TypeName = variantType.FullName + ", " + variantType.Assembly.GetName().Name,
            };
        }

        private static ScriptableVariant SaveNewAsset(string path, VariantSourceDocument document)
        {
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            VariantSourceDatabase.Save(path, document, true);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableVariant>(path);
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            return asset;
        }

        private static void CaptureDocumentState(
            VariantSourceDocument document,
            ScriptableVariant variant,
            bool hasParent)
        {
            ValidateUnityFields(variant);
            foreach (var record in document.Values)
                if (!VariantSerialization.TryGetPathType(variant.GetType(), record.Path, out _))
                    throw new InvalidOperationException($"Unknown stored field '{record.Path}' was retained. " +
                        "Restore its declaration or explicitly remove orphan data before saving values.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (hasParent)
            {
                paths.UnionWith(document.OverridePaths);
                paths.UnionWith(VariantSerialization.GetLocalPaths(variant.GetType()));
            }
            else
            {
                var fields = VariantSerialization.GetRootFields(variant.GetType());
                for (var i = 0; i < fields.Length; i++)
                {
                    paths.Add(fields[i].Name);
                }
            }

            // A whole inline override already owns its nested local values. Avoid overlapping
            // records, which would deserialize the same managed reference graph twice.
            var owned = new List<string>();
            foreach (var path in paths)
                for (var dot = path.LastIndexOf('.'); dot > 0; dot = path.LastIndexOf('.', dot - 1))
                    if (paths.Contains(path.Substring(0, dot))) { owned.Add(path); break; }
            paths.ExceptWith(owned);
            VariantValueSerializer.CaptureValues(document, variant, paths);
        }

        private static void CaptureValue(
            VariantSourceDocument document,
            ScriptableVariant variant,
            string propertyPath)
        {
            // Updating an isolated token could orphan shared $id/$ref entries.
            CaptureDocumentState(document, variant, !string.IsNullOrEmpty(document.ParentGuid));
        }

        private static void PruneValues(VariantSourceDocument document, Type variantType, bool hasParent)
        {
            var retained = new HashSet<string>(StringComparer.Ordinal);
            if (hasParent)
            {
                retained.UnionWith(document.OverridePaths);
                retained.UnionWith(VariantSerialization.GetLocalPaths(variantType));
            }
            else
            {
                var fields = VariantSerialization.GetRootFields(variantType);
                for (var i = 0; i < fields.Length; i++)
                {
                    retained.Add(fields[i].Name);
                }
            }

            document.Values.RemoveAll(record => !retained.Contains(record.Path) &&
                VariantSerialization.TryGetPathType(variantType, record.Path, out _));
        }

        private static void AddOverridePath(VariantSourceDocument document, string propertyPath)
        {
            var controller = GetOverrideController(document, propertyPath);
            if (!string.IsNullOrEmpty(controller))
            {
                return;
            }

            var prefix = propertyPath + ".";
            document.OverridePaths.RemoveAll(path => path.StartsWith(prefix, StringComparison.Ordinal));
            document.OverridePaths.Add(propertyPath);
        }

        private static string GetOverrideController(VariantSourceDocument document, string propertyPath)
        {
            if (document.OverridePaths.Contains(propertyPath))
            {
                return propertyPath;
            }

            for (var separator = propertyPath.LastIndexOf('.'); separator > 0;
                 separator = propertyPath.LastIndexOf('.', separator - 1))
            {
                var candidate = propertyPath.Substring(0, separator);
                if (document.OverridePaths.Contains(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string[] GetOverridesAffectingSubtree(
            VariantSourceDocument document,
            string propertyPath)
        {
            var prefix = propertyPath + ".";
            var subtree = document.OverridePaths
                .Where(path => string.Equals(path, propertyPath, StringComparison.Ordinal) ||
                               path.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            if (subtree.Length > 0)
            {
                return subtree;
            }

            var controller = GetOverrideController(document, propertyPath);
            return string.IsNullOrEmpty(controller) ? Array.Empty<string>() : new[] {controller};
        }

        private static List<string> GetDifferingOverridePaths(
            ScriptableVariant variant,
            ScriptableVariant parent)
        {
            var result = new List<string>();
            var variantType = variant.GetType();
            using (var childObject = new SerializedObject(variant))
            using (var parentObject = new SerializedObject(parent))
            {
                childObject.Update();
                parentObject.Update();
                var childProperty = childObject.GetIterator();
                var enterChildren = true;
                while (childProperty.Next(enterChildren))
                {
                    enterChildren = true;
                    var propertyPath = childProperty.propertyPath;
                    if (!VariantSerialization.IsKnownPath(variantType, propertyPath))
                    {
                        enterChildren = false; // includes local managed graphs, which may be cyclic
                        continue;
                    }

                    var parentProperty = parentObject.FindProperty(propertyPath);
                    if (parentProperty == null)
                    {
                        continue;
                    }

                    if (SerializedProperty.DataEquals(childProperty, parentProperty))
                    {
                        enterChildren = false;
                        continue;
                    }

                    if (VariantSerialization.IsAtomicOverridePath(variantType, propertyPath) ||
                        !childProperty.hasChildren)
                    {
                        result.Add(propertyPath);
                        enterChildren = false;
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> GetOverrideablePaths(ScriptableVariant variant)
        {
            var type = variant.GetType();
            using (var serializedObject = new SerializedObject(variant))
            {
                serializedObject.Update();
                var property = serializedObject.GetIterator();
                var enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (!VariantSerialization.IsKnownPath(type, property.propertyPath))
                    {
                        enterChildren = false;
                        continue;
                    }

                    if (VariantSerialization.IsAtomicOverridePath(type, property.propertyPath) ||
                        !property.hasChildren)
                    {
                        yield return property.propertyPath;
                        enterChildren = false;
                    }
                }
            }
        }

        private sealed class WorkingEdit : IDisposable
        {
            private readonly ScriptableVariant[] _targets;
            private readonly ScriptableVariant[] _before;
            private bool _complete;

            internal WorkingEdit(params ScriptableVariant[] targets)
            {
                _targets = targets;
                _before = new ScriptableVariant[targets.Length];
                try
                {
                    for (var i = 0; i < targets.Length; i++)
                    {
                        _before[i] = ScriptableObject.CreateInstance(targets[i].GetType()) as ScriptableVariant;
                        CopyEditingValues(targets[i], _before[i]);
                        _before[i].hideFlags = HideFlags.HideAndDontSave;
                    }
                }
                catch { _complete = true; Dispose(); throw; }
            }

            internal void Complete() { _complete = true; }
            public void Dispose()
            {
                Exception failure = null;
                for (var i = 0; i < _before.Length; i++)
                {
                    if (_before[i] == null) continue;
                    try
                    {
                        if (!_complete && _targets[i] != null)
                        {
                            var flags = _targets[i].hideFlags;
                            CopyEditingValues(_before[i], _targets[i]);
                            _targets[i].hideFlags = flags;
                        }
                    }
                    catch (Exception exception) { failure = failure ?? exception; }
                    finally { UnityEngine.Object.DestroyImmediate(_before[i]); }
                }
                if (failure != null) throw new InvalidOperationException("Could not restore an in-memory variant edit.", failure);
            }
        }
    }
}
