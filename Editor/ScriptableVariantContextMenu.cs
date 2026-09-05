using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.Editor
{
    [InitializeOnLoad]
    internal static class ScriptableVariantContextMenu
    {
        private static readonly GUIContent OverridePropertyLabel = new GUIContent("Override Property");
        private static readonly GUIContent ApplyToParentLabel = new GUIContent("Apply to Parent");
        private static readonly GUIContent RevertLabel = new GUIContent("Revert");

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

            if (ScriptableVariantAssetUtility.GetOverridesAffectingSubtree(variant, propertyPath).Length == 0)
            {
                menu.AppendAction(
                    "Override Property",
                    _ => Execute(variant,
                        () => ScriptableVariantAssetUtility.SetOverride(variant, propertyPath, true),
                        afterChange),
                    DropdownMenuAction.AlwaysEnabled);
                return;
            }

            menu.AppendAction(
                "Apply to Parent",
                _ => Execute(variant,
                    () => ApplyToParent(variant, propertyPath),
                    afterChange),
                DropdownMenuAction.AlwaysEnabled);
            menu.AppendAction(
                "Revert",
                _ => Execute(variant,
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
                return;
            }

            var propertyPath = property.propertyPath;
            if (ScriptableVariantAssetUtility.GetOverridesAffectingSubtree(variant, propertyPath).Length == 0)
            {
                menu.AddItem(
                    OverridePropertyLabel,
                    false,
                    () => Execute(variant,
                        () => ScriptableVariantAssetUtility.SetOverride(variant, propertyPath, true),
                        null));
                return;
            }

            menu.AddItem(
                ApplyToParentLabel,
                false,
                () => Execute(variant,
                    () => ApplyToParent(variant, propertyPath),
                    null));
            menu.AddItem(
                RevertLabel,
                false,
                () => Execute(variant,
                    () => ScriptableVariantAssetUtility.Revert(variant, propertyPath),
                    null));
        }

        private static bool CanHandle(ScriptableVariant variant, string propertyPath)
        {
            return VariantEditingSession.IsWorkingCopy(variant) &&
                   ScriptableVariantAssetUtility.HasParent(variant) &&
                   VariantSerialization.IsKnownPath(variant.GetType(), propertyPath);
        }

        private static void ApplyToParent(ScriptableVariant variant, string propertyPath)
        {
            if (!ScriptableVariantAssetUtility.ApplyToParent(variant, propertyPath))
                throw new InvalidOperationException("Could not apply this override. Check that the parent is available and the field still exists.");
        }

        private static void Execute(ScriptableVariant variant, Action action, Action afterChange)
        {
            try
            {
                VariantEditingSession.CommitValues(variant);
                action();
                afterChange?.Invoke();
            }
            catch (Exception exception)
            {
                if (VariantEditingSession.TryGetSession(variant, out var session)) session.ReportError(exception.Message);
                else Debug.LogError(exception.Message);
            }
            InternalEditorUtility.RepaintAllViews();
        }
    }
}
