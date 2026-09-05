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

            if (IsVariantLocal(property) || !string.IsNullOrEmpty(VariantSerialization.GetLocalControllerPath(
                    property.PropertyTree.TargetObjectType, property.PropertyPath)))
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

        internal static TriBuiltInPropertyVisualElement FindNativePropertyField(VisualElement root, string path)
        {
            if (root is TriBuiltInPropertyVisualElement field && field.bindingPath == path) return field;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindNativePropertyField(root.ElementAt(i), path);
                if (found != null) return found;
            }
            return null;
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
            private readonly ScriptableVariant _variant;
            private readonly string _propertyPath;
            private readonly VisualElement _next;
            private readonly VisualElement _overrideHitArea;
            private readonly VisualElement _overrideBar;
            private readonly Dictionary<TextElement, StyleEnum<FontStyle>> _originalFontStyles =
                new Dictionary<TextElement, StyleEnum<FontStyle>>();
            private VisualElement _propertyRow;
            private readonly VariantEditingSession _session;
            private bool _isBold;

            public VariantOverrideVisualElement(TriProperty property, VisualElement next)
            {
                _property = property;
                _variant = property.PropertyTree.TargetsCount == 1
                    ? property.PropertyTree.RootProperty.GetValue(0) as ScriptableVariant
                    : null;
                _propertyPath = property.PropertyPath;
                _next = next;
                VariantEditingSession.TryGetSession(_variant, out _session);

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

                _next.RegisterCallback<GeometryChangedEvent>(_ =>
                {
                    var bold = _isBold;
                    UpdateOverridePosition();
                    SetOverrideTextBold(bold);
                });
                RegisterCallback<AttachToPanelEvent>(_ =>
                {
                    if (_session != null) _session.StateChanged += Refresh;
                    Refresh();
                });
                RegisterCallback<DetachFromPanelEvent>(_ =>
                {
                    if (_session != null) _session.StateChanged -= Refresh;
                    SetOverrideTextBold(false);
                });
                Refresh();
            }

            private void PopulateContextMenu(ContextualMenuPopulateEvent evt)
            {
                if (_variant == null || !ScriptableVariantAssetUtility.HasParent(_variant))
                {
                    return;
                }

                try
                {
                    _property.PropertyTree.ApplyChanges();
                    VariantEditingSession.CommitValues(_variant);
                }
                catch (System.Exception exception)
                {
                    _session?.ReportError(exception.Message);
                    evt.StopPropagation();
                    return;
                }
                ScriptableVariantContextMenu.Populate(
                    evt.menu,
                    _variant,
                    _propertyPath,
                    RefreshAfterContextAction);
                evt.StopPropagation();
            }

            private void RefreshAfterContextAction()
            {
                _property.PropertyTree.Update(true);
                _property.RefreshValue();
                Refresh();
            }

            private void Refresh()
            {
                if (_variant == null || !ScriptableVariantAssetUtility.HasParent(_variant))
                {
                    _overrideHitArea.style.display = DisplayStyle.None;
                    SetOverrideTextBold(false);
                    return;
                }

                _overrideHitArea.style.display = DisplayStyle.Flex;

                var exact = ScriptableVariantAssetUtility.IsOverridden(_variant, _propertyPath);
                var locallyControlled =
                    ScriptableVariantAssetUtility.IsLocallyControlled(_variant, _propertyPath);
                var controlledByAncestor = locallyControlled && !exact;
                var hasChildren = ScriptableVariantAssetUtility.HasOverridesBelow(_variant, _propertyPath);

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
                    var source = ScriptableVariantAssetUtility.GetValueSource(_variant, _propertyPath);
                    _overrideBar.style.backgroundColor = Color.clear;
                    _overrideHitArea.tooltip = source != null
                        ? $"Inherited from {source.name}. Right-click to override."
                        : "Inherited. Right-click to override.";
                }

                UpdateOverridePosition();
                SetOverrideTextBold(locallyControlled);
            }

            private void UpdateOverridePosition()
            {
                if (panel == null || _overrideHitArea.resolvedStyle.display == DisplayStyle.None)
                {
                    return;
                }

                if (_propertyRow == null || _propertyRow.panel == null ||
                    !ReferenceEquals(_next, _propertyRow) && !_next.Contains(_propertyRow))
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
                _isBold = bold;
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
        }
    }
}
