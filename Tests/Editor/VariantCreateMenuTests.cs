using System;
using System.Collections.Generic;
using System.Linq;
using DCFApixels.ScriptableVariants.Editor;
using NUnit.Framework;
using UnityEditor;
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
                var root = window.rootVisualElement;
                Assert.That(root.Query<TreeView>().ToList().Count, Is.EqualTo(1));
                Assert.That(root.Query<ListView>().ToList().Count, Is.EqualTo(1));
                Assert.That(root.Q<ListView>("variant-type-search-results").style.display.value,
                    Is.EqualTo(DisplayStyle.None));
                Assert.That(root.Q<Button>("variant-create").enabledSelf, Is.False);

                window.CreateGUI();
                Assert.That(root.Query<TreeView>().ToList().Count, Is.EqualTo(1));
                Assert.That(root.Query<ListView>().ToList().Count, Is.EqualTo(1));
                Assert.That(ScriptableVariantCreateMenu.BuildMenu(
                    ScriptableVariantCreateMenu.GetVariantTypes()).Leaves.Length, Is.EqualTo(expectedCount));
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                Event.current = previous;
            }
        }

        [Test]
        public void MenuBuildsNestedGroupsAndMergesTheirCommonPath()
        {
            var model = ScriptableVariantCreateMenu.BuildMenu(
                new[] {typeof(Rifle), typeof(Pistol), typeof(PlainConfig)});
            Assert.That(model.GroupCount, Is.EqualTo(2));
            Assert.That(model.Leaves.Length, Is.EqualTo(3));
            Assert.That(model.Roots.Select(item => item.data.Name),
                Is.EqualTo(new[] {"Plain Config", "Configs"}));

            var configs = model.Roots.Single(item => item.data.Name == "Configs");
            var weapons = configs.children.Single();
            Assert.That(weapons.data.Name, Is.EqualTo("Weapons"));
            Assert.That(weapons.children.Select(item => item.data.Name),
                Is.EqualTo(new[] {"Pistol", "Rifle"}));

            var rifle = model.Leaves.Single(item => item.Type == typeof(Rifle));
            Assert.That(rifle.MenuPath, Is.EqualTo("Configs/Weapons/Rifle"));
            Assert.That(rifle.FileName, Is.EqualTo("New Rifle"));
        }

        [Test]
        public void SearchFlattensOnlyCreationOptionsAndMatchesTheirPaths()
        {
            var model = ScriptableVariantCreateMenu.BuildMenu(
                new[] {typeof(Rifle), typeof(Pistol), typeof(PlainConfig)});
            var matches = ScriptableVariantCreateMenu.FilterItems(model.Leaves, "configs rifle");
            Assert.That(matches.Select(item => item.Type), Is.EqualTo(new[] {typeof(Rifle)}));
            Assert.That(matches.All(item => !item.IsGroup), Is.True);
            Assert.That(ScriptableVariantCreateMenu.FilterItems(model.Leaves, "Weapons").Length, Is.EqualTo(2));
            Assert.That(ScriptableVariantCreateMenu.FilterItems(
                model.Leaves, typeof(Rifle).Assembly.GetName().Name), Is.Empty,
                "Search should use creation-option paths, not type assembly metadata.");
        }

        [Test]
        public void SearchModeShowsFullPathAndNeverAddsGroupRows()
        {
            var window = ScriptableObject.CreateInstance<ScriptableVariantCreateMenu>();
            try
            {
                window.Show();
                window.CreateGUI();
                var root = window.rootVisualElement;
                var search = root.Q<ToolbarSearchField>("variant-type-search");
                var list = root.Q<ListView>("variant-type-search-results");
                var tree = root.Q<TreeView>("variant-type-tree");
                var create = root.Q<Button>("variant-create");

                search.value = "__no_creation_option_can_match_this__";
                Assert.That(list.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(tree.style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(list.itemsSource.Count, Is.Zero);
                Assert.That(create.enabledSelf, Is.False);
                Assert.That(root.Q<Label>("variant-type-status").text,
                    Does.Contain("No matching creation options"));

                search.value = "Scriptable Variant Test Asset";
                Assert.That(list.itemsSource.Count, Is.EqualTo(1));
                var item = (ScriptableVariantCreateMenu.VariantMenuItem)list.selectedItem;
                Assert.That(item.Type, Is.EqualTo(typeof(ScriptableVariantTestAsset)));
                Assert.That(item.IsGroup, Is.False);
                Assert.That(create.enabledSelf, Is.True);
                var row = list.makeItem();
                list.bindItem(row, 0);
                Assert.That(row.Q<Label>("type-context").text,
                    Does.StartWith("Scriptable Variant Test Asset · "));

                search.value = string.Empty;
                Assert.That(tree.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(list.style.display.value, Is.EqualTo(DisplayStyle.None));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void EmptyAndUnannotatedMenuNamesFallBackToNicifiedTypeNames()
        {
            var model = ScriptableVariantCreateMenu.BuildMenu(
                new[] {typeof(PlainConfig), typeof(EmptyPath), typeof(DerivedConfig)});
            foreach (var type in new[] {typeof(PlainConfig), typeof(EmptyPath), typeof(DerivedConfig)})
            {
                var item = model.Leaves.Single(entry => entry.Type == type);
                Assert.That(item.Name, Is.EqualTo(ObjectNames.NicifyVariableName(type.Name)));
                Assert.That(item.MenuPath, Is.EqualTo(item.Name));
                Assert.That(item.FileName, Is.Null);
            }
        }

        [Test]
        public void MenuPathsIgnoreWhitespaceAndEmptySegments()
        {
            var model = ScriptableVariantCreateMenu.BuildMenu(new[] {typeof(SpacedPath)});
            Assert.That(model.Leaves.Single().MenuPath, Is.EqualTo("Configs/Weapons/Special"));
            Assert.That(model.GroupCount, Is.EqualTo(2));
        }

        [Test]
        public void MenuOrderingAndIdsAreStableRegardlessOfInputOrder()
        {
            var types = new[] {typeof(Rifle), typeof(Pistol), typeof(PlainConfig)};
            var first = ScriptableVariantCreateMenu.BuildMenu(types);
            var second = ScriptableVariantCreateMenu.BuildMenu(types.Reverse().ToArray());
            Assert.That(Describe(first.Roots), Is.EqualTo(Describe(second.Roots)));
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

        [TestCase(null, "Plain Config.svariant")]
        [TestCase("  ", "Plain Config.svariant")]
        [TestCase(" New Rifle ", "New Rifle.svariant")]
        [TestCase("Weapon.svariant", "Weapon.svariant")]
        public void AttributeControlsTheDefaultFilename(string input, string expected)
        {
            Assert.That(VariantAssetCreationAction.GetDefaultFileName(typeof(PlainConfig), input),
                Is.EqualTo(expected));
        }

        [TestCase("../Weapon")]
        [TestCase("Configs/Weapon")]
        [TestCase("Configs\\Weapon")]
        [TestCase(".")]
        [TestCase("..")]
        public void AttributeFilenameCannotChangeTheTargetFolder(string input)
        {
            Assert.Throws<ArgumentException>(() =>
                VariantAssetCreationAction.GetDefaultFileName(typeof(PlainConfig), input));
        }

        private static IEnumerable<string> Describe(
            IEnumerable<TreeViewItemData<ScriptableVariantCreateMenu.VariantMenuItem>> items)
        {
            foreach (var item in items)
            {
                yield return item.id + ":" + item.data.MenuPath + ":" + item.data.Type?.FullName;
                foreach (var child in Describe(item.children)) yield return child;
            }
        }

        private sealed class PlainConfig { }

        [CreateVariantMenu(menuName = "Configs/Weapons/Rifle", fileName = "New Rifle", order = 20)]
        private sealed class Rifle { }

        [CreateVariantMenu(menuName = "Configs/Weapons/Pistol", order = 10)]
        private sealed class Pistol { }

        [CreateVariantMenu(menuName = " / / ")]
        private sealed class EmptyPath { }

        [CreateVariantMenu(menuName = " / Configs // Weapons / Special / ")]
        private sealed class SpacedPath { }

        [CreateVariantMenu(menuName = "Inherited/Should Not Appear", fileName = "Should Not Inherit")]
        private class BaseConfig { }

        private sealed class DerivedConfig : BaseConfig { }
    }
}
