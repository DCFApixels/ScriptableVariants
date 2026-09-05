using System;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    // Created explicitly by the source inspector; never registered for imported runtime objects.
    internal sealed class VariantWorkingCopyEditor : UnityEditor.Editor
    {
        internal Func<VisualElement> CreateView;

        public override VisualElement CreateInspectorGUI()
        {
            return CreateView?.Invoke() ?? new VisualElement();
        }
    }
}
