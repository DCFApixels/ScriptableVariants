using System.Linq;
using DCFApixels.ScriptableVariants.Editor;
using NUnit.Framework;
using UnityEditor;

namespace DCFApixels.ScriptableVariants.Tests
{
    public sealed class ScriptableVariantTests
    {
        private const string TestFolder = "Assets/__ScriptableVariantTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.CreateFolder("Assets", "__ScriptableVariantTests");
            }
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TestFolder);
        }

        [Test]
        public void CustomSourceImportsConcreteScriptableObject()
        {
            var asset = CreateRoot("Imported");

            Assert.That(asset, Is.Not.Null);
            Assert.That(asset, Is.TypeOf<ScriptableVariantTestAsset>());
            Assert.That(AssetDatabase.GetAssetPath(asset), Does.EndWith(".svariant"));
        }

        [Test]
        public void PublicFieldEditPersistsThroughReimport()
        {
            var asset = CreateRoot("PublicField");
            asset.PublicNumber = 37;
            ScriptableVariantAssetUtility.NotifyValuesChanged(asset, nameof(asset.PublicNumber));

            asset = Reimport(asset);

            Assert.That(asset.PublicNumber, Is.EqualTo(37));
        }

        [Test]
        public void AssigningParentMarksEveryDifferentPropertyAsOverride()
        {
            var parent = CreateRoot("Parent");
            parent.PublicNumber = 12;
            parent.SetNested(5, "parent");
            parent.SetValues(1, 2, 3);
            parent = Persist(parent);

            var child = CreateRoot("Child");
            child.PublicNumber = 37;
            child.SetNested(5, "child");
            child.SetValues(7, 8);
            child.SetLocalNote("child local");
            child = Persist(child);

            Assert.That(ScriptableVariantAssetUtility.SetParent(child, parent, out var error), Is.True);
            Assert.That(error, Is.Null);
            child = Reimport(child);

            Assert.That(child.PublicNumber, Is.EqualTo(37));
            Assert.That(child.NestedAmount, Is.EqualTo(5));
            Assert.That(child.NestedLabel, Is.EqualTo("child"));
            Assert.That(child.Values, Is.EqualTo(new[] {7, 8}));
            Assert.That(child.LocalNote, Is.EqualTo("child local"));
            Assert.That(ScriptableVariantAssetUtility.IsOverridden(child, nameof(child.PublicNumber)), Is.True);
            Assert.That(ScriptableVariantAssetUtility.IsOverridden(child, "_nested._amount"), Is.False);
            Assert.That(ScriptableVariantAssetUtility.IsOverridden(child, "_nested._label"), Is.True);
            Assert.That(ScriptableVariantAssetUtility.IsOverridden(child, "_values"), Is.True);
            Assert.That(ScriptableVariantAssetUtility.IsOverridden(child, "_localNote"), Is.False);
        }

        [Test]
        public void ChildSourceContainsOnlyOverridesAndLocalValues()
        {
            var parent = CreateRoot("CompactParent");
            var child = CreateRoot("CompactChild");
            parent.PublicNumber = 4;
            child.PublicNumber = 4;
            parent.SetNested(3, "parent");
            child.SetNested(3, "parent");
            parent.SetValues(1, 2);
            child.SetValues(1, 2);
            child.SetLocalNote("kept locally");
            parent = Persist(parent);
            child = Persist(child);

            Assert.That(ScriptableVariantAssetUtility.SetParent(child, parent, out _), Is.True);
            Assert.That(
                VariantSourceDatabase.TryLoad(child, out var document, out _, out var error),
                Is.True,
                error);

            Assert.That(document.OverridePaths, Is.Empty);
            Assert.That(document.Values.Select(record => record.Path), Is.EquivalentTo(new[] {"_localNote"}));
        }

        [Test]
        public void ParentChangesAreMaterializedWhenChildIsReimported()
        {
            var parent = CreateRoot("LiveParent");
            var child = CreateRoot("LiveChild");
            parent.PublicNumber = 12;
            child.PublicNumber = 12;
            parent = Persist(parent);
            child = Persist(child);
            Assert.That(ScriptableVariantAssetUtility.SetParent(child, parent, out _), Is.True);
            child = Reimport(child);
            Assert.That(child.PublicNumber, Is.EqualTo(12));

            parent = AssetDatabase.LoadAssetAtPath<ScriptableVariantTestAsset>(
                TestFolder + "/LiveParent.svariant");
            var childPath = AssetDatabase.GetAssetPath(child);
            parent.PublicNumber = 99;
            ScriptableVariantAssetUtility.NotifyValuesChanged(parent, nameof(parent.PublicNumber));
            Reimport(parent);
            VariantSourceDatabase.ImportNow(childPath);
            child = AssetDatabase.LoadAssetAtPath<ScriptableVariantTestAsset>(childPath);

            Assert.That(child.PublicNumber, Is.EqualTo(99));
        }

        private static ScriptableVariantTestAsset CreateRoot(string name)
        {
            return (ScriptableVariantTestAsset)ScriptableVariantAssetUtility.CreateRoot(
                typeof(ScriptableVariantTestAsset),
                TestFolder + "/" + name + ".svariant");
        }

        private static ScriptableVariantTestAsset Persist(ScriptableVariantTestAsset asset)
        {
            ScriptableVariantAssetUtility.NotifyValuesChanged(asset);
            return Reimport(asset);
        }

        private static ScriptableVariantTestAsset Reimport(ScriptableVariantTestAsset asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            VariantSourceDatabase.ImportNow(path);
            return AssetDatabase.LoadAssetAtPath<ScriptableVariantTestAsset>(path);
        }
    }
}
