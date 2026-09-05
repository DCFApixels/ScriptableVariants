using UnityEngine;

namespace DCFApixels.ScriptableVariants
{
    /// <summary>
    /// Marker base type for ScriptableObjects produced from .svariant source assets.
    /// Variant authoring and inheritance exist only in the Unity Editor; imported objects are flat.
    /// </summary>
    public abstract class ScriptableVariant : ScriptableObject
    {
    }
}
