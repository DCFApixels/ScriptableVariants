using System;
using System.Collections.Generic;
using UnityEngine;

namespace DCFApixels.ScriptableVariants
{
    /// <summary>
    /// Base class for ScriptableObject assets whose serialized fields can inherit values from a parent asset.
    /// </summary>
    public abstract class ScriptableVariant : ScriptableObject, ISerializationCallbackReceiver
    {
        [SerializeField, HideInInspector, VariantLocal]
        private ScriptableVariant _variantParent;

        [SerializeField, HideInInspector, VariantLocal]
        private List<string> _variantOverrides = new List<string>();

        [NonSerialized]
        private HashSet<string> _overrideLookup;

        [NonSerialized]
        private bool _resolutionDirty = true;

        [NonSerialized]
        private bool _overridePathsNeedNormalization = true;

        [NonSerialized]
        private bool _isResolving;

        [NonSerialized]
        private bool _resolutionErrorLogged;

        [NonSerialized]
        private int _resolvedRevision;

        [NonSerialized]
        private int _observedParentRevision = -1;

        private static readonly List<ScriptableVariant> ActiveVariants = new List<ScriptableVariant>();

        /// <summary>Untyped parent asset. Prefer the typed property on ScriptableVariant&lt;TSelf&gt;.</summary>
        public ScriptableVariant Parent => _variantParent;

        public bool HasParent => _variantParent != null;

        /// <summary>The serialized override paths. The returned collection is read-only.</summary>
        public IReadOnlyList<string> OverridePaths
        {
            get
            {
                NormalizeOverridePaths();
                return _variantOverrides;
            }
        }

        /// <summary>
        /// Changes whenever this asset's materialized values are recomputed.
        /// Calling this property also guarantees that inherited values are current.
        /// </summary>
        public int ResolvedRevision
        {
            get
            {
                EnsureResolved();
                return _resolvedRevision;
            }
        }

        /// <summary>
        /// Ensures that every non-overridden serialized field contains its effective inherited value.
        /// Call this before reading fields from performance-sensitive code that can run before OnEnable.
        /// </summary>
        public void EnsureResolved()
        {
            var stack = new HashSet<ScriptableVariant>(ReferenceComparer.Instance);
            EnsureResolved(stack);
        }

        /// <summary>
        /// Invalidates this asset and all currently loaded descendants. Use this after changing values from code.
        /// </summary>
        public void InvalidateResolvedData()
        {
            InvalidateHierarchy(this, new HashSet<ScriptableVariant>(ReferenceComparer.Instance));
        }

        /// <summary>Returns true when the exact property path has a local override.</summary>
        public bool IsOverridden(string propertyPath)
        {
            NormalizeOverridePaths();
            return !string.IsNullOrEmpty(propertyPath) && GetOverrideLookup().Contains(propertyPath);
        }

        /// <summary>Returns true when this path or one of its owning paths has a local override.</summary>
        public bool IsLocallyControlled(string propertyPath)
        {
            NormalizeOverridePaths();
            if (string.IsNullOrEmpty(propertyPath))
            {
                return false;
            }

            var lookup = GetOverrideLookup();
            if (lookup.Contains(propertyPath))
            {
                return true;
            }

            for (var separator = propertyPath.LastIndexOf('.'); separator > 0;
                 separator = propertyPath.LastIndexOf('.', separator - 1))
            {
                if (lookup.Contains(propertyPath.Substring(0, separator)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Returns true when at least one child path is overridden locally.</summary>
        public bool HasOverridesBelow(string propertyPath)
        {
            NormalizeOverridePaths();
            if (string.IsNullOrEmpty(propertyPath))
            {
                return false;
            }

            var prefix = propertyPath + ".";
            var paths = _variantOverrides;
            for (var i = 0; i < paths.Count; i++)
            {
                if (paths[i] != null && paths[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Called after effective values have been materialized.</summary>
        protected virtual void OnVariantResolved()
        {
        }

        protected virtual void OnEnable()
        {
            RegisterActive(this);
            _resolutionDirty = true;
            EnsureResolved();
        }

        protected virtual void OnDisable()
        {
            UnregisterActive(this);
        }

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            EditorNotifyValuesChanged();
            EnsureResolved();
        }
#endif

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (_variantOverrides == null)
            {
                _variantOverrides = new List<string>();
            }

            _overrideLookup = null;
            _overridePathsNeedNormalization = true;
            _resolutionDirty = true;
            _observedParentRevision = -1;
        }

        internal bool CanAssignParent(ScriptableVariant candidate, out string error)
        {
            if (candidate == null)
            {
                error = null;
                return true;
            }

            if (ReferenceEquals(candidate, this))
            {
                error = "A Scriptable Variant cannot inherit from itself.";
                return false;
            }

            if (!IsCompatibleParent(candidate))
            {
                error = $"Parent must have the exact type {GetType().Name}.";
                return false;
            }

            var visited = new HashSet<ScriptableVariant>(ReferenceComparer.Instance);
            for (var current = candidate; current != null; current = current._variantParent)
            {
                if (ReferenceEquals(current, this))
                {
                    error = "The selected parent would create an inheritance cycle.";
                    return false;
                }

                if (!visited.Add(current))
                {
                    error = "The selected parent already belongs to a cyclic inheritance chain.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        internal void EditorSetParent(ScriptableVariant parent)
        {
            EditorSetParent(parent, null);
        }

        internal void EditorSetParent(ScriptableVariant parent, IReadOnlyList<string> additionalOverridePaths)
        {
            if (!CanAssignParent(parent, out var error))
            {
                throw new ArgumentException(error, nameof(parent));
            }

            if (ReferenceEquals(_variantParent, parent))
            {
                return;
            }

            EnsureResolved();
            if (parent != null && additionalOverridePaths != null && additionalOverridePaths.Count > 0)
            {
                NormalizeOverridePaths();
                for (var i = 0; i < additionalOverridePaths.Count; i++)
                {
                    AddOverridePath(additionalOverridePaths[i]);
                }

                SortAndDeduplicateOverrides();
            }

            _variantParent = parent;
            _resolutionErrorLogged = false;
            InvalidateResolvedData();
            EnsureResolved();
        }

        internal void EditorFlatten()
        {
            EnsureResolved();
            _variantParent = null;
            _variantOverrides.Clear();
            _overrideLookup = null;
            _resolutionErrorLogged = false;
            InvalidateResolvedData();
            EnsureResolved();
        }

        internal void EditorSetOverride(string propertyPath, bool enabled)
        {
            if (string.IsNullOrEmpty(propertyPath) || _variantParent == null)
            {
                return;
            }

            EnsureResolved();
            NormalizeOverridePaths();

            var prefix = propertyPath + ".";
            for (var i = _variantOverrides.Count - 1; i >= 0; i--)
            {
                var existing = _variantOverrides[i];
                if (string.Equals(existing, propertyPath, StringComparison.Ordinal) ||
                    existing.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _variantOverrides.RemoveAt(i);
                }
            }

            if (enabled)
            {
                var controlledByAncestor = false;
                for (var separator = propertyPath.LastIndexOf('.'); separator > 0;
                     separator = propertyPath.LastIndexOf('.', separator - 1))
                {
                    if (GetOverrideLookup().Contains(propertyPath.Substring(0, separator)))
                    {
                        controlledByAncestor = true;
                        break;
                    }
                }

                if (!controlledByAncestor)
                {
                    _variantOverrides.Add(propertyPath);
                }
            }

            SortAndDeduplicateOverrides();
            InvalidateResolvedData();
            EnsureResolved();
        }

        internal void EditorClearOverrides()
        {
            if (_variantOverrides.Count == 0)
            {
                return;
            }

            _variantOverrides.Clear();
            _overrideLookup = null;
            InvalidateResolvedData();
            EnsureResolved();
        }

        internal void EditorOverrideAll()
        {
            if (_variantParent == null)
            {
                return;
            }

            EnsureResolved();
            _variantOverrides.Clear();

            var fields = VariantSerialization.GetRootFields(GetType());
            for (var i = 0; i < fields.Length; i++)
            {
                if (!fields[i].IsDefined(typeof(VariantLocalAttribute), true))
                {
                    _variantOverrides.Add(fields[i].Name);
                }
            }

            SortAndDeduplicateOverrides();
            InvalidateResolvedData();
            EnsureResolved();
        }

        internal void EditorRemoveOrphanOverrides()
        {
            NormalizeOverridePaths();
            for (var i = _variantOverrides.Count - 1; i >= 0; i--)
            {
                if (!VariantSerialization.IsKnownPath(GetType(), _variantOverrides[i]))
                {
                    _variantOverrides.RemoveAt(i);
                }
            }

            SortAndDeduplicateOverrides();
            InvalidateResolvedData();
            EnsureResolved();
        }

        internal string[] EditorGetOrphanOverrides()
        {
            NormalizeOverridePaths();
            var result = new List<string>();
            for (var i = 0; i < _variantOverrides.Count; i++)
            {
                if (!VariantSerialization.IsKnownPath(GetType(), _variantOverrides[i]))
                {
                    result.Add(_variantOverrides[i]);
                }
            }

            return result.ToArray();
        }

        internal string[] EditorGetOverridesAffectingSubtree(string propertyPath)
        {
            NormalizeOverridePaths();
            if (string.IsNullOrEmpty(propertyPath))
            {
                return Array.Empty<string>();
            }

            var lookup = GetOverrideLookup();
            if (lookup.Contains(propertyPath))
            {
                return new[] {propertyPath};
            }

            for (var separator = propertyPath.LastIndexOf('.'); separator > 0;
                 separator = propertyPath.LastIndexOf('.', separator - 1))
            {
                var ancestorPath = propertyPath.Substring(0, separator);
                if (lookup.Contains(ancestorPath))
                {
                    return new[] {ancestorPath};
                }
            }

            var prefix = propertyPath + ".";
            var result = new List<string>();
            for (var i = 0; i < _variantOverrides.Count; i++)
            {
                var candidate = _variantOverrides[i];
                if (candidate.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result.Add(candidate);
                }
            }

            return result.ToArray();
        }

        internal void EditorNotifyValuesChanged()
        {
            _overrideLookup = null;
            _overridePathsNeedNormalization = true;
            InvalidateResolvedData();
        }

        internal ScriptableVariant GetValueSource(string propertyPath)
        {
            EnsureResolved();
            if (_variantParent == null || IsLocallyControlled(propertyPath))
            {
                return this;
            }

            return _variantParent.GetValueSource(propertyPath);
        }

        private bool IsCompatibleParent(ScriptableVariant candidate)
        {
            return candidate == null || candidate.GetType() == GetType();
        }

        private bool EnsureResolved(HashSet<ScriptableVariant> stack)
        {
            if (_isResolving || !stack.Add(this))
            {
                LogResolutionErrorOnce("Cyclic Scriptable Variant inheritance detected.");
                return false;
            }

            _isResolving = true;
            try
            {
                NormalizeOverridePaths();

                var parent = _variantParent;
                var parentIsUsable = parent == null ||
                                     IsCompatibleParent(parent) && parent.EnsureResolved(stack);
                if (!parentIsUsable)
                {
                    LogResolutionErrorOnce("Scriptable Variant parent is incompatible or cyclic. Local values are used.");
                    parent = null;
                }

                var parentRevision = parent != null ? parent._resolvedRevision : -1;
                if (!_resolutionDirty && _observedParentRevision == parentRevision)
                {
                    return true;
                }

                if (parent != null)
                {
                    VariantSerialization.ApplyParent(parent, this, GetOverrideLookup());
                }

                _resolutionDirty = false;
                _observedParentRevision = parentRevision;
                unchecked
                {
                    _resolvedRevision++;
                }

                _resolutionErrorLogged = false;
                OnVariantResolved();
                return true;
            }
            finally
            {
                _isResolving = false;
                stack.Remove(this);
            }
        }

        private void NormalizeOverridePaths()
        {
            if (!_overridePathsNeedNormalization)
            {
                return;
            }

            _overridePathsNeedNormalization = false;
            var changed = false;
            for (var i = 0; i < _variantOverrides.Count; i++)
            {
                var oldPath = _variantOverrides[i];
                if (VariantSerialization.TryRemapFormerPath(GetType(), oldPath, out var remappedPath) &&
                    !string.Equals(oldPath, remappedPath, StringComparison.Ordinal))
                {
                    _variantOverrides[i] = remappedPath;
                    changed = true;
                }
            }

            if (changed)
            {
                SortAndDeduplicateOverrides();
            }
            else
            {
                _overrideLookup = null;
            }
        }

        private void SortAndDeduplicateOverrides()
        {
            _variantOverrides.RemoveAll(string.IsNullOrEmpty);
            _variantOverrides.Sort(StringComparer.Ordinal);

            for (var i = _variantOverrides.Count - 1; i > 0; i--)
            {
                if (string.Equals(_variantOverrides[i], _variantOverrides[i - 1], StringComparison.Ordinal))
                {
                    _variantOverrides.RemoveAt(i);
                }
            }

            _overrideLookup = null;
        }

        private void AddOverridePath(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath) ||
                !VariantSerialization.IsKnownPath(GetType(), propertyPath))
            {
                return;
            }

            for (var i = 0; i < _variantOverrides.Count; i++)
            {
                var existing = _variantOverrides[i];
                if (string.Equals(existing, propertyPath, StringComparison.Ordinal) ||
                    propertyPath.StartsWith(existing + ".", StringComparison.Ordinal))
                {
                    return;
                }
            }

            var prefix = propertyPath + ".";
            for (var i = _variantOverrides.Count - 1; i >= 0; i--)
            {
                if (_variantOverrides[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    _variantOverrides.RemoveAt(i);
                }
            }

            _variantOverrides.Add(propertyPath);
        }

        private HashSet<string> GetOverrideLookup()
        {
            if (_overrideLookup == null)
            {
                _overrideLookup = new HashSet<string>(_variantOverrides, StringComparer.Ordinal);
            }

            return _overrideLookup;
        }

        private void LogResolutionErrorOnce(string message)
        {
            if (_resolutionErrorLogged)
            {
                return;
            }

            _resolutionErrorLogged = true;
            Debug.LogError(message, this);
        }

        private static void RegisterActive(ScriptableVariant variant)
        {
            if (!ActiveVariants.Contains(variant))
            {
                ActiveVariants.Add(variant);
            }
        }

        private static void UnregisterActive(ScriptableVariant variant)
        {
            ActiveVariants.Remove(variant);
        }

        private static void InvalidateHierarchy(ScriptableVariant root, HashSet<ScriptableVariant> visited)
        {
            if (root == null || !visited.Add(root))
            {
                return;
            }

            root._resolutionDirty = true;

            for (var i = ActiveVariants.Count - 1; i >= 0; i--)
            {
                var candidate = ActiveVariants[i];
                if (candidate == null)
                {
                    ActiveVariants.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(candidate._variantParent, root))
                {
                    InvalidateHierarchy(candidate, visited);
                }
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<ScriptableVariant>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(ScriptableVariant x, ScriptableVariant y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(ScriptableVariant obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }

    /// <summary>Strongly typed convenience base for a family of variants.</summary>
    public abstract class ScriptableVariant<TSelf> : ScriptableVariant
        where TSelf : ScriptableVariant<TSelf>
    {
        public new TSelf Parent => base.Parent as TSelf;
    }
}
