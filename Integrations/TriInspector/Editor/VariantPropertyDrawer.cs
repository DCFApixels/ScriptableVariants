using System.Collections.Generic;
using System.Reflection;
using DCFApixels.ScriptableVariants.Editor;
using TriInspector;
using TriInspector.VisualElements;
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

            next = RemoveDuplicateUnityDecorators(property, next);
            if (IsVariantLocal(property))
            {
                return next;
            }

            return new VariantOverrideVisualElement(property, next);
        }

        private static bool IsVariantLocal(TriProperty property)
        {
            return property.TryGetMemberInfo(out var memberInfo) &&
                   memberInfo is FieldInfo field &&
                   field.IsDefined(typeof(VariantLocalAttribute), true);
        }

        private static VisualElement RemoveDuplicateUnityDecorators(TriProperty property, VisualElement root)
        {
            var nativePropertyField = FindNativePropertyField(root, property.PropertyPath);
            if (nativePropertyField == null)
            {
                return root;
            }

            return StripTriUnityDecoratorWrappers(root, nativePropertyField);
        }

        private static TriBuiltInPropertyVisualElement FindNativePropertyField(
            VisualElement root,
            string propertyPath)
        {
            if (root is TriBuiltInPropertyVisualElement rootPropertyField &&
                rootPropertyField.bindingPath == propertyPath)
            {
                return rootPropertyField;
            }

            var propertyFields = root.Query<TriBuiltInPropertyVisualElement>().ToList();
            for (var i = 0; i < propertyFields.Count; i++)
            {
                if (propertyFields[i].bindingPath == propertyPath)
                {
                    return propertyFields[i];
                }
            }

            return null;
        }

        private static VisualElement StripTriUnityDecoratorWrappers(
            VisualElement element,
            TriBuiltInPropertyVisualElement nativePropertyField)
        {
            if (ReferenceEquals(element, nativePropertyField))
            {
                return element;
            }

            if (IsTriUnityDecoratorWrapper(element))
            {
                for (var i = element.childCount - 1; i >= 0; i--)
                {
                    var content = element.ElementAt(i);
                    if (!IsOrContains(content, nativePropertyField))
                    {
                        continue;
                    }

                    content.RemoveFromHierarchy();
                    return StripTriUnityDecoratorWrappers(content, nativePropertyField);
                }
            }

            for (var i = element.childCount - 1; i >= 0; i--)
            {
                var child = element.ElementAt(i);
                if (!IsOrContains(child, nativePropertyField))
                {
                    continue;
                }

                var normalizedChild = StripTriUnityDecoratorWrappers(child, nativePropertyField);
                if (ReferenceEquals(child, normalizedChild))
                {
                    break;
                }

                element.RemoveAt(i);
                element.Insert(i, normalizedChild);
                break;
            }

            return element;
        }

        private static bool IsOrContains(VisualElement root, VisualElement descendant)
        {
            return ReferenceEquals(root, descendant) || root.Contains(descendant);
        }

        private static bool IsTriUnityDecoratorWrapper(VisualElement element)
        {
            var typeName = element.GetType().FullName;
            return typeName == "TriInspector.Drawers.HeaderDrawer+TriHeader" ||
                   typeName == "TriInspector.Drawers.SpaceDrawer+TriSpace";
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
            private const float OverrideHitAreaHeight = 20f;
            private const string BaseFieldClassName = "unity-base-field";

            private static readonly Color OverrideColor = new Color32(47, 145, 255, 255);
            private static readonly Color ChildOverrideColor = new Color32(47, 145, 255, 150);

            private readonly TriProperty _property;
            private readonly VisualElement _next;
            private readonly VisualElement _overrideHitArea;
            private readonly VisualElement _overrideBar;
            private readonly Dictionary<TextElement, StyleEnum<FontStyle>> _originalFontStyles =
                new Dictionary<TextElement, StyleEnum<FontStyle>>();
            private VisualElement _propertyRow;

            public VariantOverrideVisualElement(TriProperty property, VisualElement next)
            {
                _property = property;
                _next = next;

                style.flexDirection = FlexDirection.Row;
                style.alignItems = Align.FlexStart;

                _overrideHitArea = new VisualElement
                {
                    tooltip = "Inherited",
                };
                _overrideHitArea.style.width = 8;
                _overrideHitArea.style.minWidth = 8;
                _overrideHitArea.style.height = OverrideHitAreaHeight;
                _overrideHitArea.style.flexShrink = 0;
                _overrideHitArea.style.alignItems = Align.Center;
                _overrideHitArea.style.justifyContent = Justify.Center;

                _overrideBar = new VisualElement();
                _overrideBar.pickingMode = PickingMode.Ignore;
                _overrideBar.style.width = 2;
                _overrideBar.style.height = 16;
                _overrideBar.style.borderTopLeftRadius = 1;
                _overrideBar.style.borderTopRightRadius = 1;
                _overrideBar.style.borderBottomLeftRadius = 1;
                _overrideBar.style.borderBottomRightRadius = 1;
                _overrideBar.style.backgroundColor = Color.clear;

                _overrideHitArea.Add(_overrideBar);
                _overrideHitArea.AddManipulator(new ContextualMenuManipulator(PopulateContextMenu));

                _next.style.flexGrow = 1;
                _next.style.flexShrink = 1;

                Add(_overrideHitArea);
                Add(_next);

                _next.RegisterCallback<GeometryChangedEvent>(_ => UpdateOverridePosition());
                _next.TrackPropertyValueChanged(_property, OnPropertyValueChanged);
                this.PeriodicRun(Refresh);
                Refresh();
            }

            private void PopulateContextMenu(ContextualMenuPopulateEvent evt)
            {
                var variant = GetVariant();
                if (variant == null || !variant.HasParent)
                {
                    return;
                }

                _property.PropertyTree.ApplyChanges();
                ScriptableVariantContextMenu.Populate(
                    evt.menu,
                    variant,
                    _property.PropertyPath,
                    RefreshAfterContextAction);
                evt.StopPropagation();
            }

            private void RefreshAfterContextAction()
            {
                _property.PropertyTree.Update(true);
                _property.RefreshValue();
                Refresh();
            }

            private void OnPropertyValueChanged(TriProperty _)
            {
                var variant = GetVariant();
                if (variant == null || _property.PropertyType == TriPropertyType.Generic)
                {
                    return;
                }

                var path = _property.PropertyPath;
                if (!variant.HasParent || variant.IsLocallyControlled(path))
                {
                    ScriptableVariantAssetUtility.NotifyValuesChanged(variant);
                    return;
                }

                if (ScriptableVariantAssetUtility.ValueMatchesParent(variant, path))
                {
                    return;
                }

                ScriptableVariantAssetUtility.SetOverride(variant, path, true);
                _property.PropertyTree.Update(true);
                _property.RefreshValue();
                Refresh();
            }

            private void Refresh()
            {
                var variant = GetVariant();
                if (variant == null || !variant.HasParent)
                {
                    _overrideHitArea.style.display = DisplayStyle.None;
                    SetOverrideTextBold(false);
                    _next.SetEnabled(true);
                    return;
                }

                _overrideHitArea.style.display = DisplayStyle.Flex;

                var path = _property.PropertyPath;
                var exact = variant.IsOverridden(path);
                var locallyControlled = variant.IsLocallyControlled(path);
                var controlledByAncestor = locallyControlled && !exact;
                var hasChildren = variant.HasOverridesBelow(path);

                if (controlledByAncestor)
                {
                    _overrideBar.style.backgroundColor = Color.clear;
                    _overrideHitArea.tooltip =
                        "Controlled by an owning property override. Right-click to apply or revert it.";
                }
                else if (exact)
                {
                    _overrideBar.style.backgroundColor = OverrideColor;
                    _overrideHitArea.tooltip =
                        "Local override. Right-click to apply it to the parent or revert it.";
                }
                else if (hasChildren)
                {
                    _overrideBar.style.backgroundColor = ChildOverrideColor;
                    _overrideHitArea.tooltip =
                        "Contains local child overrides. Right-click to apply or revert the subtree.";
                }
                else
                {
                    var source = variant.GetValueSource(path);
                    _overrideBar.style.backgroundColor = Color.clear;
                    _overrideHitArea.tooltip = source != null
                        ? $"Inherited from {source.name}. Right-click to override."
                        : "Inherited. Right-click to override.";
                }

                UpdateOverridePosition();
                SetOverrideTextBold(locallyControlled);
                _next.SetEnabled(true);
            }

            private void UpdateOverridePosition()
            {
                if (panel == null || _overrideHitArea.resolvedStyle.display == DisplayStyle.None)
                {
                    return;
                }

                if (_propertyRow == null || _propertyRow.panel == null || !_next.Contains(_propertyRow))
                {
                    SetOverrideTextBold(false);
                    _propertyRow = FindPropertyRow();
                }

                if (_propertyRow == null)
                {
                    return;
                }

                var localCenter = _propertyRow.worldBound.center.y - worldBound.yMin;
                if (float.IsNaN(localCenter) || float.IsInfinity(localCenter))
                {
                    return;
                }

                var marginTop = Mathf.Max(0f, localCenter - OverrideHitAreaHeight * 0.5f);
                var currentMargin = _overrideHitArea.resolvedStyle.marginTop;
                if (float.IsNaN(currentMargin) || Mathf.Abs(currentMargin - marginTop) > 0.1f)
                {
                    _overrideHitArea.style.marginTop = marginTop;
                }
            }

            private void SetOverrideTextBold(bool bold)
            {
                if (!bold)
                {
                    foreach (var pair in _originalFontStyles)
                    {
                        pair.Key.style.unityFontStyleAndWeight = pair.Value;
                    }

                    _originalFontStyles.Clear();
                    return;
                }

                if (_propertyRow == null)
                {
                    return;
                }

                var textElements = _propertyRow.Query<TextElement>().ToList();
                for (var i = 0; i < textElements.Count; i++)
                {
                    var textElement = textElements[i];
                    if (_originalFontStyles.ContainsKey(textElement))
                    {
                        continue;
                    }

                    _originalFontStyles.Add(textElement, textElement.style.unityFontStyleAndWeight);

                    var currentStyle = textElement.resolvedStyle.unityFontStyleAndWeight;
                    textElement.style.unityFontStyleAndWeight =
                        currentStyle == FontStyle.Italic || currentStyle == FontStyle.BoldAndItalic
                            ? FontStyle.BoldAndItalic
                            : FontStyle.Bold;
                }
            }

            private VisualElement FindPropertyRow()
            {
                if (_property.PropertyType == TriPropertyType.Array ||
                    _property.PropertyType == TriPropertyType.Generic)
                {
                    var foldout = _next.Q<Foldout>();
                    if (foldout != null)
                    {
                        var toggle = foldout.Q<Toggle>();
                        return toggle != null ? (VisualElement) toggle : foldout;
                    }
                }

                if (_next.ClassListContains(BaseFieldClassName))
                {
                    return _next;
                }

                return _next.Q<VisualElement>(className: BaseFieldClassName);
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
