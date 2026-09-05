#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace DCFApixels.ScriptableVariants
{
    internal static class VariantSerialization
    {
        private static readonly Dictionary<Type, FieldInfo[]> FieldsCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, FieldInfo[]> RootFieldsCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, string[]> LocalPathsCache = new Dictionary<Type, string[]>();
        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
            "MemberwiseClone",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly object CacheLock = new object();

        internal static FieldInfo[] GetRootFields(Type variantType)
        {
            lock (CacheLock)
            {
                if (RootFieldsCache.TryGetValue(variantType, out var cached))
                {
                    return cached;
                }

                var types = new Stack<Type>();
                for (var type = variantType;
                     type != null && type != typeof(ScriptableVariant) && type != typeof(ScriptableObject);
                     type = type.BaseType)
                {
                    types.Push(type);
                }

                var fields = new List<FieldInfo>();
                while (types.Count > 0)
                {
                    AddSerializableDeclaredFields(types.Pop(), fields);
                }

                cached = fields.ToArray();
                RootFieldsCache.Add(variantType, cached);
                return cached;
            }
        }

        internal static void ApplyParent(
            ScriptableVariant parent,
            ScriptableVariant child,
            HashSet<string> overridePaths)
        {
            // Validate inline schemas before recursively merging their values.
            GetLocalPaths(child.GetType());
            var cloneContext = new Dictionary<object, object>(ObjectReferenceComparer.Instance);
            var fields = GetRootFields(child.GetType());
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var parentValue = field.GetValue(parent);
                var childValue = field.GetValue(child);
                var merged = MergeField(field, parentValue, childValue, field.Name, overridePaths, cloneContext);
                field.SetValue(child, merged);
            }
        }

        internal static bool IsKnownPath(Type rootType, string propertyPath)
        {
            return TryGetFieldPath(rootType, propertyPath, out _);
        }

        internal static bool IsAtomicOverridePath(Type rootType, string propertyPath)
        {
            if (!TryGetFieldPath(rootType, propertyPath, out var fields) || fields.Length == 0)
            {
                return false;
            }

            var field = fields[fields.Length - 1];
            return field.IsDefined(typeof(SerializeReference), true) ||
                   !IsInlineComposite(field.FieldType);
        }

        internal static bool TryRemapFormerPath(Type rootType, string oldPath, out string remappedPath)
        {
            remappedPath = oldPath;
            if (!TryGetFieldPath(rootType, oldPath, true, true, out var fields))
            {
                return false;
            }

            var segments = new string[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                segments[i] = fields[i].Name;
            }

            remappedPath = string.Join(".", segments);
            return true;
        }

        internal static bool CopyPathValue(
            ScriptableVariant source,
            ScriptableVariant destination,
            string propertyPath)
        {
            if (source == null || destination == null || source.GetType() != destination.GetType() ||
                !TryGetFieldPath(source.GetType(), propertyPath, out var fields))
            {
                return false;
            }

            var sourceContainers = new object[fields.Length];
            object sourceValue = source;
            for (var i = 0; i < fields.Length; i++)
            {
                if (sourceValue == null)
                {
                    return false;
                }

                sourceContainers[i] = sourceValue;
                sourceValue = fields[i].GetValue(sourceValue);
            }

            var cloneContext = new Dictionary<object, object>(ObjectReferenceComparer.Instance);
            var clonedValue = CloneValue(sourceValue, cloneContext);
            return TrySetPathValue(
                destination,
                sourceContainers,
                fields,
                0,
                clonedValue,
                cloneContext,
                out _);
        }

        internal static bool CanCopyPathValue(
            ScriptableVariant source,
            ScriptableVariant destination,
            string propertyPath)
        {
            if (source == null || destination == null || source.GetType() != destination.GetType() ||
                !TryGetFieldPath(source.GetType(), propertyPath, out var fields))
            {
                return false;
            }

            object sourceValue = source;
            for (var i = 0; i < fields.Length; i++)
            {
                if (sourceValue == null)
                {
                    return false;
                }

                sourceValue = fields[i].GetValue(sourceValue);
            }

            return true;
        }

        internal static bool TryGetPathValue(
            ScriptableVariant source,
            string propertyPath,
            out object value,
            out Type declaredType)
        {
            value = null;
            declaredType = null;
            if (source == null ||
                !TryGetFieldPath(source.GetType(), propertyPath, false, true, out var fields))
            {
                return false;
            }

            object current = source;
            for (var i = 0; i < fields.Length; i++)
            {
                if (current == null)
                {
                    return false;
                }

                current = fields[i].GetValue(current);
            }

            value = current;
            declaredType = fields[fields.Length - 1].FieldType;
            return true;
        }

        internal static bool TryGetPathType(Type rootType, string propertyPath, out Type declaredType)
        {
            declaredType = null;
            if (!TryGetFieldPath(rootType, propertyPath, false, true, out var fields))
            {
                return false;
            }

            declaredType = fields[fields.Length - 1].FieldType;
            return true;
        }

        internal static bool TrySetPathValue(
            ScriptableVariant destination,
            string propertyPath,
            object value)
        {
            if (destination == null ||
                !TryGetFieldPath(destination.GetType(), propertyPath, false, true, out var fields))
            {
                return false;
            }

            return TrySetPathValue(destination, fields, 0, value, out _);
        }

        internal static string GetRootPath(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return string.Empty;
            }

            var separator = propertyPath.IndexOf('.');
            return separator < 0 ? propertyPath : propertyPath.Substring(0, separator);
        }

        internal static string GetLocalControllerPath(Type rootType, string propertyPath)
        {
            if (!TryGetFieldPath(rootType, propertyPath, false, true, out var fields))
            {
                return null;
            }

            var segments = propertyPath.Split('.');
            for (var i = 0; i < fields.Length; i++)
            {
                if (!fields[i].IsDefined(typeof(VariantLocalAttribute), true))
                {
                    continue;
                }

                return string.Join(".", segments, 0, i + 1);
            }

            return null;
        }

        internal static string[] GetLocalPaths(Type rootType)
        {
            lock (CacheLock)
            {
                if (LocalPathsCache.TryGetValue(rootType, out var cached))
                {
                    return cached;
                }

                var result = new List<string>();
                CollectLocalPaths(GetRootFields(rootType), string.Empty, result, new HashSet<Type> {rootType});
                cached = result.ToArray();
                LocalPathsCache.Add(rootType, cached);
                return cached;
            }
        }

        private static object MergeField(
            FieldInfo field,
            object parentValue,
            object childValue,
            string path,
            HashSet<string> overridePaths,
            Dictionary<object, object> cloneContext)
        {
            if (field.IsDefined(typeof(VariantLocalAttribute), true) ||
                HasOverrideAtOrAbove(overridePaths, path))
            {
                return childValue;
            }

            if (field.IsDefined(typeof(SerializeReference), true) || !IsInlineComposite(field.FieldType))
            {
                return CloneValue(parentValue, cloneContext);
            }

            return MergeInlineObject(parentValue, childValue, field.FieldType, path, overridePaths, cloneContext);
        }

        private static object MergeInlineObject(
            object parentValue,
            object childValue,
            Type declaredType,
            string path,
            HashSet<string> overridePaths,
            Dictionary<object, object> cloneContext)
        {
            if (parentValue == null)
            {
                if (!HasOverrideAtOrBelow(overridePaths, path))
                {
                    return null;
                }

                return childValue;
            }

            if (childValue == null || ReferenceEquals(parentValue, childValue))
            {
                childValue = CloneValue(parentValue, cloneContext);
            }

            var valueType = declaredType.IsValueType ? declaredType : parentValue.GetType();
            var fields = GetSerializableFields(valueType);
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var childPath = path + "." + field.Name;
                var parentFieldValue = field.GetValue(parentValue);
                var childFieldValue = childValue != null ? field.GetValue(childValue) : null;
                var merged = MergeField(
                    field,
                    parentFieldValue,
                    childFieldValue,
                    childPath,
                    overridePaths,
                    cloneContext);
                field.SetValue(childValue, merged);
            }

            return childValue;
        }

        private static object CloneValue(object value, Dictionary<object, object> visited)
        {
            if (value == null)
            {
                return null;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                typeof(Object).IsAssignableFrom(type))
            {
                return value;
            }

            if (!type.IsValueType && visited.TryGetValue(value, out var existing))
            {
                return existing;
            }

            if (value is AnimationCurve curve)
            {
                var clone = new AnimationCurve(curve.keys)
                {
                    preWrapMode = curve.preWrapMode,
                    postWrapMode = curve.postWrapMode,
                };
                visited[value] = clone;
                return clone;
            }

            if (value is Gradient gradient)
            {
                var clone = new Gradient
                {
                    mode = gradient.mode,
                    colorSpace = gradient.colorSpace,
                };
                clone.SetKeys(gradient.colorKeys, gradient.alphaKeys);
                visited[value] = clone;
                return clone;
            }

            if (type.IsArray)
            {
                var source = (Array)value;
                var elementType = type.GetElementType();
                var clone = Array.CreateInstance(elementType, source.Length);
                visited[value] = clone;
                for (var i = 0; i < source.Length; i++)
                {
                    clone.SetValue(CloneValue(source.GetValue(i), visited), i);
                }

                return clone;
            }

            if (value is IList sourceList)
            {
                IList cloneList;
                try
                {
                    cloneList = (IList)Activator.CreateInstance(type, true);
                }
                catch
                {
                    var elementType = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
                    cloneList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
                }

                visited[value] = cloneList;
                for (var i = 0; i < sourceList.Count; i++)
                {
                    cloneList.Add(CloneValue(sourceList[i], visited));
                }

                return cloneList;
            }

            if (type.IsValueType && IsUnityNamespace(type))
            {
                return value;
            }

            var cloneObject = type.IsValueType ? value : MemberwiseClone(value);
            if (!type.IsValueType)
            {
                visited[value] = cloneObject;
            }

            var fields = GetSerializableFields(type);
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                field.SetValue(cloneObject, CloneValue(field.GetValue(value), visited));
            }

            return cloneObject;
        }

        private static object MemberwiseClone(object value)
        {
            return MemberwiseCloneMethod.Invoke(value, null);
        }

        internal static FieldInfo[] GetSerializableFields(Type type)
        {
            lock (CacheLock)
            {
                if (FieldsCache.TryGetValue(type, out var cached))
                {
                    return cached;
                }

                var hierarchy = new Stack<Type>();
                for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                {
                    hierarchy.Push(current);
                }

                var fields = new List<FieldInfo>();
                while (hierarchy.Count > 0)
                {
                    AddSerializableDeclaredFields(hierarchy.Pop(), fields);
                }

                cached = fields.ToArray();
                FieldsCache.Add(type, cached);
                return cached;
            }
        }

        private static void AddSerializableDeclaredFields(Type type, List<FieldInfo> result)
        {
            var fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Array.Sort(fields, (left, right) => left.MetadataToken.CompareTo(right.MetadataToken));

            for (var i = 0; i < fields.Length; i++)
            {
                if (IsSerializedField(fields[i]))
                {
                    result.Add(fields[i]);
                }
            }
        }

        private static bool IsSerializedField(FieldInfo field)
        {
            if (field.IsStatic || field.IsInitOnly || field.IsLiteral || field.IsNotSerialized)
            {
                return false;
            }

            var explicitlySerialized = field.IsDefined(typeof(SerializeField), true) ||
                                       field.IsDefined(typeof(SerializeReference), true);
            if (!field.IsPublic && !explicitlySerialized)
            {
                return false;
            }

            return field.IsDefined(typeof(SerializeReference), true) || IsSerializableType(field.FieldType, false);
        }

        internal static bool IsSerializableType(Type type, bool insideCollection)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            {
                return true;
            }

            if (typeof(Object).IsAssignableFrom(type))
            {
                return true;
            }

            // Unity serializes these native classes even though they do not have [Serializable].
            if (type == typeof(AnimationCurve) || type == typeof(Gradient))
            {
                return true;
            }

            if (type.IsArray)
            {
                return !insideCollection && type.GetArrayRank() == 1 &&
                       IsSerializableType(type.GetElementType(), true);
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                return !insideCollection && IsSerializableType(type.GetGenericArguments()[0], true);
            }

            return type.IsDefined(typeof(SerializableAttribute), false);
        }

        private static bool IsInlineComposite(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type.IsArray ||
                typeof(Object).IsAssignableFrom(type))
            {
                return false;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                return false;
            }

            return !IsUnityNamespace(type) && type.IsDefined(typeof(SerializableAttribute), false);
        }

        private static bool IsUnityNamespace(Type type)
        {
            return type.Namespace != null && type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal);
        }

        private static bool HasOverrideAtOrAbove(HashSet<string> overrides, string path)
        {
            if (overrides.Contains(path))
            {
                return true;
            }

            for (var separator = path.LastIndexOf('.'); separator > 0;
                 separator = path.LastIndexOf('.', separator - 1))
            {
                if (overrides.Contains(path.Substring(0, separator)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasOverrideAtOrBelow(HashSet<string> overrides, string path)
        {
            if (overrides.Contains(path))
            {
                return true;
            }

            var prefix = path + ".";
            foreach (var candidate in overrides)
            {
                if (candidate.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetFieldPath(Type rootType, string propertyPath, out FieldInfo[] fields)
        {
            return TryGetFieldPath(rootType, propertyPath, false, false, out fields);
        }

        private static bool TryGetFieldPath(
            Type rootType,
            string propertyPath,
            bool allowFormerNames,
            bool allowVariantLocal,
            out FieldInfo[] fields)
        {
            fields = null;
            if (string.IsNullOrEmpty(propertyPath) || propertyPath.Contains("Array.data["))
            {
                return false;
            }

            var segments = propertyPath.Split('.');
            var result = new FieldInfo[segments.Length];
            var currentType = rootType;

            for (var i = 0; i < segments.Length; i++)
            {
                var candidates = i == 0 ? GetRootFields(currentType) : GetSerializableFields(currentType);
                var field = FindField(candidates, segments[i], allowFormerNames);
                if (field == null ||
                    !allowVariantLocal && field.IsDefined(typeof(VariantLocalAttribute), true))
                {
                    return false;
                }

                result[i] = field;
                if (i == segments.Length - 1)
                {
                    continue;
                }

                if (field.IsDefined(typeof(SerializeReference), true) || !IsInlineComposite(field.FieldType))
                {
                    return false;
                }

                currentType = field.FieldType;
            }

            fields = result;
            return true;
        }

        private static bool TrySetPathValue(
            object target,
            object[] sourceContainers,
            FieldInfo[] fields,
            int fieldIndex,
            object value,
            Dictionary<object, object> cloneContext,
            out object updatedTarget)
        {
            updatedTarget = target;
            if (target == null)
            {
                return false;
            }

            var field = fields[fieldIndex];
            if (fieldIndex == fields.Length - 1)
            {
                field.SetValue(target, value);
                updatedTarget = target;
                return true;
            }

            var nestedTarget = field.GetValue(target);
            if (nestedTarget == null)
            {
                var sourceNestedTarget = field.GetValue(sourceContainers[fieldIndex]);
                nestedTarget = CloneValue(sourceNestedTarget, cloneContext);
                if (nestedTarget == null)
                {
                    return false;
                }
            }

            if (!TrySetPathValue(
                    nestedTarget,
                    sourceContainers,
                    fields,
                    fieldIndex + 1,
                    value,
                    cloneContext,
                    out var updatedNestedTarget))
            {
                return false;
            }

            field.SetValue(target, updatedNestedTarget);
            updatedTarget = target;
            return true;
        }

        private static bool TrySetPathValue(
            object target,
            FieldInfo[] fields,
            int fieldIndex,
            object value,
            out object updatedTarget)
        {
            updatedTarget = target;
            if (target == null)
            {
                return false;
            }

            var field = fields[fieldIndex];
            if (fieldIndex == fields.Length - 1)
            {
                field.SetValue(target, value);
                updatedTarget = target;
                return true;
            }

            var nestedTarget = field.GetValue(target);
            if (nestedTarget == null)
            {
                try
                {
                    nestedTarget = Activator.CreateInstance(field.FieldType, true);
                }
                catch
                {
                    return false;
                }
            }

            if (!TrySetPathValue(nestedTarget, fields, fieldIndex + 1, value, out var updatedNestedTarget))
            {
                return false;
            }

            field.SetValue(target, updatedNestedTarget);
            updatedTarget = target;
            return true;
        }

        private static void CollectLocalPaths(
            FieldInfo[] fields,
            string prefix,
            List<string> result,
            HashSet<Type> ancestors)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var path = string.IsNullOrEmpty(prefix) ? field.Name : prefix + "." + field.Name;
                if (field.IsDefined(typeof(VariantLocalAttribute), true))
                {
                    result.Add(path);
                    continue;
                }

                if (!field.IsDefined(typeof(SerializeReference), true) && IsInlineComposite(field.FieldType))
                {
                    if (!ancestors.Add(field.FieldType))
                    {
                        throw new InvalidOperationException(
                            $"Recursive inline field '{path}' is not supported by Scriptable Variants. " +
                            "Use [SerializeReference] for recursive data graphs.");
                    }

                    CollectLocalPaths(GetSerializableFields(field.FieldType), path, result, ancestors);
                    ancestors.Remove(field.FieldType);
                }
            }
        }

        private static FieldInfo FindField(FieldInfo[] fields, string name, bool allowFormerNames)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (string.Equals(fields[i].Name, name, StringComparison.Ordinal))
                {
                    return fields[i];
                }
            }

            if (!allowFormerNames)
            {
                return null;
            }

            for (var i = 0; i < fields.Length; i++)
            {
                var formerNames = fields[i].GetCustomAttributes<FormerlySerializedAsAttribute>(true);
                foreach (var formerName in formerNames)
                {
                    if (string.Equals(formerName.oldName, name, StringComparison.Ordinal))
                    {
                        return fields[i];
                    }
                }
            }

            return null;
        }

        private sealed class ObjectReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ObjectReferenceComparer Instance = new ObjectReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
#endif
