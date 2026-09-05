using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.Editor
{
    internal sealed class ScriptableVariantCreateMenu : EditorWindow
    {
        [SerializeField] private string _directory = "Assets";
        private HelpBox _error;

        [MenuItem("Assets/Create/Scriptable Variant...", false, 81)]
        private static void ShowCreateMenu()
        {
            var directory = GetSelectedDirectory();
            // MenuItem callbacks need not have Event.current. ShowAsContext silently returns
            // without it; a utility window works from both the main and Project menus.
            var window = GetWindow<ScriptableVariantCreateMenu>(true, "Create Scriptable Variant", true);
            window._directory = directory;
            window.minSize = new Vector2(420, 180);
        }

        internal static Type[] GetVariantTypes()
        {
            return TypeCache.GetTypesDerivedFrom<ScriptableVariant>()
                .Where(type => !type.IsAbstract && !type.ContainsGenericParameters)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = root.style.paddingRight = 8;
            root.style.paddingTop = root.style.paddingBottom = 8;
            root.Add(new Label("Choose a ScriptableVariant type:"));
            var types = GetVariantTypes();
            if (types.Length == 0)
            {
                root.Add(new HelpBox("No concrete ScriptableVariant types found. Add a non-abstract " +
                    "class derived from ScriptableVariant and compile it in Unity.", HelpBoxMessageType.Info));
            }
            else
            {
                var list = new ScrollView();
                list.style.flexGrow = 1;
                root.Add(list);
                for (var i = 0; i < types.Length; i++)
                {
                    var type = types[i];
                    var label = ObjectNames.NicifyVariableName(type.Name) + "  (" +
                                (type.Namespace ?? "global namespace") + ")";
                    list.Add(new Button(() => Create(type)) {text = label, tooltip = type.FullName});
                }
            }
            _error = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _error.style.display = DisplayStyle.None;
            root.Add(_error);
        }

        private void Create(Type variantType)
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Create Scriptable Variant",
                ObjectNames.NicifyVariableName(variantType.Name),
                VariantSourceDatabase.Extension,
                "Choose where to create the Scriptable Variant source asset.",
                _directory);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (ScriptableVariantAssetUtility.CreateRoot(variantType, path) == null)
                {
                    ShowError("The source was saved, but Unity could not import it. See the Console for details.");
                    return;
                }
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
            if (AssetDatabase.IsValidFolder(path))
            {
                return path;
            }

            var directory = !string.IsNullOrEmpty(path) ? Path.GetDirectoryName(path) : null;
            return string.IsNullOrEmpty(directory) ? "Assets" : directory.Replace('\\', '/');
        }
    }
}
