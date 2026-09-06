using System;
using System.Collections.Generic;
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
        private VariantMenuModel _model = VariantMenuModel.Empty;
        private VariantMenuItem[] _filteredItems = Array.Empty<VariantMenuItem>();
        private TreeView _tree;
        private ListView _searchList;
        private Button _createButton;
        private Label _status;
        private Label _destination;
        private HelpBox _error;
        private ToolbarSearchField _search;

        [MenuItem("Assets/Create/Scriptable Variant...", false, 81)]
        private static void ShowCreateMenu()
        {
            var window = GetWindow<ScriptableVariantCreateMenu>(true, "Create Scriptable Variant", true);
            window._directory = GetSelectedDirectory();
            window.minSize = new Vector2(460, 320);
            window.UpdateDestination();
        }

        internal static Type[] GetVariantTypes()
        {
            return TypeCache.GetTypesDerivedFrom<ScriptableVariant>()
                .Where(type => !type.IsAbstract && !type.ContainsGenericParameters)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }

        internal static VariantMenuItem[] FilterItems(VariantMenuItem[] items, string search)
        {
            var words = (search ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return items;
            return items.Where(item => words.All(word =>
                item.MenuPath.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
        }

        internal static VariantMenuModel BuildMenu(Type[] types)
        {
            var definitions = types.Distinct().Select(CreateDefinition)
                .OrderBy(definition => definition.Order)
                .ThenBy(definition => definition.MenuPath, StringComparer.Ordinal)
                .ThenBy(definition => definition.Type.AssemblyQualifiedName, StringComparer.Ordinal)
                .ToArray();
            var root = new MenuNode(string.Empty, string.Empty);
            foreach (var definition in definitions)
            {
                var node = root;
                for (var i = 0; i < definition.Parts.Length - 1; i++)
                    node = node.GetOrAddGroup(definition.Parts[i]);
                node.Leaves.Add(definition);
            }

            var nextId = 1;
            var leaves = new List<VariantMenuItem>(definitions.Length);
            var roots = BuildTreeItems(root, ref nextId, leaves);
            return new VariantMenuModel(roots, leaves.ToArray(), CountGroups(root));
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

            _search = new ToolbarSearchField {name = "variant-type-search"};
            _search.SetValueWithoutNotify(_searchText);
            _search.style.marginBottom = 6;
            root.Add(_search);

            _model = BuildMenu(GetVariantTypes());
            _tree = new TreeView
            {
                name = "variant-type-tree",
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = 42,
                showBorder = true,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = MakeTreeRow,
            };
            _tree.bindItem = (row, index) =>
                BindTreeRow(row, _tree.GetItemDataForIndex<VariantMenuItem>(index));
            _tree.SetRootItems(_model.Roots);
            _tree.style.flexGrow = 1;
            _tree.style.minHeight = 100;
            _tree.selectionChanged += _ => UpdateSelection();
            _tree.itemsChosen += _ => CreateSelected();
            root.Add(_tree);

            _searchList = new ListView
            {
                name = "variant-type-search-results",
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = 42,
                showBorder = true,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                makeItem = MakeSearchRow,
                bindItem = (row, index) => BindSearchRow(row, _filteredItems[index]),
            };
            _searchList.style.flexGrow = 1;
            _searchList.style.minHeight = 100;
            _searchList.style.display = DisplayStyle.None;
            _searchList.selectionChanged += _ => UpdateSelection();
            _searchList.itemsChosen += _ => CreateSelected();
            root.Add(_searchList);

            _status = new Label {name = "variant-type-status"};
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = 4;
            root.Add(_status);
            _destination = new Label {name = "variant-destination", displayTooltipWhenElided = false};
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

            _search.RegisterValueChangedCallback(change =>
            {
                _searchText = change.newValue;
                ApplyFilter();
            });
            root.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _search.RegisterCallback<KeyDownEvent>(OnSearchKeyDown, TrickleDown.TrickleDown);
            ApplyFilter();
            UpdateDestination();
            _search.schedule.Execute(() => _search.Focus());
        }

        private static VisualElement MakeTreeRow()
        {
            var row = new VisualElement();
            row.style.flexGrow = 1;
            row.style.alignSelf = Align.Stretch;
            row.style.paddingLeft = row.style.paddingRight = 6;
            row.style.justifyContent = Justify.Center;
            var nameLabel = new Label {name = "type-name", displayTooltipWhenElided = false};
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.overflow = Overflow.Hidden;
            nameLabel.style.textOverflow = TextOverflow.Ellipsis;
            nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(nameLabel);
            var context = new Label {name = "type-context", displayTooltipWhenElided = false};
            context.style.fontSize = 10;
            context.style.opacity = 0.65f;
            context.style.overflow = Overflow.Hidden;
            context.style.textOverflow = TextOverflow.Ellipsis;
            context.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(context);
            return row;
        }

        private static VisualElement MakeSearchRow()
        {
            var row = new VisualElement();
            row.style.paddingLeft = row.style.paddingRight = 6;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 3;
            row.style.justifyContent = Justify.FlexStart;
            var nameLabel = new Label {name = "type-name", displayTooltipWhenElided = false};
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.overflow = Overflow.Hidden;
            nameLabel.style.textOverflow = TextOverflow.Ellipsis;
            nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(nameLabel);
            var context = new Label {name = "type-context", displayTooltipWhenElided = false};
            context.style.fontSize = 10;
            context.style.opacity = 0.65f;
            context.style.overflow = Overflow.Hidden;
            context.style.textOverflow = TextOverflow.Ellipsis;
            context.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(context);
            return row;
        }

        private static void BindTreeRow(VisualElement row, VariantMenuItem item)
        {
            var name = row.Q<Label>("type-name");
            name.text = item.Name;
            var context = row.Q<Label>("type-context");
            context.text = item.IsGroup
                ? item.MenuPath
                : (item.Type.Namespace ?? "global namespace") + " · " + item.Type.Assembly.GetName().Name;
        }

        private static void BindSearchRow(VisualElement row, VariantMenuItem item)
        {
            row.Q<Label>("type-name").text = item.Name;
            var context = row.Q<Label>("type-context");
            context.text = item.MenuPath.Replace("/", "  /  ") + " · " +
                           (item.Type.Namespace ?? "global namespace") + " · " + item.Type.Assembly.GetName().Name;
        }

        private void ApplyFilter()
        {
            var selected = GetSelectedItem();
            var searching = !string.IsNullOrWhiteSpace(_searchText);
            _filteredItems = FilterItems(_model.Leaves, _searchText);
            _tree.style.display = searching ? DisplayStyle.None : DisplayStyle.Flex;
            _searchList.style.display = searching ? DisplayStyle.Flex : DisplayStyle.None;

            if (searching)
            {
                _searchList.itemsSource = _filteredItems;
                var index = selected == null ? -1 : Array.FindIndex(_filteredItems, item => item.Id == selected.Id);
                _searchList.selectedIndex = index >= 0 ? index : _filteredItems.Length > 0 ? 0 : -1;
            }
            else
            {
                _searchList.itemsSource = Array.Empty<VariantMenuItem>();
                _searchList.selectedIndex = -1;
                if (selected != null && !selected.IsGroup)
                    _tree.SetSelectionById(selected.Id);
            }

            _error.style.display = DisplayStyle.None;
            _status.text = _model.Leaves.Length == 0
                ? "No concrete ScriptableVariant types found. Add a class derived from ScriptableVariant and compile it in Unity."
                : searching && _filteredItems.Length == 0
                    ? "No matching creation options. Try a different search."
                    : searching
                        ? $"{_filteredItems.Length} of {_model.Leaves.Length} creation options · Full menu paths are shown below"
                        : $"{_model.Leaves.Length} types in {_model.GroupCount} groups · Expand a group or search by creation path";
            UpdateSelection();
        }

        private VariantMenuItem GetSelectedItem()
        {
            if (_searchList != null && _searchList.style.display.value == DisplayStyle.Flex)
                return _searchList.selectedItem as VariantMenuItem;
            if (_tree == null || _tree.selectedIndex < 0) return null;
            return _tree.GetItemDataForIndex<VariantMenuItem>(_tree.selectedIndex);
        }

        private void UpdateSelection() => _createButton?.SetEnabled(GetSelectedItem()?.Type != null);

        private void UpdateDestination()
        {
            if (_destination != null) _destination.text = "Create in: " + _directory;
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
                if (evt.target is VisualElement target && _search.Contains(target) && GetSelectedItem()?.Type != null)
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

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                _tree.Focus();
                if (_tree.selectedIndex < 0 && _tree.GetTreeCount() > 0) _tree.selectedIndex = 0;
                return;
            }

            if (_filteredItems.Length == 0) return;
            var index = _searchList.selectedIndex + (evt.keyCode == KeyCode.DownArrow ? 1 : -1);
            _searchList.selectedIndex = Mathf.Clamp(index, 0, _filteredItems.Length - 1);
            _searchList.ScrollToItem(_searchList.selectedIndex);
        }

        private void CreateSelected()
        {
            var selected = GetSelectedItem();
            if (selected?.Type == null) return;
            try
            {
                VariantAssetCreationAction.Begin(selected.Type, _directory, selected.FileName);
                Close();
            }
            catch (ExitGUIException) { throw; }
            catch (Exception exception)
            {
                _error.text = exception.Message;
                _error.style.display = DisplayStyle.Flex;
                Debug.LogException(exception);
            }
        }

        private static MenuDefinition CreateDefinition(Type type)
        {
            var attribute = (CreateVariantMenuAttribute)Attribute.GetCustomAttribute(
                type, typeof(CreateVariantMenuAttribute), false);
            var parts = (attribute?.menuName ?? string.Empty).Split('/')
                .Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
            if (parts.Length == 0) parts = new[] {ObjectNames.NicifyVariableName(type.Name)};
            return new MenuDefinition(type, parts, attribute?.fileName, attribute?.order ?? 0);
        }

        private static List<TreeViewItemData<VariantMenuItem>> BuildTreeItems(
            MenuNode node, ref int nextId, List<VariantMenuItem> leaves)
        {
            var results = new List<TreeViewItemData<VariantMenuItem>>();
            var children = node.Groups.Cast<object>().Concat(node.Leaves)
                .OrderBy(value => value is MenuNode group ? group.MinimumOrder : ((MenuDefinition)value).Order)
                .ThenBy(value => value is MenuNode group ? group.Name : ((MenuDefinition)value).Name,
                    StringComparer.Ordinal)
                .ThenBy(value => value is MenuDefinition leaf ? leaf.Type.AssemblyQualifiedName : string.Empty,
                    StringComparer.Ordinal);
            foreach (var child in children)
            {
                if (child is MenuNode group)
                {
                    var id = nextId++;
                    var item = new VariantMenuItem(id, null, group.Name, group.Path, null);
                    results.Add(new TreeViewItemData<VariantMenuItem>(
                        id, item, BuildTreeItems(group, ref nextId, leaves)));
                }
                else
                {
                    var definition = (MenuDefinition)child;
                    var id = nextId++;
                    var item = new VariantMenuItem(
                        id, definition.Type, definition.Name, definition.MenuPath, definition.FileName);
                    leaves.Add(item);
                    results.Add(new TreeViewItemData<VariantMenuItem>(id, item));
                }
            }
            return results;
        }

        private static int CountGroups(MenuNode node) =>
            node.Groups.Count + node.Groups.Sum(CountGroups);

        private static string GetSelectedDirectory()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            var directory = AssetDatabase.IsValidFolder(path) ? path :
                !string.IsNullOrEmpty(path) ? Path.GetDirectoryName(path)?.Replace('\\', '/') : null;
            return IsAssetDirectory(directory) && AssetDatabase.IsValidFolder(directory) ? directory : "Assets";
        }

        internal static bool IsAssetDirectory(string directory) => directory == "Assets" ||
            (directory != null && directory.StartsWith("Assets/", StringComparison.Ordinal) &&
             !directory.Split('/').Any(part => part == ".." || part == "."));

        internal sealed class VariantMenuModel
        {
            internal static readonly VariantMenuModel Empty =
                new VariantMenuModel(new List<TreeViewItemData<VariantMenuItem>>(), Array.Empty<VariantMenuItem>(), 0);

            internal readonly List<TreeViewItemData<VariantMenuItem>> Roots;
            internal readonly VariantMenuItem[] Leaves;
            internal readonly int GroupCount;

            internal VariantMenuModel(
                List<TreeViewItemData<VariantMenuItem>> roots, VariantMenuItem[] leaves, int groupCount)
            {
                Roots = roots;
                Leaves = leaves;
                GroupCount = groupCount;
            }
        }

        internal sealed class VariantMenuItem
        {
            internal readonly int Id;
            internal readonly Type Type;
            internal readonly string Name;
            internal readonly string MenuPath;
            internal readonly string FileName;
            internal bool IsGroup => Type == null;

            internal VariantMenuItem(int id, Type type, string name, string menuPath, string fileName)
            {
                Id = id;
                Type = type;
                Name = name;
                MenuPath = menuPath;
                FileName = fileName;
            }
        }

        private sealed class MenuDefinition
        {
            internal readonly Type Type;
            internal readonly string[] Parts;
            internal readonly string Name;
            internal readonly string MenuPath;
            internal readonly string FileName;
            internal readonly int Order;

            internal MenuDefinition(Type type, string[] parts, string fileName, int order)
            {
                Type = type;
                Parts = parts;
                Name = parts[parts.Length - 1];
                MenuPath = string.Join("/", parts);
                FileName = fileName;
                Order = order;
            }
        }

        private sealed class MenuNode
        {
            private readonly Dictionary<string, MenuNode> _groups =
                new Dictionary<string, MenuNode>(StringComparer.Ordinal);

            internal readonly string Name;
            internal readonly string Path;
            internal readonly List<MenuDefinition> Leaves = new List<MenuDefinition>();
            internal ICollection<MenuNode> Groups => _groups.Values;
            internal int MinimumOrder => Groups.Select(group => group.MinimumOrder)
                .Concat(Leaves.Select(leaf => leaf.Order)).DefaultIfEmpty(0).Min();

            internal MenuNode(string name, string path)
            {
                Name = name;
                Path = path;
            }

            internal MenuNode GetOrAddGroup(string name)
            {
                if (_groups.TryGetValue(name, out var group)) return group;
                var path = string.IsNullOrEmpty(Path) ? name : Path + "/" + name;
                group = new MenuNode(name, path);
                _groups.Add(name, group);
                return group;
            }
        }
    }
}
