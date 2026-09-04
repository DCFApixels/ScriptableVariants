using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.Editor
{
    [InitializeOnLoad]
    internal static class ScriptableVariantContextMenu
    {
        static ScriptableVariantContextMenu()
        {
            EditorApplication.contextualPropertyMenu -= PopulatePropertyMenu;
            EditorApplication.contextualPropertyMenu += PopulatePropertyMenu;
        }

        internal static void Populate(
            DropdownMenu menu,
            ScriptableVariant variant,
            string propertyPath,
            Action afterChange = null)
        {
            if (!CanHandle(variant, propertyPath))
            {
                return;
            }

            if (variant.EditorGetOverridesAffectingSubtree(propertyPath).Length == 0)
            {
                menu.AppendAction(
                    "Override Property",
                    _ => Execute(
                        () => ScriptableVariantAssetUtility.SetOverride(variant, propertyPath, true),
                        afterChange),
                    DropdownMenuAction.AlwaysEnabled);
                return;
            }

            menu.AppendAction(
                "Apply to Parent",
                _ => Execute(
                    () => ScriptableVariantAssetUtility.ApplyToParent(variant, propertyPath),
                    afterChange),
                DropdownMenuAction.AlwaysEnabled);
            menu.AppendAction(
                "Revert",
                _ => Execute(
                    () => ScriptableVariantAssetUtility.Revert(variant, propertyPath),
                    afterChange),
                DropdownMenuAction.AlwaysEnabled);
        }

        private static void PopulatePropertyMenu(GenericMenu menu, SerializedProperty property)
        {
            if (property == null || property.serializedObject == null ||
                property.serializedObject.isEditingMultipleObjects ||
                !(property.serializedObject.targetObject is ScriptableVariant variant) ||
                !CanHandle(variant, property.propertyPath))
            {
                RemoveTrailingSeparator(menu);
                return;
            }

            var propertyPath = property.propertyPath;
            if (variant.EditorGetOverridesAffectingSubtree(propertyPath).Length == 0)
            {
                menu.AddItem(
                    new GUIContent("Override Property"),
                    false,
                    () => Execute(
                        () => ScriptableVariantAssetUtility.SetOverride(variant, propertyPath, true),
                        null));
                return;
            }

            menu.AddItem(
                new GUIContent("Apply to Parent"),
                false,
                () => Execute(
                    () => ScriptableVariantAssetUtility.ApplyToParent(variant, propertyPath),
                    null));
            menu.AddItem(
                new GUIContent("Revert"),
                false,
                () => Execute(
                    () => ScriptableVariantAssetUtility.Revert(variant, propertyPath),
                    null));
        }

        private static bool CanHandle(ScriptableVariant variant, string propertyPath)
        {
            return variant != null && variant.HasParent &&
                   VariantSerialization.IsKnownPath(variant.GetType(), propertyPath);
        }

        private static void RemoveTrailingSeparator(GenericMenu menu)
        {
            var itemsField = typeof(GenericMenu).GetField(
                "m_MenuItems",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(itemsField?.GetValue(menu) is IList items) || items.Count == 0)
            {
                return;
            }

            var lastItem = items[items.Count - 1];
            var separatorField = lastItem.GetType().GetField("separator");
            if (separatorField != null && (bool)separatorField.GetValue(lastItem))
            {
                items.RemoveAt(items.Count - 1);
            }
        }

        private static void Execute(Action action, Action afterChange)
        {
            action();
            afterChange?.Invoke();
            InternalEditorUtility.RepaintAllViews();
        }
    }
}
