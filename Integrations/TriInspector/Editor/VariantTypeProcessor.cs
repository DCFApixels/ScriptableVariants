using System;
using System.Collections.Generic;
using System.Reflection;
using TriInspector;

[assembly: RegisterTriTypeProcessor(
    typeof(DCFApixels.ScriptableVariants.TriInspector.Editor.VariantTypeProcessor), 500)]

namespace DCFApixels.ScriptableVariants.TriInspector.Editor
{
    public sealed class VariantTypeProcessor : TriTypeProcessor
    {
        public override void ProcessType(Type type, List<TriPropertyDefinition> properties)
        {
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
            }
        }
    }
}
