using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_4_OR_NEWER
using NameEditAction = UnityEditor.ProjectWindowCallback.AssetCreationEndAction;
using AssetId = UnityEngine.EntityId;
#else
using NameEditAction = UnityEditor.ProjectWindowCallback.EndNameEditAction;
using AssetId = System.Int32;
#endif

namespace DCFApixels.ScriptableVariants.Editor
{
    // Unity owns this callback until the Project window accepts/cancels the filename. Keep
    // only a serializable type identity so a domain reload during naming does not lose it.
    internal sealed class VariantAssetCreationAction : NameEditAction
    {
        [SerializeField] private string _typeName;

        internal static void Begin(Type variantType, string directory)
        {
            if (!ScriptableVariantCreateMenu.IsAssetDirectory(directory) || !AssetDatabase.IsValidFolder(directory))
                throw new InvalidOperationException("The target folder no longer exists under Assets. Reopen the creation window from the target folder.");
            var action = CreateInstance<VariantAssetCreationAction>();
            action._typeName = variantType.AssemblyQualifiedName;
            var path = directory + "/" + ObjectNames.NicifyVariableName(variantType.Name) + "." + VariantSourceDatabase.Extension;
            try
            {
                // Public API only. Unity 6.4 replaced int instance IDs with EntityId.
                ProjectWindowUtil.StartNameEditingIfProjectWindowExists(default(AssetId), action, path,
                    EditorGUIUtility.ObjectContent(null, variantType).image as Texture2D, null);
            }
            catch
            {
                if (action != null) DestroyImmediate(action);
                throw;
            }
        }

        public override void Action(AssetId instanceId, string pathName, string resourceFile)
        {
            try
            {
                var type = TypeCache.GetTypesDerivedFrom<ScriptableVariant>()
                    .FirstOrDefault(candidate => candidate.AssemblyQualifiedName == _typeName);
                if (type == null)
                    throw new InvalidOperationException("The selected ScriptableVariant type is no longer available. Reopen the creation window after compiling your scripts.");
                var path = EnsureSourceExtension(pathName);
                var asset = ScriptableVariantAssetUtility.CreateRoot(type, path);
                if (asset == null)
                    throw new InvalidOperationException("The source was saved, but Unity could not import it. See the Console for details.");
                ProjectWindowUtil.ShowCreatedAsset(asset);
            }
            catch (ExitGUIException) { throw; }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Could not create Scriptable Variant", exception.Message, "OK");
            }
        }

        internal static string EnsureSourceExtension(string path) =>
            path.EndsWith("." + VariantSourceDatabase.Extension, StringComparison.OrdinalIgnoreCase)
                ? path : path + "." + VariantSourceDatabase.Extension;
    }
}
