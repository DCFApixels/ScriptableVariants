using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DCFApixels.ScriptableVariants.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace DCFApixels.ScriptableVariants.Tests
{
    public sealed class VariantSerializationRegressionTests
    {
        [TestCase("2026-09-05T12:34:56+05:30")]
        [TestCase("2026-09-05T12:34:56.1234567Z")]
        [TestCase("2026-09-05")]
        public void DateLookingStringsSurviveDocumentAndValueRoundTrip(string value)
        {
            var document = new VariantSourceDocument();
            document.SetValue(new VariantValueRecord
            {
                Path = "Text",
                Value = VariantValueSerializer.Serialize(value, typeof(string)),
            });

            var restored = VariantSourceDatabase.DeserializeDocument(
                VariantSourceDatabase.SerializeDocument(document));
            var token = restored.FindValue("Text").Value;

            Assert.That(token.Type, Is.EqualTo(JTokenType.String));
            Assert.That(VariantValueSerializer.Deserialize(token, typeof(string)), Is.EqualTo(value));
        }

        [Test]
        public void DocumentRejectsTrailingJson()
        {
            Assert.Throws<JsonReaderException>(() => VariantSourceDatabase.DeserializeDocument("{} {}"));
        }

        [Test]
        public void NestedCollectionsReplaceConstructorDefaults()
        {
            var data = new CollectionDefaults();
            data.Nested.Numbers.Add(2);
            for (var i = 0; i < 3; i++)
            {
                data = RoundTrip(data);
                Assert.That(data.Nested.Numbers, Is.EqualTo(new[] {1, 2}));
            }
        }

        [Test]
        public void FormerNamesWorkInsideNestedObjectsAndListElements()
        {
            var token = JObject.Parse(
                "{\"Nested\":{\"oldAmount\":17},\"Items\":[{\"oldAmount\":23}]} ");
            var data = (RenameContainer)VariantValueSerializer.Deserialize(token, typeof(RenameContainer));
            Assert.That(data.Nested.Amount, Is.EqualTo(17));
            Assert.That(data.Items[0].Amount, Is.EqualTo(23));

            var saved = VariantValueSerializer.Serialize(data, typeof(RenameContainer));
            Assert.That(saved["Nested"]["Amount"].Value<int>(), Is.EqualTo(17));
            Assert.That(saved["Nested"]["oldAmount"], Is.Null);
            Assert.That(saved["Items"][0]["oldAmount"], Is.Null);
        }

        [Test]
        public void FormerNameDoesNotShadowAnotherCurrentField()
        {
            var token = JObject.Parse("{\"Old\":5,\"Current\":9}");
            var data = (NameCollision)VariantValueSerializer.Deserialize(token, typeof(NameCollision));
            Assert.That(data.Old, Is.EqualTo(5));
            Assert.That(data.Current, Is.EqualTo(9));
        }

        [Test]
        public void UnityNativeTypesAreIncludedAndRoundTrip()
        {
            var data = new NativeValues
            {
                Curve = AnimationCurve.Linear(0f, 1f, 1f, 5f),
                Gradient = new Gradient {mode = GradientMode.Fixed, colorSpace = ColorSpace.Linear},
                Bounds = new Bounds(new Vector3(1f, 2f, 3f), new Vector3(4f, 6f, 8f)),
            };
            data.Curve.preWrapMode = WrapMode.Loop;
            data.Curve.postWrapMode = WrapMode.PingPong;
            data.Gradient.SetKeys(
                new[] {new GradientColorKey(Color.red, 0f), new GradientColorKey(Color.blue, 1f)},
                new[] {new GradientAlphaKey(0.25f, 0f), new GradientAlphaKey(1f, 1f)});

            Assert.That(VariantSerialization.GetSerializableFields(typeof(NativeValues)).Select(field => field.Name),
                Is.EquivalentTo(new[] {"Curve", "Gradient", "Bounds"}));
            var restored = RoundTrip(data);
            Assert.That(restored.Curve.Evaluate(0.5f), Is.EqualTo(3f).Within(0.0001f));
            Assert.That(restored.Curve.preWrapMode, Is.EqualTo(WrapMode.Loop));
            Assert.That(restored.Curve.postWrapMode, Is.EqualTo(WrapMode.PingPong));
            Assert.That(restored.Gradient.mode, Is.EqualTo(data.Gradient.mode));
            Assert.That(restored.Gradient.colorSpace, Is.EqualTo(data.Gradient.colorSpace));
            Assert.That(restored.Gradient.colorKeys, Is.EqualTo(data.Gradient.colorKeys));
            Assert.That(restored.Gradient.alphaKeys, Is.EqualTo(data.Gradient.alphaKeys));
            Assert.That(restored.Bounds, Is.EqualTo(data.Bounds));
        }

        [Test]
        public void RecursiveInlineSchemaReportsAnErrorInsteadOfRecursingForever()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                VariantSerialization.GetLocalPaths(typeof(RecursiveInline)));
            Assert.That(error.Message, Does.Contain("Next").And.Contain("SerializeReference"));
        }

        [Test]
        public void ReusedNonRecursiveTypesKeepEveryLocalPath()
        {
            Assert.That(VariantSerialization.GetLocalPaths(typeof(LocalContainer)),
                Is.EquivalentTo(new[] {"Left.Note", "Right.Note"}));
        }

        [Test]
        public void RecursiveManagedReferenceStillRoundTripsInsideOneRecord()
        {
            var data = new ManagedNode();
            data.Next = data;
            Assert.That(VariantSerialization.GetLocalPaths(typeof(ManagedNode)), Is.Empty);
            var restored = RoundTrip(data);
            Assert.That(restored.Next, Is.SameAs(restored));
        }

        [Test]
        public void EditingCloneDoesNotMutateSourceTokensOrOverrides()
        {
            var original = new VariantSourceDocument {ParentGuid = "parent"};
            original.OverridePaths.Add("Settings");
            original.SetValue(new VariantValueRecord {Path = "Settings", Value = JObject.Parse("{\"Amount\":3}")});
            var copy = original.Clone();
            copy.ParentGuid = null;
            copy.OverridePaths.Clear();
            copy.FindValue("Settings").Value["Amount"] = 7;

            Assert.That(original.ParentGuid, Is.EqualTo("parent"));
            Assert.That(original.OverridePaths, Is.EqualTo(new[] {"Settings"}));
            Assert.That(original.FindValue("Settings").Value["Amount"].Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void NormalizeRemovesOnlyOverridesOwnedByAnAncestor()
        {
            var document = new VariantSourceDocument
            {
                OverridePaths = new List<string> {"A.B.C", "AA.B", " A ", "A.B", "Z.X", "A", "", "Z"},
            };
            document.Normalize();
            Assert.That(document.OverridePaths, Is.EqualTo(new[] {"A", "AA.B", "Z"}));
        }

        [Test]
        public void AtomicWritesReplaceTheSourceAndLeaveNoTemporaryFiles()
        {
            WithTemporarySource(path =>
            {
                VariantSourceDatabase.WriteSourceAtomically(path, "first");
                VariantSourceDatabase.WriteSourceAtomically(path, "second — второй");
                Assert.That(File.ReadAllText(path), Is.EqualTo("second — второй"));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)), Is.EqualTo(new[] {path}));
            });
        }

        [Test]
        public void FailedAtomicReplacementPreservesTheOriginalFile()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                Assert.Ignore("This failure injection relies on Windows file-sharing locks.");
            }

            WithTemporarySource(path =>
            {
                VariantSourceDatabase.WriteSourceAtomically(path, "original");
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.That(() => VariantSourceDatabase.WriteSourceAtomically(path, "replacement"),
                        Throws.Exception);
                }

                Assert.That(File.ReadAllText(path), Is.EqualTo("original"));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)), Is.EqualTo(new[] {path}));
            });
        }

        [Test]
        public void UnrelatedContextMenuIsNotModified()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Existing action"), false, () => { });
            menu.AddSeparator(string.Empty);
            var count = menu.GetItemCount();
            var callback = typeof(ScriptableVariantContextMenu).GetMethod(
                "PopulatePropertyMenu", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(callback, Is.Not.Null);
            callback.Invoke(null, new object[] {menu, null});
            Assert.That(menu.GetItemCount(), Is.EqualTo(count));
        }

        private static T RoundTrip<T>(T value)
        {
            return (T)VariantValueSerializer.Deserialize(VariantValueSerializer.Serialize(value, typeof(T)), typeof(T));
        }

        private static void WithTemporarySource(Action<string> action)
        {
            var directory = Path.Combine(Path.GetTempPath(), "ScriptableVariantsTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                action(Path.Combine(directory, "source.svariant"));
            }
            finally
            {
                foreach (var path in Directory.GetFiles(directory))
                {
                    File.Delete(path);
                }

                Directory.Delete(directory);
            }
        }

        [Serializable]
        private sealed class CollectionDefaults
        {
            public NumbersData Nested = new NumbersData();
        }

        [Serializable]
        private sealed class NumbersData
        {
            public List<int> Numbers = new List<int> {1};
        }

        [Serializable]
        private sealed class RenameContainer
        {
            public RenamedData Nested;
            public List<RenamedData> Items;
        }

        [Serializable]
        private sealed class RenamedData
        {
            [FormerlySerializedAs("oldAmount")] public int Amount;
        }

        [Serializable]
        private sealed class NameCollision
        {
            public int Old;
            [FormerlySerializedAs("Old")] public int Current;
        }

        [Serializable]
        private sealed class NativeValues
        {
            public AnimationCurve Curve;
            public Gradient Gradient;
            public Bounds Bounds;
        }

        [Serializable]
        private sealed class RecursiveInline
        {
            public RecursiveInline Next;
        }

        [Serializable]
        private sealed class ManagedNode
        {
            [SerializeReference] public ManagedNode Next;
        }

        [Serializable]
        private sealed class LocalContainer
        {
            public LocalData Left;
            public LocalData Right;
        }

        [Serializable]
        private sealed class LocalData
        {
            [VariantLocal] public string Note;
        }
    }
}
