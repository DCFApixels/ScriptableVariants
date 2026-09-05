using System;
using DCFApixels.ScriptableVariants.Editor;
using TriInspector;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(ScriptableVariantImporter))]
    internal sealed class ScriptableVariantTriEditor : ScriptedImporterEditor
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
        private VariantEditingSession _session;
        private VariantWorkingCopyEditor _workingEditor;
        private SerializedObject _workingObject;
        private TriPropertyTreeForSerializedObject _tree;
        private VisualElement _root;
        private IVisualElementScheduledItem _update;
        private bool _refreshing;

        public override bool showImportedObject => false;
        protected override bool needsApplyRevert => false;

        public override void OnEnable()
        {
            base.OnEnable();
            if (target == null || targets.Length != 1)
            {
                return;
            }

            try
            {
                _session = VariantEditingSession.Acquire(((AssetImporter)target).assetPath);
                _variant = _session.WorkingCopy;
                _workingEditor = (VariantWorkingCopyEditor)CreateEditor(_variant, typeof(VariantWorkingCopyEditor));
                _workingEditor.CreateView = CreateWorkingInspectorGUI;
                _workingObject = _workingEditor.serializedObject;
                _session.Reloaded += RefreshAfterHeaderAction;
                Undo.undoRedoPerformed += RefreshAfterHeaderAction;
            }
            catch (Exception exception)
            {
                _parentError = exception.Message;
            }
        }

        public override void OnDisable()
        {
            Undo.undoRedoPerformed -= RefreshAfterHeaderAction;
            if (_variant != null)
            {
                try
                {
                    // Native bindings may not have delivered their change event before selection changes.
                    _tree?.ApplyChanges();
                    _session?.CommitValues();
                }
                catch (Exception exception)
                {
                    Debug.LogError($"Could not save variant source '{_session?.AssetPath}': {exception.Message}");
                }
            }

            DisposeTree();
            if (_workingEditor != null)
            {
                DestroyImmediate(_workingEditor);
                _workingEditor = null;
            }

            _workingObject = null;
            if (_session != null)
            {
                _session.Reloaded -= RefreshAfterHeaderAction;
                _session.Dispose();
                _session = null;
            }

            _variant = null;
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
                ScriptableVariantAssetUtility.GetParent(_variant),
                _variant.GetType(),
                false) as ScriptableVariant;
            if (EditorGUI.EndChangeCheck())
            {
                RunEdit(() =>
                {
                    if (!ScriptableVariantAssetUtility.SetParent(_variant, newParent, out var error))
                    {
                        throw new InvalidOperationException(error);
                    }
                });
            }
        }

        private void DrawStatusRow()
        {
            _chainLabel.text = ScriptableVariantAssetUtility.GetChainLabel(_variant);
            _chainLabel.tooltip = _chainLabel.text;
            var statusRowHeight = EditorGUIUtility.singleLineHeight + 2f;
            using (new EditorGUI.DisabledScope(!ScriptableVariantAssetUtility.HasParent(_variant)))
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

            var orphans = ScriptableVariantAssetUtility.GetOrphanOverrides(_variant);
            if (orphans.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    "Unknown override paths: " + string.Join(", ", orphans),
                    MessageType.Warning);
            }
        }

        public override VisualElement CreateInspectorGUI()
        {
            if (targets.Length != 1)
            {
                return new HelpBox("Select one Scriptable Variant to edit its source.", HelpBoxMessageType.Info);
            }

            if (_session == null)
            {
                return new HelpBox(_parentError ?? "Could not load the variant source.", HelpBoxMessageType.Error);
            }

            // InspectorElement is Unity's supported binding boundary for a nested editor with a
            // different target. The outer importer must not rebind these fields to its own data.
            var inspector = new InspectorElement(_workingEditor);
            inspector.style.paddingLeft = 0;
            inspector.style.paddingRight = 0;
            return inspector;
        }

        private VisualElement CreateWorkingInspectorGUI()
        {
            DisposeTree();
            _root = new VisualElement();
            _tree = new TriPropertyTreeForSerializedObject(_workingObject);
            _tree.Update(true);
            _tree.RunValidation();
            _tree.RootProperty.ChildValueChanged += OnValuesChanged;

            if (!_tree.RootProperty.TryGetAttribute(out HideMonoScriptAttribute _))
            {
                var script = new PropertyField(_workingObject.FindProperty("m_Script"));
                script.Bind(_workingObject);
                script.SetEnabled(false);
                _root.Add(script);
            }

            _root.Add(_tree.GetRootElement());
            _root.TrackSerializedObjectValue(_workingObject, _ => OnValuesChanged(null));
            _update = _root.schedule.Execute(() =>
            {
                _tree.Update();
                _tree.RunValidationIfRequired();
            }).Every(100);
            return _root;
        }

        private void OnValuesChanged(TriProperty property)
        {
            if (_refreshing || _session == null)
            {
                return;
            }

            try
            {
                _session.CommitValues();
                _parentError = null;
            }
            catch (Exception exception)
            {
                _parentError = exception.Message;
            }

            Repaint();
        }

        private void DisposeTree()
        {
            _update?.Pause();
            _update = null;
            _root?.Unbind();
            _root?.Clear();
            _root = null;
            if (_tree != null)
            {
                _tree.RootProperty.ChildValueChanged -= OnValuesChanged;
                _tree.Dispose();
                _tree = null;
            }
        }

        private void ShowActionsMenu(Rect buttonRect)
        {
            var menu = new GenericMenu();
            menu.AddItem(OverrideAllLabel, false, OverrideAll);

            if (ScriptableVariantAssetUtility.GetOverridePaths(_variant).Count > 0)
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
            if (ScriptableVariantAssetUtility.GetOrphanOverrides(_variant).Length > 0)
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
            RunEdit(() => ScriptableVariantAssetUtility.OverrideAll(_variant));
        }

        private void RevertAll()
        {
            RunEdit(() => ScriptableVariantAssetUtility.RevertAll(_variant));
        }

        private void Flatten()
        {
            RunEdit(() => ScriptableVariantAssetUtility.Flatten(_variant));
        }

        private void RemoveOrphans()
        {
            RunEdit(() => ScriptableVariantAssetUtility.RemoveOrphanOverrides(_variant));
        }

        private void RefreshAfterHeaderAction()
        {
            _refreshing = true;
            try
            {
                _workingObject?.Update();
                _tree?.Update(true);
                Repaint();
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void RunEdit(Action edit)
        {
            try
            {
                _tree?.ApplyChanges();
                _session.CommitValues();
                edit();
                _parentError = null;
            }
            catch (Exception exception)
            {
                _parentError = exception.Message;
            }

            RefreshAfterHeaderAction();
        }
    }
}
