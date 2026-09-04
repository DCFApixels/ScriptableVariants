using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    public static class ScriptableVariantAssetUtility
    {
        public static bool SetParent(ScriptableVariant variant, ScriptableVariant parent, out string error)
        {
            if (variant == null)
            {
                error = "Variant is null.";
                return false;
            }

            if (!variant.CanAssignParent(parent, out error))
            {
                return false;
            }

            Undo.RecordObject(variant, "Change Scriptable Variant Parent");
            variant.EditorSetParent(parent);
            MarkChanged(variant);
            return true;
        }

        public static void SetOverride(ScriptableVariant variant, string propertyPath, bool enabled)
        {
            if (variant == null || string.IsNullOrEmpty(propertyPath))
            {
                return;
            }

            Undo.RecordObject(variant, enabled ? "Override Variant Property" : "Revert Variant Property");
            variant.EditorSetOverride(propertyPath, enabled);
            MarkChanged(variant);
        }

        public static void RevertAll(ScriptableVariant variant)
        {
            if (variant == null)
            {
                return;
            }

            Undo.RecordObject(variant, "Revert All Variant Overrides");
            variant.EditorClearOverrides();
            MarkChanged(variant);
        }

        public static void OverrideAll(ScriptableVariant variant)
        {
            if (variant == null)
            {
                return;
            }

            Undo.RecordObject(variant, "Override All Variant Properties");
            variant.EditorOverrideAll();
            MarkChanged(variant);
        }

        public static void Flatten(ScriptableVariant variant)
        {
            if (variant == null)
            {
                return;
            }

            Undo.RecordObject(variant, "Flatten Scriptable Variant");
            variant.EditorFlatten();
            MarkChanged(variant);
        }

        public static void RemoveOrphanOverrides(ScriptableVariant variant)
        {
            if (variant == null)
            {
                return;
            }

            Undo.RecordObject(variant, "Remove Orphan Variant Overrides");
            variant.EditorRemoveOrphanOverrides();
            MarkChanged(variant);
        }

        public static void NotifyValuesChanged(ScriptableVariant variant)
        {
            if (variant == null)
            {
                return;
            }

            variant.EditorNotifyValuesChanged();
            variant.EnsureResolved();
            EditorUtility.SetDirty(variant);
        }

        public static ScriptableVariant CreateChild(ScriptableVariant parent)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            parent.EnsureResolved();

            var parentPath = AssetDatabase.GetAssetPath(parent);
            var directory = string.IsNullOrEmpty(parentPath)
                ? "Assets"
                : Path.GetDirectoryName(parentPath)?.Replace('\\', '/');
            var defaultName = parent.name + " Child.asset";
            var path = EditorUtility.SaveFilePanelInProject(
                "Create Scriptable Variant Child",
                defaultName,
                "asset",
                "Choose where to create the child variant.",
                directory);

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var child = ScriptableObject.CreateInstance(parent.GetType()) as ScriptableVariant;
            if (child == null)
            {
                throw new InvalidOperationException($"Could not instantiate {parent.GetType().FullName}.");
            }

            child.name = Path.GetFileNameWithoutExtension(path);
            child.EditorSetParent(parent);
            AssetDatabase.CreateAsset(child, AssetDatabase.GenerateUniqueAssetPath(path));
            AssetDatabase.SaveAssetIfDirty(child);
            Selection.activeObject = child;
            EditorGUIUtility.PingObject(child);
            return child;
        }

        public static string GetChainLabel(ScriptableVariant variant)
        {
            if (variant == null)
            {
                return string.Empty;
            }

            var names = new List<string>();
            var visited = new HashSet<ScriptableVariant>();
            for (var current = variant; current != null && visited.Add(current); current = current.Parent)
            {
                names.Add(current.name);
            }

            names.Reverse();
            return string.Join("  →  ", names);
        }

        private static void MarkChanged(ScriptableVariant variant)
        {
            EditorUtility.SetDirty(variant);
            variant.EnsureResolved();
        }
    }
}
