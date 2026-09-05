using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.Editor
{
    internal sealed class ScriptableVariantCreateMenu : EditorWindow
    {
        [SerializeField] private string _directory = "Assets";
        [SerializeField] private string _searchText = string.Empty;
        private Type[] _types = Array.Empty<Type>();
        private Type[] _filteredTypes = Array.Empty<Type>();
        private ListView _list;
        private Button _createButton;
        private Label _status;
        private Label _destination;
        private HelpBox _error;

        [MenuItem("Assets/Create/Scriptable Variant...", false, 81)]
        private static void ShowCreateMenu()
        {
            var directory = GetSelectedDirectory();
            // MenuItem callbacks need not have Event.current. ShowAsContext silently returns
            // without it; a utility window works from both the main and Project menus.
            var window = GetWindow<ScriptableVariantCreateMenu>(true, "Create Scriptable Variant", true);
            window._directory = directory;
            window.minSize = new Vector2(460, 300);
            window.UpdateDestination();
        }

        internal static Type[] GetVariantTypes()
        {
            return TypeCache.GetTypesDerivedFrom<ScriptableVariant>()
                .Where(type => !type.IsAbstract && !type.ContainsGenericParameters)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }

        internal static Type[] FilterTypes(Type[] types, string search)
        {
            var words = (search ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return types;
            return types.Where(type =>
            {
                var text = type.FullName + " " + ObjectNames.NicifyVariableName(type.Name) + " " +
                           type.Assembly.GetName().Name;
                return words.All(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
            }).ToArray();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = root.style.paddingRight = 8;
            root.style.paddingTop = root.style.paddingBottom = 8;
            var heading = new Label("Scriptable Variant");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.fontSize = 14;
            heading.style.marginBottom = 8;
            root.Add(heading);
            var search = new ToolbarSearchField
            {
                name = "variant-type-search",
                tooltip = "Search by type name, namespace or assembly. Use spaces to combine search terms.",
            };
            search.SetValueWithoutNotify(_searchText);
            search.style.marginBottom = 6;
            root.Add(search);

            _types = GetVariantTypes();
            _list = new ListView
            {
                name = "variant-type-list",
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = 42,
                showBorder = true,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = MakeTypeRow,
                bindItem = BindTypeRow,
            };
            _list.style.flexGrow = 1;
            _list.style.minHeight = 100;
            _list.selectionChanged += _ => UpdateSelection();
            _list.itemsChosen += _ => CreateSelected();
            root.Add(_list);
            _status = new Label {name = "variant-type-status"};
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = 4;
            root.Add(_status);
            _destination = new Label {name = "variant-destination"};
            _destination.style.whiteSpace = WhiteSpace.Normal;
            root.Add(_destination);

            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.justifyContent = Justify.FlexEnd;
            footer.style.marginTop = 8;
            footer.Add(new Button(Close) {text = "Cancel"});
            _createButton = new Button(CreateSelected) {name = "variant-create", text = "Create"};
            _createButton.style.minWidth = 100;
            footer.Add(_createButton);
            root.Add(footer);
            _error = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _error.style.display = DisplayStyle.None;
            root.Add(_error);

            search.RegisterValueChangedCallback(change =>
            {
                _searchText = change.newValue;
                ApplyFilter();
            });
            // The root survives CreateGUI calls; avoid accumulating keyboard handlers on rebuild.
            root.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            search.RegisterCallback<KeyDownEvent>(OnSearchKeyDown, TrickleDown.TrickleDown);
            ApplyFilter();
            UpdateDestination();
            search.schedule.Execute(() => search.Focus());
        }

        private static VisualElement MakeTypeRow()
        {
            var row = new VisualElement();
            row.style.paddingLeft = row.style.paddingRight = 6;
            row.style.justifyContent = Justify.Center;
            var nameLabel = new Label {name = "type-name", displayTooltipWhenElided = false};
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.overflow = Overflow.Hidden;
            nameLabel.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(nameLabel);
            var context = new Label {name = "type-context", displayTooltipWhenElided = false};
            context.style.fontSize = 10;
            context.style.opacity = 0.65f;
            context.style.overflow = Overflow.Hidden;
            context.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(context);
            return row;
        }

        private void BindTypeRow(VisualElement row, int index)
        {
            var type = _filteredTypes[index];
            row.Q<Label>("type-name").text = ObjectNames.NicifyVariableName(type.Name);
            row.Q<Label>("type-context").text = (type.Namespace ?? "global namespace") + " · " + type.Assembly.GetName().Name;
        }

        private void ApplyFilter()
        {
            var selected = _list.selectedItem as Type;
            _filteredTypes = FilterTypes(_types, _searchText);
            _list.itemsSource = _filteredTypes;
            var index = Array.IndexOf(_filteredTypes, selected);
            _list.selectedIndex = index >= 0 ? index : _filteredTypes.Length > 0 ? 0 : -1;
            UpdateSelection();
            _error.style.display = DisplayStyle.None;
            _status.text = _types.Length == 0
                ? "No concrete ScriptableVariant types found. Add a class derived from ScriptableVariant and compile it in Unity."
                : _filteredTypes.Length == 0
                    ? "No matching types. Try a different search."
                    : $"{_filteredTypes.Length} of {_types.Length} types · Double-click or press Enter to create";
        }

        private void UpdateSelection() => _createButton?.SetEnabled(_list.selectedItem is Type);

        private void UpdateDestination()
        {
            if (_destination == null) return;
            _destination.text = "Create in: " + _directory;
            _destination.tooltip = "The folder selected when this window was opened. Name the new .svariant in the Project window.";
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                evt.StopPropagation();
                Close();
            }
            else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                // Buttons/list handle their own submit. Handle Enter here only while searching.
                if (evt.target is VisualElement target && rootVisualElement.Q<ToolbarSearchField>().Contains(target))
                {
                    evt.StopPropagation();
                    CreateSelected();
                }
            }
        }

        private void OnSearchKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.DownArrow && evt.keyCode != KeyCode.UpArrow) return;
            evt.StopPropagation();
            if (_filteredTypes.Length == 0) return;
            var index = _list.selectedIndex + (evt.keyCode == KeyCode.DownArrow ? 1 : -1);
            _list.selectedIndex = Mathf.Clamp(index, 0, _filteredTypes.Length - 1);
            _list.ScrollToItem(_list.selectedIndex);
        }

        private void CreateSelected()
        {
            if (!(_list.selectedItem is Type variantType)) return;
            try
            {
                VariantAssetCreationAction.Begin(variantType, _directory);
                Close();
            }
            catch (ExitGUIException) { throw; }
            catch (Exception exception)
            {
                ShowError(exception.Message);
                Debug.LogException(exception);
            }
        }

        private void ShowError(string message)
        {
            _error.text = message;
            _error.style.display = DisplayStyle.Flex;
        }

        private static string GetSelectedDirectory()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            var directory = AssetDatabase.IsValidFolder(path) ? path :
                !string.IsNullOrEmpty(path) ? Path.GetDirectoryName(path)?.Replace('\\', '/') : null;
            // New user assets belong in Assets, never in an installed/read-only package.
            return IsAssetDirectory(directory) && AssetDatabase.IsValidFolder(directory) ? directory : "Assets";
        }

        internal static bool IsAssetDirectory(string directory) => directory == "Assets" ||
            (directory != null && directory.StartsWith("Assets/", StringComparison.Ordinal) &&
             !directory.Split('/').Any(part => part == ".." || part == "."));
    }
}
