using DCFApixels.ScriptableVariants.Editor;
using TriInspector;
using UnityEngine;
using UnityEngine.UIElements;

[assembly: RegisterTriAttributeDrawer(
    typeof(DCFApixels.ScriptableVariants.TriInspector.Editor.VariantPropertyDrawer),
    TriDrawerOrder.System - 100,
    ApplyOnArrayElement = false)]

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    public sealed class VariantPropertyDrawer : TriAttributeDrawer<VariantPropertyAttribute>
    {
        public override VisualElement CreateVisualElement(TriProperty property, VisualElement next)
        {
            if (!typeof(ScriptableVariant).IsAssignableFrom(property.PropertyTree.TargetObjectType) ||
                !property.TryGetSerializedProperty(out _) ||
                IsInsideAtomicProperty(property))
            {
                return next;
            }

            return new VariantOverrideVisualElement(property, next);
        }

        private static bool IsInsideAtomicProperty(TriProperty property)
        {
            for (var current = property.Parent; current != null && !current.IsRootProperty; current = current.Parent)
            {
                if (current.PropertyType == TriPropertyType.Array ||
                    current.PropertyType == TriPropertyType.Reference)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class VariantOverrideVisualElement : VisualElement
        {
            private readonly TriProperty _property;
            private readonly VisualElement _next;
            private readonly Button _overrideButton;

            public VariantOverrideVisualElement(TriProperty property, VisualElement next)
            {
                _property = property;
                _next = next;

                style.flexDirection = FlexDirection.Row;
                style.alignItems = Align.FlexStart;

                _overrideButton = new Button(ToggleOverride)
                {
                    text = "○",
                    tooltip = "Inherited",
                };
                _overrideButton.style.width = 20;
                _overrideButton.style.minWidth = 20;
                _overrideButton.style.height = 20;
                _overrideButton.style.marginLeft = 0;
                _overrideButton.style.marginRight = 2;
                _overrideButton.style.paddingLeft = 0;
                _overrideButton.style.paddingRight = 0;

                _next.style.flexGrow = 1;
                _next.style.flexShrink = 1;

                Add(_overrideButton);
                Add(_next);

                _next.TrackPropertyValueChanged(_property, OnPropertyValueChanged);
                this.PeriodicRun(Refresh);
                Refresh();
            }

            private void ToggleOverride()
            {
                var variant = GetVariant();
                if (variant == null || !variant.HasParent)
                {
                    return;
                }

                var path = _property.PropertyPath;
                var controlledByAncestor = variant.IsLocallyControlled(path) && !variant.IsOverridden(path);
                if (controlledByAncestor)
                {
                    return;
                }

                _property.PropertyTree.ApplyChanges();
                var hasLocalSubtree = variant.IsOverridden(path) || variant.HasOverridesBelow(path);
                ScriptableVariantAssetUtility.SetOverride(variant, path, !hasLocalSubtree);
                _property.PropertyTree.Update(true);
                _property.RefreshValue();
                Refresh();
            }

            private void OnPropertyValueChanged(TriProperty changedProperty)
            {
                var variant = GetVariant();
                if (variant != null &&
                    (!variant.HasParent || variant.IsLocallyControlled(_property.PropertyPath)))
                {
                    ScriptableVariantAssetUtility.NotifyValuesChanged(variant);
                }
            }

            private void Refresh()
            {
                var variant = GetVariant();
                if (variant == null || !variant.HasParent)
                {
                    _overrideButton.style.display = DisplayStyle.None;
                    _next.SetEnabled(true);
                    return;
                }

                _overrideButton.style.display = DisplayStyle.Flex;

                var path = _property.PropertyPath;
                var exact = variant.IsOverridden(path);
                var locallyControlled = variant.IsLocallyControlled(path);
                var controlledByAncestor = locallyControlled && !exact;
                var hasChildren = variant.HasOverridesBelow(path);
                var isContainer = _property.PropertyType == TriPropertyType.Generic;

                if (controlledByAncestor)
                {
                    _overrideButton.text = "◆";
                    _overrideButton.tooltip = "Locally controlled by an owning property override.";
                    _overrideButton.SetEnabled(false);
                }
                else if (exact)
                {
                    _overrideButton.text = "●";
                    _overrideButton.tooltip = "Local override. Click to revert this property subtree.";
                    _overrideButton.SetEnabled(true);
                }
                else if (hasChildren)
                {
                    _overrideButton.text = "◐";
                    _overrideButton.tooltip = "Contains local child overrides. Click to revert the subtree.";
                    _overrideButton.SetEnabled(true);
                }
                else
                {
                    var source = variant.GetValueSource(path);
                    _overrideButton.text = "○";
                    _overrideButton.tooltip = source != null
                        ? $"Inherited from {source.name}. Click to override."
                        : "Inherited. Click to override.";
                    _overrideButton.SetEnabled(true);
                }

                _next.SetEnabled(locallyControlled || isContainer);
            }

            private ScriptableVariant GetVariant()
            {
                if (_property.PropertyTree.TargetsCount != 1)
                {
                    return null;
                }

                return _property.PropertyTree.RootProperty.GetValue(0) as ScriptableVariant;
            }
        }
    }
}
