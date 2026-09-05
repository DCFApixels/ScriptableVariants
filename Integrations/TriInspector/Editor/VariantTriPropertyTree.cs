using System;
using System.Reflection;
using TriInspector;
using UnityEditor;

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    // Tri 2 exposes this property publicly, but its setter is protected and its concrete
    // SerializedObject tree is sealed. Keep the version-sensitive bridge in one checked place.
    internal static class VariantTriPropertyTree
    {
        private static readonly PropertyInfo Persistence = typeof(TriPropertyTree).GetProperty(
            nameof(TriPropertyTree.TargetIsPersistent), BindingFlags.Instance | BindingFlags.Public);

        internal static TriPropertyTreeForSerializedObject Create(SerializedObject serialized)
        {
            if (Persistence?.PropertyType != typeof(bool) || Persistence.GetSetMethod(true) == null)
                throw new NotSupportedException("This Tri Inspector version cannot provide asset semantics for a variant working copy.");
            var tree = new TriPropertyTreeForSerializedObject(serialized);
            try
            {
                Persistence.SetValue(tree, true);
                if (!tree.TargetIsPersistent) throw new NotSupportedException("Tri Inspector did not accept the asset editing context.");
                return tree;
            }
            catch { tree.Dispose(); throw; }
        }
    }
}
