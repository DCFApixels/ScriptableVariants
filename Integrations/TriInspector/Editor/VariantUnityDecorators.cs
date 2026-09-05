using System;
using TriInspector;
using UnityEngine;
using UnityEngine.UIElements;

[assembly: RegisterTriAttributeDrawer(typeof(DCFApixels.ScriptableVariants.TriInspector.Editor.VariantUnityDecoratorDrawer), 8900)]

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    public sealed class VariantUnityDecoratorAttribute : Attribute
    {
        internal readonly Attribute Original;
        internal VariantUnityDecoratorAttribute(Attribute original) { Original = original; }
    }

    // Keep one owner for Unity decorators. The native PropertyField owns them when it is used;
    // otherwise render them at Tri's normal decorator priority, without private wrapper names.
    public sealed class VariantUnityDecoratorDrawer : TriAttributeDrawer<VariantUnityDecoratorAttribute>
    {
        public override VisualElement CreateVisualElement(TriProperty property, VisualElement next)
        {
            if (typeof(ScriptableVariant).IsAssignableFrom(property.PropertyTree.TargetObjectType) &&
                VariantPropertyDrawer.FindNativePropertyField(next, property.PropertyPath) != null) return next;
            var wrapper = new VisualElement();
            wrapper.AddToClassList("scriptable-variant-decorator");
            if (Attribute.Original is HeaderAttribute header)
                wrapper.Add(new Label(header.header)
                {style = {marginTop = 13, unityFontStyleAndWeight = FontStyle.Bold}});
            else if (Attribute.Original is SpaceAttribute space) wrapper.style.marginTop = space.height;
            wrapper.Add(next);
            return wrapper;
        }
    }
}
