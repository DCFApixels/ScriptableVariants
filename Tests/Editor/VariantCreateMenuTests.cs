using System.Linq;
using DCFApixels.ScriptableVariants.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.Tests
{
    public sealed class VariantCreateMenuTests
    {
        [Test]
        public void TypeChoicesContainConcreteVariantsAndExcludeAbstractOrOpenGenericTypes()
        {
            var types = ScriptableVariantCreateMenu.GetVariantTypes();
            Assert.That(types, Does.Contain(typeof(ScriptableVariantTestAsset)));
            Assert.That(types.All(type => typeof(ScriptableVariant).IsAssignableFrom(type) &&
                !type.IsAbstract && !type.ContainsGenericParameters), Is.True);
        }

        [Test]
        public void TypePickerBuildsWithoutAnImguiEventAndCanBeRebuilt()
        {
            var previous = Event.current;
            ScriptableVariantCreateMenu window = null;
            try
            {
                Event.current = null;
                window = ScriptableObject.CreateInstance<ScriptableVariantCreateMenu>();
                window.CreateGUI();
                var expectedCount = ScriptableVariantCreateMenu.GetVariantTypes().Length;
                var buttons = window.rootVisualElement.Query<Button>().ToList();
                Assert.That(buttons.Count, Is.EqualTo(expectedCount));
                Assert.That(buttons.Any(button => button.tooltip == typeof(ScriptableVariantTestAsset).FullName), Is.True);
                window.CreateGUI();
                Assert.That(window.rootVisualElement.Query<Button>().ToList().Count, Is.EqualTo(expectedCount),
                    "Rebuilding the window must not duplicate the type choices.");
            }
            finally
            {
                if (window != null) Object.DestroyImmediate(window);
                Event.current = previous;
            }
        }
    }
}
