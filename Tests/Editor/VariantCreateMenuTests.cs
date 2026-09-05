using System.Linq;
using DCFApixels.ScriptableVariants.Editor;
using NUnit.Framework;
using UnityEditor.UIElements;
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
                var list = window.rootVisualElement.Q<ListView>("variant-type-list");
                Assert.That(list.itemsSource.Count, Is.EqualTo(expectedCount));
                Assert.That(list.itemsSource, Does.Contain(typeof(ScriptableVariantTestAsset)));
                Assert.That(window.rootVisualElement.Q<Button>("variant-create").enabledSelf, Is.True);
                window.CreateGUI();
                Assert.That(window.rootVisualElement.Query<ListView>().ToList().Count, Is.EqualTo(1));
                Assert.That(window.rootVisualElement.Q<ListView>("variant-type-list").itemsSource.Count, Is.EqualTo(expectedCount));
                Assert.That(window.rootVisualElement.Query<Button>("variant-create").ToList().Count, Is.EqualTo(1),
                    "Rebuilding the window must not duplicate the controls.");
            }
            finally
            {
                if (window != null) Object.DestroyImmediate(window);
                Event.current = previous;
            }
        }

        [TestCase("")]
        [TestCase("  \t ")]
        [TestCase("scriptablevarianttestasset")]
        [TestCase("SCRIPTABLE VARIANT TEST ASSET")]
        [TestCase("dcfapixels asset")]
        [TestCase("DCFApixels.ScriptableVariants.Tests.Editor")]
        public void SearchMatchesNamesNamespacesAndAssemblies(string query)
        {
            var types = new[] {typeof(ScriptableVariantTestAsset)};
            Assert.That(ScriptableVariantCreateMenu.FilterTypes(types, query), Is.EqualTo(types));
        }

        [Test]
        public void SearchRequiresEveryWordToMatch()
        {
            var types = new[] {typeof(ScriptableVariantTestAsset)};
            Assert.That(ScriptableVariantCreateMenu.FilterTypes(types, "TestAsset unknown-word"), Is.Empty);
        }

        [Test]
        public void SearchFiltersTheListAndDisablesCreationWithNoMatches()
        {
            var window = ScriptableObject.CreateInstance<ScriptableVariantCreateMenu>();
            try
            {
                window.CreateGUI();
                var root = window.rootVisualElement;
                var search = root.Q<ToolbarSearchField>("variant-type-search");
                var list = root.Q<ListView>("variant-type-list");
                var create = root.Q<Button>("variant-create");
                search.value = "__no_variant_type_can_match_this__";
                Assert.That(list.itemsSource.Count, Is.Zero);
                Assert.That(list.selectedIndex, Is.EqualTo(-1));
                Assert.That(create.enabledSelf, Is.False);
                Assert.That(root.Q<Label>("variant-type-status").text, Does.Contain("No matching types"));

                search.value = nameof(ScriptableVariantTestAsset);
                Assert.That(list.itemsSource.Count, Is.EqualTo(1));
                Assert.That(list.selectedItem, Is.EqualTo(typeof(ScriptableVariantTestAsset)));
                Assert.That(create.enabledSelf, Is.True);
                var row = list.makeItem();
                list.bindItem(row, 0);
                Assert.That(row.Q<Label>("type-name").text, Does.Contain("Test Asset"));
                Assert.That(row.Q<Label>("type-context").text, Does.Contain(typeof(ScriptableVariantTestAsset).Namespace));

                search.value = string.Empty;
                Assert.That(list.selectedItem, Is.EqualTo(typeof(ScriptableVariantTestAsset)),
                    "Clearing a search must keep the selected type, not its old list index.");
            }
            finally { Object.DestroyImmediate(window); }
        }

        [TestCase("Assets", true)]
        [TestCase("Assets/Configs", true)]
        [TestCase("Packages/com.example.configs", false)]
        [TestCase("AssetsBackup/Configs", false)]
        [TestCase("Assets/../Packages", false)]
        [TestCase("Assets/./Configs", false)]
        [TestCase(null, false)]
        public void CreationIsRestrictedToUserAssetFolders(string directory, bool expected)
        {
            Assert.That(ScriptableVariantCreateMenu.IsAssetDirectory(directory), Is.EqualTo(expected));
        }

        [TestCase("Assets/Weapon.svariant", "Assets/Weapon.svariant")]
        [TestCase("Assets/Weapon.SVARIANT", "Assets/Weapon.SVARIANT")]
        [TestCase("Assets/Weapon", "Assets/Weapon.svariant")]
        [TestCase("Assets/Weapon.Fire", "Assets/Weapon.Fire.svariant")]
        public void NativeNamingAlwaysKeepsTheSourceExtension(string input, string expected)
        {
            Assert.That(VariantAssetCreationAction.EnsureSourceExtension(input), Is.EqualTo(expected));
        }
    }
}
