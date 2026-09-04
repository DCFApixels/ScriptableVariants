using DCFApixels.ScriptableVariants.Editor;
using TriInspector.Editors;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(ScriptableVariant), editorForChildClasses: true)]
    internal sealed class ScriptableVariantTriEditor : TriEditor
    {
        private ScriptableVariant _variant;

        protected override void OnEnable()
        {
            _variant = target as ScriptableVariant;
            if (_variant != null)
            {
                _variant.EnsureResolved();
            }

            Undo.undoRedoPerformed += OnUndoRedo;
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            base.OnDisable();
        }

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            if (targets.Length != 1)
            {
                root.Add(new HelpBox(
                    "Multi-object editing is disabled for Scriptable Variants because selected assets can have different inheritance sources.",
                    HelpBoxMessageType.Info));
                var disabledInspector = base.CreateInspectorGUI();
                disabledInspector.SetEnabled(false);
                root.Add(disabledInspector);
                return root;
            }

            root.Add(CreateHeader());
            root.Add(base.CreateInspectorGUI());
            return root;
        }

        private VisualElement CreateHeader()
        {
            var container = new VisualElement();
            container.style.marginBottom = 6;
            container.style.paddingLeft = 4;
            container.style.paddingRight = 4;
            container.style.paddingTop = 4;
            container.style.paddingBottom = 4;
            container.style.borderBottomWidth = 1;
            container.style.borderTopWidth = 1;
            container.style.borderLeftWidth = 1;
            container.style.borderRightWidth = 1;

            var parentField = new ObjectField("Parent")
            {
                objectType = _variant.GetType(),
                allowSceneObjects = false,
            };
            parentField.SetValueWithoutNotify(_variant.Parent);
            container.Add(parentField);

            var statusRow = new Toolbar();
            statusRow.style.marginTop = 2;

            var chainLabel = new Label();
            chainLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            chainLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            chainLabel.style.flexGrow = 1;
            chainLabel.style.flexShrink = 1;
            chainLabel.style.paddingLeft = 4;
            statusRow.Add(chainLabel);

            var actionsMenu = new ToolbarMenu
            {
                text = "Actions",
                tooltip = "Scriptable Variant actions",
                variant = ToolbarMenu.Variant.Popup,
            };
            statusRow.Add(actionsMenu);
            container.Add(statusRow);

            var errorBox = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            errorBox.style.display = DisplayStyle.None;
            container.Add(errorBox);

            var orphanBox = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            orphanBox.style.display = DisplayStyle.None;
            container.Add(orphanBox);

            actionsMenu.menu.AppendAction("Override All", _ =>
            {
                ScriptableVariantAssetUtility.OverrideAll(_variant);
                serializedObject.Update();
            }, _ => _variant != null && _variant.HasParent
                ? DropdownMenuAction.Status.Normal
                : DropdownMenuAction.Status.Disabled);
            actionsMenu.menu.AppendAction("Revert All", _ =>
            {
                ScriptableVariantAssetUtility.RevertAll(_variant);
                serializedObject.Update();
            }, _ => _variant != null && _variant.HasParent && _variant.OverridePaths.Count > 0
                ? DropdownMenuAction.Status.Normal
                : DropdownMenuAction.Status.Disabled);
            actionsMenu.menu.AppendSeparator();
            actionsMenu.menu.AppendAction("Flatten", _ =>
            {
                ScriptableVariantAssetUtility.Flatten(_variant);
                parentField.SetValueWithoutNotify(null);
                serializedObject.Update();
            }, _ => _variant != null && _variant.HasParent
                ? DropdownMenuAction.Status.Normal
                : DropdownMenuAction.Status.Disabled);
            actionsMenu.menu.AppendSeparator();
            actionsMenu.menu.AppendAction("Remove Orphan Overrides", _ =>
            {
                ScriptableVariantAssetUtility.RemoveOrphanOverrides(_variant);
                serializedObject.Update();
            }, _ => _variant != null && _variant.EditorGetOrphanOverrides().Length > 0
                ? DropdownMenuAction.Status.Normal
                : DropdownMenuAction.Status.Disabled);

            parentField.RegisterValueChangedCallback(evt =>
            {
                var newParent = evt.newValue as ScriptableVariant;
                if (!ScriptableVariantAssetUtility.SetParent(_variant, newParent, out var error))
                {
                    parentField.SetValueWithoutNotify(_variant.Parent);
                    errorBox.text = error;
                    errorBox.style.display = DisplayStyle.Flex;
                    return;
                }

                errorBox.style.display = DisplayStyle.None;
                serializedObject.Update();
            });

            void RefreshHeader()
            {
                if (_variant == null)
                {
                    return;
                }

                if (parentField.value != _variant.Parent)
                {
                    parentField.SetValueWithoutNotify(_variant.Parent);
                }

                statusRow.SetEnabled(_variant.HasParent);
                chainLabel.text = ScriptableVariantAssetUtility.GetChainLabel(_variant);

                var orphans = _variant.EditorGetOrphanOverrides();
                var hasOrphans = orphans.Length > 0;
                orphanBox.text = hasOrphans
                    ? "Unknown override paths: " + string.Join(", ", orphans)
                    : string.Empty;
                orphanBox.style.display = hasOrphans ? DisplayStyle.Flex : DisplayStyle.None;
            }

            container.schedule.Execute(RefreshHeader).Every(100);
            RefreshHeader();
            return container;
        }

        private void OnUndoRedo()
        {
            if (_variant == null)
            {
                return;
            }

            _variant.EditorNotifyValuesChanged();
            _variant.EnsureResolved();
            serializedObject.Update();
            Repaint();
        }
    }
}
