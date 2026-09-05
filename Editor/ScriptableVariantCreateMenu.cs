using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    internal static class ScriptableVariantCreateMenu
    {
        [MenuItem("Assets/Create/Scriptable Variant...", false, 81)]
        private static void ShowCreateMenu()
        {
            var types = TypeCache.GetTypesDerivedFrom<ScriptableVariant>()
                .Where(type => !type.IsAbstract && !type.ContainsGenericParameters)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            var menu = new GenericMenu();
            if (types.Length == 0)
            {
                menu.AddDisabledItem(new GUIContent("No concrete ScriptableVariant types found"));
            }
            else
            {
                for (var i = 0; i < types.Length; i++)
                {
                    var type = types[i];
                    var label = ObjectNames.NicifyVariableName(type.Name) + "  (" +
                                (type.Namespace ?? "global namespace") + ")";
                    menu.AddItem(new GUIContent(label), false, () => Create(type));
                }
            }

            menu.ShowAsContext();
        }

        private static void Create(Type variantType)
        {
            var directory = GetSelectedDirectory();
            var path = EditorUtility.SaveFilePanelInProject(
                "Create Scriptable Variant",
                ObjectNames.NicifyVariableName(variantType.Name),
                VariantSourceDatabase.Extension,
                "Choose where to create the Scriptable Variant source asset.",
                directory);
            if (!string.IsNullOrEmpty(path))
            {
                ScriptableVariantAssetUtility.CreateRoot(variantType, path);
            }
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
