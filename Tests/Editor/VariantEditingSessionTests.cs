using System.IO;
using System.Linq;
using System.Reflection;
using DCFApixels.ScriptableVariants.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.ScriptableVariants.Tests
{
    public sealed class VariantEditingSessionTests
    {
        private const string Folder = "Assets/__VariantEditingSessionTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "__VariantEditingSessionTests");
            }

            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void InspectorTargetsImporterAndDrawsEnabledTriFields()
        {
            var asset = Create("Inspector");
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(asset));
            var editor = UnityEditor.Editor.CreateEditor(importer);
            try
            {
                Assert.That(editor.GetType().Name, Is.EqualTo("ScriptableVariantTriEditor"));
                Assert.That(((ScriptedImporterEditor)editor).showImportedObject, Is.False);
                var root = new InspectorElement(editor);
                var number = root.Query<PropertyField>().ToList()
                    .FirstOrDefault(field => field.bindingPath == "PublicNumber");
                Assert.That(number, Is.Not.Null, "The concrete variant must be drawn by Tri Inspector.");
                Assert.That(number.enabledInHierarchy, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(editor);
            }
        }

        [Test]
        public void TriWorkingCopyUsesAssetSemanticsAndNestedNativeDecoratorsHaveOneOwner()
        {
            var asset = Create("TriContext");
            var path = AssetDatabase.GetAssetPath(asset);
            using var session = VariantEditingSession.Acquire(path);
            using (var serialized = new SerializedObject(session.WorkingCopy))
                serialized.FindProperty("_nested").isExpanded = true;
            var editor = UnityEditor.Editor.CreateEditor(AssetImporter.GetAtPath(path));
            try
            {
                var root = new InspectorElement(editor);
                var tree = editor.GetType().GetField("_tree", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
                Assert.That(tree, Is.Not.Null);
                Assert.That(tree.GetType().GetProperty("TargetIsPersistent").GetValue(tree), Is.True);
                var elements = root.Query<VisualElement>().ToList();
                Assert.That(elements.Any(element => element.GetType().Name == "VariantOverrideVisualElement" &&
                    (string)element.GetType().GetField("_propertyPath", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(element) == "_nested._amount"), Is.True, "Nested fields need their own override marker.");
                Assert.That(root.Query<VisualElement>(className: "scriptable-variant-decorator").ToList(), Is.Empty,
                    "Min/TextArea select native fields; Unity, not a second Tri wrapper, must draw their headers (including VariantLocal).");
            }
            finally { Object.DestroyImmediate(editor); }
        }

        [Test]
        public void WorkingCopyIsEditableAndPublishesChangesOnlyThroughSource()
        {
            var imported = Create("Isolated");
            var path = AssetDatabase.GetAssetPath(imported);
            using var session = VariantEditingSession.Acquire(path);
            var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
            Assert.That(EditorUtility.IsPersistent(copy), Is.False);
            Assert.That(copy.hideFlags & HideFlags.NotEditable, Is.EqualTo(HideFlags.None));
            using (var serialized = new SerializedObject(copy))
            {
                Assert.That(serialized.FindProperty(nameof(copy.PublicNumber)).editable, Is.True);
                serialized.FindProperty(nameof(copy.PublicNumber)).intValue = 37;
                serialized.ApplyModifiedProperties();
            }

            session.CommitValues();
            Assert.That(imported.PublicNumber, Is.Zero, "Do not mutate the imported output in memory.");
            Assert.That(Read(path).FindValue(nameof(copy.PublicNumber)).Value.Value<int>(), Is.EqualTo(37));

            VariantSourceDatabase.ImportNow(path);
            VariantEditingSession.ReloadOpenSessions();
            Assert.That(AssetDatabase.LoadAssetAtPath<ScriptableVariantTestAsset>(path).PublicNumber, Is.EqualTo(37));
            Assert.That(session.WorkingCopy, Is.SameAs(copy), "Reimport must preserve the bound working object.");
        }

        [Test]
        public void InheritedEditAndLocalEditAreStoredWithDifferentOverrideSemantics()
        {
            var parent = Create("Parent");
            var child = Create("Child");
            var path = AssetDatabase.GetAssetPath(child);
            using var session = VariantEditingSession.Acquire(path);
            var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
            Assert.That(ScriptableVariantAssetUtility.SetParent(copy, parent, out _), Is.True);

            var before = File.ReadAllText(path);
            session.CommitValues();
            Assert.That(File.ReadAllText(path), Is.EqualTo(before), "Opening/tracking a field is not an edit.");

            copy.PublicNumber = 42;
            copy.SetNested(6, null);
            copy.SetValues(3, 4);
            copy.SetLocalNote("local edit");
            session.CommitValues();
            var document = Read(path);
            Assert.That(document.OverridePaths,
                Is.EquivalentTo(new[] {nameof(copy.PublicNumber), "_nested._amount", "_values"}));
            Assert.That(document.FindValue("_localNote").Value.Value<string>(), Is.EqualTo("local edit"));

            ScriptableVariantAssetUtility.Revert(copy, nameof(copy.PublicNumber));
            session.CommitValues();
            Assert.That(copy.PublicNumber, Is.Zero);
            Assert.That(Read(path).OverridePaths, Does.Not.Contain(nameof(copy.PublicNumber)));
        }

        [Test]
        public void ParentSourceChangesRefreshOpenChildWithoutCreatingOverrides()
        {
            var parent = Create("LiveParent");
            var child = Create("LiveChild");
            var childPath = AssetDatabase.GetAssetPath(child);
            using var childSession = VariantEditingSession.Acquire(childPath);
            var childCopy = (ScriptableVariantTestAsset)childSession.WorkingCopy;
            Assert.That(ScriptableVariantAssetUtility.SetParent(childCopy, parent, out _), Is.True);
            using (var parentSession = VariantEditingSession.Acquire(AssetDatabase.GetAssetPath(parent)))
            {
                ((ScriptableVariantTestAsset)parentSession.WorkingCopy).PublicNumber = 99;
                parentSession.CommitValues();
            }

            // Read the new source even before a scheduled dependency reimport has run.
            VariantEditingSession.ReloadOpenSessions();
            childSession.CommitValues();
            Assert.That(childCopy.PublicNumber, Is.EqualTo(99));
            Assert.That(Read(childPath).OverridePaths, Is.Empty);
            Assert.That(Read(childPath).Values.Select(value => value.Path), Is.EquivalentTo(new[] {"_localNote"}));
        }

        [Test]
        public void UndoAndRedoRestoreValuesAndOverridesAfterInspectorCloses()
        {
            var parent = Create("UndoParent");
            var child = Create("UndoChild");
            var path = AssetDatabase.GetAssetPath(child);
            using (var session = VariantEditingSession.Acquire(path))
            {
                var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
                Assert.That(ScriptableVariantAssetUtility.SetParent(copy, parent, out _), Is.True);
                Undo.IncrementCurrentGroup();
                copy.PublicNumber = 77;
                session.CommitValues();
                Undo.IncrementCurrentGroup();
            }

            Undo.PerformUndo();
            Assert.That(Read(path).OverridePaths, Is.Empty);
            using (var reopened = VariantEditingSession.Acquire(path))
            {
                Assert.That(((ScriptableVariantTestAsset)reopened.WorkingCopy).PublicNumber, Is.Zero);
                Undo.PerformRedo();
                Assert.That(((ScriptableVariantTestAsset)reopened.WorkingCopy).PublicNumber, Is.EqualTo(77));
                Assert.That(Read(path).OverridePaths, Does.Contain("PublicNumber"));
            }
        }

        [Test]
        public void ApplyToParentIsOneUndoableSourceOperation()
        {
            var parent = Create("ApplyParent");
            var child = Create("ApplyChild");
            var parentPath = AssetDatabase.GetAssetPath(parent);
            var childPath = AssetDatabase.GetAssetPath(child);
            using var session = VariantEditingSession.Acquire(childPath);
            var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
            Assert.That(ScriptableVariantAssetUtility.SetParent(copy, parent, out _), Is.True);
            copy.PublicNumber = 51;
            session.CommitValues();
            Undo.IncrementCurrentGroup();

            Assert.That(ScriptableVariantAssetUtility.ApplyToParent(copy, "PublicNumber"), Is.True);
            Undo.IncrementCurrentGroup();
            Assert.That(parent.PublicNumber, Is.Zero, "Apply must not mutate the published parent object.");
            Assert.That(Read(parentPath).FindValue("PublicNumber").Value.Value<int>(), Is.EqualTo(51));
            Assert.That(Read(childPath).OverridePaths, Is.Empty);

            Undo.PerformUndo();
            Assert.That(Read(parentPath).FindValue("PublicNumber").Value.Value<int>(), Is.Zero);
            Assert.That(Read(childPath).OverridePaths, Does.Contain("PublicNumber"));
            Assert.That(copy.PublicNumber, Is.EqualTo(51));
        }

        [Test]
        public void MultipleInspectorsShareCopyAndReleaseItAfterLastInspectorCloses()
        {
            var asset = Create("Shared");
            var path = AssetDatabase.GetAssetPath(asset);
            var first = VariantEditingSession.Acquire(path);
            var second = VariantEditingSession.Acquire(path);
            var copy = first.WorkingCopy;
            Assert.That(second.WorkingCopy, Is.SameAs(copy));
            first.Dispose();
            Assert.That(copy != null, Is.True);
            second.Dispose();
            Assert.That(copy == null, Is.True);
        }

        [Test]
        public void UndoParentAssignmentAndFlattenRestoreTheSourceGraph()
        {
            var parent = Create("GraphParent");
            var child = Create("GraphChild");
            var path = AssetDatabase.GetAssetPath(child);
            using var session = VariantEditingSession.Acquire(path);
            var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
            copy.PublicNumber = 19;
            session.CommitValues();
            Undo.IncrementCurrentGroup();

            Assert.That(ScriptableVariantAssetUtility.SetParent(copy, parent, out _), Is.True);
            Undo.IncrementCurrentGroup();
            Undo.PerformUndo();
            Assert.That(Read(path).ParentGuid, Is.Null);
            Assert.That(copy.PublicNumber, Is.EqualTo(19));
            Undo.PerformRedo();
            Assert.That(Read(path).OverridePaths, Does.Contain("PublicNumber"));

            Undo.IncrementCurrentGroup();
            ScriptableVariantAssetUtility.Flatten(copy);
            Undo.IncrementCurrentGroup();
            Assert.That(Read(path).ParentGuid, Is.Null);
            Undo.PerformUndo();
            Assert.That(ScriptableVariantAssetUtility.GetParent(copy), Is.EqualTo(parent));
            Assert.That(Read(path).OverridePaths, Does.Contain("PublicNumber"));
            Assert.That(copy.PublicNumber, Is.EqualTo(19));
        }

        private static ScriptableVariantTestAsset Create(string name)
        {
            return (ScriptableVariantTestAsset)ScriptableVariantAssetUtility.CreateRoot(
                typeof(ScriptableVariantTestAsset), Folder + "/" + name + ".svariant");
        }

        private static VariantSourceDocument Read(string path)
        {
            Assert.That(VariantSourceDatabase.TryLoadUncached(path, out var document, out var error), Is.True, error);
            return document;
        }
    }
}
