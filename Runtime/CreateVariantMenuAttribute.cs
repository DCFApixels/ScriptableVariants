using System;
using System.Diagnostics;

namespace DCFApixels.ScriptableVariants
{
    /// <summary>
    /// Customizes an entry in the Scriptable Variant creation picker, not Unity's Assets menu.
    /// Slash-separated menuName segments form groups; fileName is a filename, not a folder path.
    /// </summary>
    [Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class CreateVariantMenuAttribute : Attribute
    {
        public string menuName { get; set; }
        public string fileName { get; set; }
        public int order { get; set; }
    }
}
