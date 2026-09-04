using System;

namespace DCFApixels.ScriptableVariants
{
    /// <summary>
    /// Excludes a serialized field from variant inheritance. The field remains local on every asset.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class VariantLocalAttribute : Attribute
    {
    }
}
