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
                _session.StateChanged += Repaint;
                Undo.undoRedoPerformed += RefreshAfterHeaderAction;
            }
            catch (Exception exception)
            {
                _parentError = exception.Message;
                if (_workingEditor != null) DestroyImmediate(_workingEditor);
                _workingEditor = null;
                _workingObject = null;
                if (_session != null)
                {
                    _session.Reloaded -= RefreshAfterHeaderAction;
                    _session.StateChanged -= Repaint;
                    _session.Dispose();
                    _session = null;
                }
                _variant = null;
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
                _session.StateChanged -= Repaint;
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
            var error = _session?.Error ?? _parentError;
            if (!string.IsNullOrEmpty(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Retry Save")) RunEdit(() => { });
                    if (GUILayout.Button("Reload from Source") && EditorUtility.DisplayDialog(
                            "Discard pending variant edits?",
                            "This replaces the Inspector's unsaved values with the current source. The source file is not changed.",
                            "Discard and Reload", "Cancel"))
                    {
                        _session?.ReloadDiscardingChanges();
                        _parentError = null;
                    }
                }
            }

            var orphans = ScriptableVariantAssetUtility.GetOrphanOverrides(_variant);
            if (orphans.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    "Unknown stored paths: " + string.Join(", ", orphans),
                    MessageType.Warning);
                if (GUILayout.Button("Remove Orphan Data...")) RemoveOrphans();
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
            try
            {
                _tree = VariantTriPropertyTree.Create(_workingObject);
                _tree.Update(true);
                _tree.RunValidation();
            }
            catch (Exception exception)
            {
                DisposeTree();
                _session.ReportError(exception.Message);
                return new HelpBox(exception.Message, HelpBoxMessageType.Error);
            }
            _tree.RootProperty.ChildValueChanged += OnValuesChanged;

            if (!_tree.RootProperty.TryGetAttribute(out HideMonoScriptAttribute _))
            {
                var script = new PropertyField(_workingObject.FindProperty("m_Script"));
                script.Bind(_workingObject);
                script.SetEnabled(false);
                _root.Add(script);
            }

            _root.Add(_tree.GetRootElement());
            _root.RegisterCallback<GeometryChangedEvent>(_ => ConfigureAssetFields());
            _root.schedule.Execute(ConfigureAssetFields);
            _root.TrackSerializedObjectValue(_workingObject, _ => OnValuesChanged(null));
            _update = _root.schedule.Execute(() =>
            {
                _tree.Update();
                _tree.RunValidationIfRequired();
            }).Every(100);
            return _root;
        }

        private void ConfigureAssetFields()
        {
            _root?.Query<ObjectField>().ForEach(field => field.allowSceneObjects = false);
        }

        private void OnValuesChanged(TriProperty property)
        {
            if (_refreshing || _session == null)
            {
                return;
            }

            try
            {
                _session.RequestCommit();
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
            if (EditorUtility.DisplayDialog("Remove orphan variant data?",
                    "These stored fields no longer exist in the script and will be removed:\n" +
                    string.Join(", ", ScriptableVariantAssetUtility.GetOrphanOverrides(_variant)) +
                    "\nOther pending field edits will be saved. This action supports Undo.", "Remove", "Cancel"))
                RunEdit(() => _session.RemoveOrphans(), false);
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

        private void RunEdit(Action edit, bool commitPending = true)
        {
            try
            {
                _tree?.ApplyChanges();
                if (commitPending) _session.CommitValues();
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
