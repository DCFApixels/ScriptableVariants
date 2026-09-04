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
        private static readonly GUIContent ParentLabel = new GUIContent("Parent");
        private static readonly GUIContent ActionsLabel = new GUIContent(
            "Actions",
            "Scriptable Variant actions");
        private static readonly GUIContent OverrideAllLabel = new GUIContent("Override All");
        private static readonly GUIContent RevertAllLabel = new GUIContent("Revert All");
        private static readonly GUIContent FlattenLabel = new GUIContent("Flatten");
        private static readonly GUIContent RemoveOrphansLabel = new GUIContent("Remove Orphan Overrides");

        private readonly GUIContent _chainLabel = new GUIContent();
        private ScriptableVariant _variant;
        private string _parentError;

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

        protected override void OnHeaderGUI()
        {
            base.OnHeaderGUI();

            if (_variant == null || targets.Length != 1)
            {
                return;
            }

            GUILayout.Space(2f);
            DrawParentField();
            DrawStatusRow();
            DrawWarnings();
            GUILayout.Space(6f);
        }

        private void DrawParentField()
        {
            EditorGUI.BeginChangeCheck();
            var newParent = EditorGUILayout.ObjectField(
                ParentLabel,
                _variant.Parent,
                _variant.GetType(),
                false) as ScriptableVariant;
            if (EditorGUI.EndChangeCheck())
            {
                if (!ScriptableVariantAssetUtility.SetParent(_variant, newParent, out var error))
                {
                    _parentError = error;
                }
                else
                {
                    _parentError = null;
                    serializedObject.Update();
                }

                Repaint();
            }
        }

        private void DrawStatusRow()
        {
            _chainLabel.text = ScriptableVariantAssetUtility.GetChainLabel(_variant);
            _chainLabel.tooltip = _chainLabel.text;
            var statusRowHeight = EditorGUIUtility.singleLineHeight + 2f;
            using (new EditorGUI.DisabledScope(!_variant.HasParent))
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(statusRowHeight)))
            {
                GUILayout.Label(
                    _chainLabel,
                    EditorStyles.miniLabel,
                    GUILayout.MinWidth(0f),
                    GUILayout.ExpandWidth(true),
                    GUILayout.Height(statusRowHeight));

                var actionsRect = GUILayoutUtility.GetRect(
                    ActionsLabel,
                    EditorStyles.popup,
                    GUILayout.Width(86f),
                    GUILayout.Height(statusRowHeight));
                if (EditorGUI.DropdownButton(
                        actionsRect,
                        ActionsLabel,
                        FocusType.Passive,
                        EditorStyles.popup))
                {
                    ShowActionsMenu(actionsRect);
                }
            }
        }

        private void DrawWarnings()
        {
            if (!string.IsNullOrEmpty(_parentError))
            {
                EditorGUILayout.HelpBox(_parentError, MessageType.Error);
            }

            var orphans = _variant.EditorGetOrphanOverrides();
            if (orphans.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    "Unknown override paths: " + string.Join(", ", orphans),
                    MessageType.Warning);
            }
        }

        public override VisualElement CreateInspectorGUI()
        {
            if (targets.Length == 1)
            {
                return base.CreateInspectorGUI();
            }

            var root = new VisualElement();
            root.Add(new HelpBox(
                "Multi-object editing is disabled for Scriptable Variants because selected assets can have different inheritance sources.",
                HelpBoxMessageType.Info));
            var disabledInspector = base.CreateInspectorGUI();
            disabledInspector.SetEnabled(false);
            root.Add(disabledInspector);
            return root;
        }

        private void ShowActionsMenu(Rect buttonRect)
        {
            var menu = new GenericMenu();
            menu.AddItem(OverrideAllLabel, false, OverrideAll);

            if (_variant.OverridePaths.Count > 0)
            {
                menu.AddItem(RevertAllLabel, false, RevertAll);
            }
            else
            {
                menu.AddDisabledItem(RevertAllLabel);
            }

            menu.AddSeparator(string.Empty);
            menu.AddItem(FlattenLabel, false, Flatten);

            menu.AddSeparator(string.Empty);
            if (_variant.EditorGetOrphanOverrides().Length > 0)
            {
                menu.AddItem(RemoveOrphansLabel, false, RemoveOrphans);
            }
            else
            {
                menu.AddDisabledItem(RemoveOrphansLabel);
            }

            menu.DropDown(buttonRect);
        }

        private void OverrideAll()
        {
            ScriptableVariantAssetUtility.OverrideAll(_variant);
            RefreshAfterHeaderAction();
        }

        private void RevertAll()
        {
            ScriptableVariantAssetUtility.RevertAll(_variant);
            RefreshAfterHeaderAction();
        }

        private void Flatten()
        {
            ScriptableVariantAssetUtility.Flatten(_variant);
            RefreshAfterHeaderAction();
        }

        private void RemoveOrphans()
        {
            ScriptableVariantAssetUtility.RemoveOrphanOverrides(_variant);
            RefreshAfterHeaderAction();
        }

        private void RefreshAfterHeaderAction()
        {
            _parentError = null;
            serializedObject.Update();
            Repaint();
        }

        private void OnUndoRedo()
        {
            if (_variant == null)
            {
                return;
            }

            _variant.EditorNotifyValuesChanged();
            _variant.EnsureResolved();
            _parentError = null;
            serializedObject.Update();
            Repaint();
        }
    }
}
