using System;
using System.Collections.Generic;
using System.Reflection;
using TriInspector;
using UnityEditor;
using UnityEngine;

[assembly: RegisterTriTypeProcessor(
    typeof(DCFApixels.ScriptableVariants.TriInspector.Editor.VariantTypeProcessor), 500)]

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    public sealed class VariantTypeProcessor : TriTypeProcessor
    {
        private static HashSet<Type> _variantTypes;

        public override void ProcessType(Type type, List<TriPropertyDefinition> properties)
        {
            if (_variantTypes == null)
            {
                _variantTypes = new HashSet<Type>();
                foreach (var root in TypeCache.GetTypesDerivedFrom<ScriptableVariant>())
                    if (!root.ContainsGenericParameters) Collect(root);
            }
            if (!_variantTypes.Contains(type))
            {
                return;
            }

            for (var i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                if (!property.TryGetMemberInfo(out var memberInfo) || !(memberInfo is FieldInfo))
                {
                    continue;
                }

                if (property.GetEditableAttributes().Exists(attribute => attribute is VariantPropertyAttribute))
                {
                    continue;
                }

                property.GetEditableAttributes().Add(new VariantPropertyAttribute());
                var attributes = property.GetEditableAttributes();
                for (var a = 0; a < attributes.Count; a++)
                    if (attributes[a] is HeaderAttribute || attributes[a] is SpaceAttribute)
                        attributes[a] = new VariantUnityDecoratorAttribute(attributes[a]);
            }
        }

        private static void Collect(Type type)
        {
            if (type == null || type.ContainsGenericParameters || !_variantTypes.Add(type)) return;
            if (type.IsArray) { Collect(type.GetElementType()); return; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            { Collect(type.GetGenericArguments()[0]); return; }
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) ||
                typeof(UnityEngine.Object).IsAssignableFrom(type) && !typeof(ScriptableVariant).IsAssignableFrom(type)) return;
            var fields = typeof(ScriptableVariant).IsAssignableFrom(type)
                ? VariantSerialization.GetRootFields(type) : VariantSerialization.GetSerializableFields(type);
            foreach (var field in fields)
            {
                Collect(field.FieldType);
                if (field.IsDefined(typeof(SerializeReference), true))
                {
                    var baseType = field.FieldType.IsArray ? field.FieldType.GetElementType() : field.FieldType;
                    if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(List<>))
                        baseType = baseType.GetGenericArguments()[0];
                    foreach (var derived in TypeCache.GetTypesDerivedFrom(baseType))
                        if (!derived.IsAbstract && !derived.ContainsGenericParameters &&
                            !typeof(UnityEngine.Object).IsAssignableFrom(derived) &&
                            !typeof(Delegate).IsAssignableFrom(derived) &&
                            derived.IsDefined(typeof(SerializableAttribute), false)) Collect(derived);
                }
            }
        }
    }
}
