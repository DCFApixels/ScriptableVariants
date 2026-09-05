using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DCFApixels.ScriptableVariants.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DCFApixels.ScriptableVariants.Tests
{
    public sealed class VariantHardeningTests
    {
        private const string Folder = "Assets/__VariantHardeningTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__VariantHardeningTests");
            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(Folder);

        [Test]
        public void DocumentRejectsDuplicateJsonKeysAndDuplicateRecords()
        {
            Assert.Throws<JsonReaderException>(() => VariantSourceDatabase.DeserializeDocument("{\"formatVersion\":1,\"formatVersion\":2}"));
            var document = new VariantSourceDocument();
            document.Values.Add(new VariantValueRecord {Path = "A", Value = new JValue(1)});
            document.Values.Add(new VariantValueRecord {Path = "A", Value = new JValue(2)});
            Assert.Throws<JsonSerializationException>(() => document.Normalize());
        }

        [Test]
        public void ExplicitNullIsDistinctFromAMissingRecordValue()
        {
            var document = new VariantSourceDocument();
            document.SetValue(new VariantValueRecord {Path = "Reference", Value = JValue.CreateNull()});
            var restored = VariantSourceDatabase.DeserializeDocument(VariantSourceDatabase.SerializeDocument(document));
            Assert.DoesNotThrow(() => restored.Normalize());
            Assert.That(restored.FindValue("Reference").Value.Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void UnknownPolymorphicTypesCannotBeInstantiated()
        {
            var token = new JObject { ["$type"] = typeof(Version).AssemblyQualifiedName };
            Assert.Throws<JsonSerializationException>(() => VariantValueSerializer.Deserialize(token, typeof(ScriptableVariantTestNode)));
        }

        [Test]
        public void UnresolvedAndMalformedReferencesDoNotSilentlyBecomeNull()
        {
            Assert.Throws<JsonSerializationException>(() => VariantValueSerializer.Deserialize(
                JObject.Parse("{\"$ref\":\"missing\"}"), typeof(ScriptableVariantTestNode)));
            Assert.Throws<JsonSerializationException>(() => VariantValueSerializer.Deserialize(
                JObject.Parse("{\"$unityObject\":\"broken\"}"), typeof(Object)));
            Assert.Throws<JsonSerializationException>(() => VariantValueSerializer.Deserialize(JValue.CreateNull(), typeof(int)));
        }

        [Test]
        public void SharedManagedGraphSurvivesAcrossStoredFields()
        {
            var asset = ScriptableObject.CreateInstance<ScriptableVariantTestAsset>();
            try
            {
                asset.A = asset.B = new ScriptableVariantTestNode {Amount = 31};
                asset.A.Next = asset.A;
                var document = new VariantSourceDocument();
                VariantValueSerializer.CaptureValues(document, asset, new[] {"B", "A"});
                document = VariantSourceDatabase.DeserializeDocument(VariantSourceDatabase.SerializeDocument(document));
                var restored = VariantValueSerializer.ReadValues(document, asset.GetType(), new[] {"A", "B"});
                var a = (ScriptableVariantTestNode)restored["A"];
                Assert.That(document.FormatVersion, Is.EqualTo(VariantSourceDocument.CurrentFormatVersion));
                Assert.That(restored["B"], Is.SameAs(a));
                Assert.That(a.Next, Is.SameAs(a));
                Assert.That(a.Amount, Is.EqualTo(31));
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test]
        public void GraphDefinitionsAreReadBeforeReferencesEvenAfterRecordRenames()
        {
            var document = new VariantSourceDocument();
            document.SetValue(new VariantValueRecord {Path = "A", Value = JObject.Parse("{\"$ref\":\"node\"}")});
            document.SetValue(new VariantValueRecord {Path = "B", Value = JObject.Parse("{\"$id\":\"node\",\"Amount\":17}")});
            var values = VariantValueSerializer.ReadValues(document, typeof(ScriptableVariantTestAsset), new[] {"A", "B"});
            Assert.That(values["A"], Is.SameAs(values["B"]));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void LegacyDocumentVersionsAreRejectedWithoutMigration(int version)
        {
            var document = new VariantSourceDocument {FormatVersion = version};
            Assert.Throws<JsonSerializationException>(() => document.Normalize());
            Assert.Throws<JsonSerializationException>(() => VariantValueSerializer.ReadValues(
                document, typeof(ScriptableVariantTestAsset), new[] {"A", "B"}));
            Assert.That(document.FormatVersion, Is.EqualTo(version));
        }

        [Test]
        public void IntegerVectorsAndNativeOffsetsAreNotSerializedAsEmptyObjects()
        {
            var vector = new Vector3Int(11, 22, 33);
            Assert.That(VariantValueSerializer.Deserialize(VariantValueSerializer.Serialize(vector, typeof(Vector3Int)), typeof(Vector3Int)), Is.EqualTo(vector));
            var offset = new RectOffset(1, 2, 3, 4);
            var restored = (RectOffset)VariantValueSerializer.Deserialize(
                VariantValueSerializer.Serialize(offset, typeof(RectOffset)), typeof(RectOffset));
            Assert.That(new[] {restored.left, restored.right, restored.top, restored.bottom}, Is.EqualTo(new[] {1, 2, 3, 4}));
        }

        [Test]
        public void LocalAttributeInsideAtomicCollectionIsRejectedExplicitly()
        {
            Assert.That(() => VariantSerialization.GetLocalPaths(typeof(InvalidAtomicLocal)),
                Throws.InvalidOperationException.With.Message.Contains("atomic"));
        }

        [Test]
        public void ExternalEditWithSameTimestampCannotBeOverwritten()
        {
            var path = Create("Conflict");
            using var session = VariantEditingSession.Acquire(path);
            var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
            var stamp = File.GetLastWriteTimeUtc(path);
            var external = Read(path).Clone();
            external.SetValue(new VariantValueRecord {Path = "PublicNumber", Value = new JValue(9)});
            var text = VariantSourceDatabase.SerializeDocument(external);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, stamp);
            copy.PublicNumber = 7;
            Assert.Throws<IOException>(() => session.CommitValues());
            Assert.That(File.ReadAllText(path), Is.EqualTo(text));
            VariantEditingSession.ReloadOpenSessions();
            Assert.That(copy.PublicNumber, Is.EqualTo(7), "Reload must retain a dirty Inspector.");
            session.ReloadDiscardingChanges();
            Assert.That(copy.PublicNumber, Is.EqualTo(9));
        }

        [Test]
        public void ClosingFailedSessionKeepsItsWorkingCopy()
        {
            var path = Create("Retained");
            var session = VariantEditingSession.Acquire(path);
            var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
            copy.PublicNumber = 73;
            File.AppendAllText(path, " "); // exact revision conflict, even though JSON still parses
            Assert.Throws<IOException>(() => session.CommitValues());
            session.Dispose();
            Assert.That(copy != null, Is.True);
            using var reopened = VariantEditingSession.Acquire(path);
            Assert.That(reopened.WorkingCopy, Is.SameAs(copy));
            Assert.That(copy.PublicNumber, Is.EqualTo(73));
            reopened.ReloadDiscardingChanges();
        }

        [Test]
        public void SourceBatchPreflightsEveryRevisionBeforeWriting()
        {
            var first = Create("PreflightA");
            var second = Create("PreflightB");
            var a = Read(first).Clone();
            var b = Read(second).Clone();
            a.SetValue(new VariantValueRecord {Path = "PublicNumber", Value = new JValue(1)});
            b.SetValue(new VariantValueRecord {Path = "PublicNumber", Value = new JValue(2)});
            var original = File.ReadAllText(first);
            File.AppendAllText(second, " ");
            Assert.Throws<IOException>(() => VariantSourceDatabase.SaveBatch(new[] {first, second}, new[] {a, b}));
            Assert.That(File.ReadAllText(first), Is.EqualTo(original));
        }

        [Test]
        public void FailedSecondReplacementRollsBackFirstSource()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Windows file-sharing failure injection.");
            var first = Create("RollbackA");
            var second = Create("RollbackB");
            var a = Read(first).Clone();
            var b = Read(second).Clone();
            var originalA = File.ReadAllText(first);
            var originalB = File.ReadAllText(second);
            a.SetValue(new VariantValueRecord {Path = "PublicNumber", Value = new JValue(1)});
            b.SetValue(new VariantValueRecord {Path = "PublicNumber", Value = new JValue(2)});
            using (File.Open(second, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.That(() => VariantSourceDatabase.SaveBatch(new[] {first, second}, new[] {a, b}), Throws.Exception);
            Assert.That(File.ReadAllText(first), Is.EqualTo(originalA));
            Assert.That(File.ReadAllText(second), Is.EqualTo(originalB));
        }

        [Test]
        public void FailedApplyToParentRetainsBothWorkingCopiesAndSources()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Windows file-sharing failure injection.");
            var parentPath = Create("ApplyRollbackParent");
            var childPath = Create("ApplyRollbackChild");
            using var parent = VariantEditingSession.Acquire(parentPath);
            using var child = VariantEditingSession.Acquire(childPath);
            Assert.That(ScriptableVariantAssetUtility.SetParent(child.WorkingCopy, parent.WorkingCopy, out _), Is.True);
            ((ScriptableVariantTestAsset)child.WorkingCopy).PublicNumber = 51;
            child.CommitValues();
            var beforeParent = File.ReadAllText(parentPath);
            var beforeChild = File.ReadAllText(childPath);
            using (File.Open(childPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.That(() => ScriptableVariantAssetUtility.ApplyToParent(child.WorkingCopy, "PublicNumber"), Throws.Exception);
            Assert.That(((ScriptableVariantTestAsset)parent.WorkingCopy).PublicNumber, Is.Zero);
            Assert.That(((ScriptableVariantTestAsset)child.WorkingCopy).PublicNumber, Is.EqualTo(51));
            Assert.That(parent.HasPendingChanges, Is.False);
            Assert.That(child.HasPendingChanges, Is.False);
            Assert.That(File.ReadAllText(parentPath), Is.EqualTo(beforeParent));
            Assert.That(File.ReadAllText(childPath), Is.EqualTo(beforeChild));
        }

        [Test]
        public void SelfReferenceFollowsTheStableWorkingCopyAndImportedObject()
        {
            var path = Create("SelfReference");
            using var session = VariantEditingSession.Acquire(path);
            var copy = (ScriptableVariantTestAsset)session.WorkingCopy;
            copy.Reference = copy;
            session.CommitValues();
            session.ReloadDiscardingChanges();
            Assert.That(copy.Reference, Is.SameAs(copy));
            Assert.That(session.HasPendingChanges, Is.False);
            VariantSourceDatabase.ImportNow(path);
            var imported = AssetDatabase.LoadAssetAtPath<ScriptableVariantTestAsset>(path);
            Assert.That(imported.Reference, Is.SameAs(imported));
        }

        [Test]
        public void SubassetReferenceRoundTripsThroughItsLocalIdentifier()
        {
            var main = ScriptableObject.CreateInstance<ScriptableVariantTestAsset>();
            var sub = ScriptableObject.CreateInstance<ScriptableVariantTestAsset>();
            AssetDatabase.CreateAsset(main, Folder + "/Referenced.asset");
            AssetDatabase.AddObjectToAsset(sub, main);
            AssetDatabase.SaveAssetIfDirty(main);
            var token = VariantValueSerializer.Serialize(sub, typeof(Object));
            Assert.That(token["$main"].Value<bool>(), Is.False);
            Assert.That(VariantValueSerializer.Deserialize(token, typeof(Object)), Is.EqualTo(sub));
        }

        [Test]
        public void MissingOverrideStopsResolutionInsteadOfPublishingDefault()
        {
            var parent = Create("MissingParent");
            var path = Create("MissingChild");
            var document = Read(path).Clone();
            document.ParentGuid = AssetDatabase.AssetPathToGUID(parent);
            document.OverridePaths.Add("PublicNumber");
            document.RemoveValue("PublicNumber");
            File.WriteAllText(path, VariantSourceDatabase.SerializeDocument(document));
            Assert.That(ScriptableVariantImporter.TryCreateVariant(path, out var output, out var error), Is.False);
            Assert.That(output, Is.Null);
            Assert.That(error, Does.Contain("Missing stored override"));
        }

        [Test]
        public void UnknownStoredValuesAreRetainedUntilExplicitRemoval()
        {
            var path = Create("Orphan");
            var document = Read(path).Clone();
            document.SetValue(new VariantValueRecord {Path = "DeletedField", Value = new JValue("keep me")});
            File.WriteAllText(path, VariantSourceDatabase.SerializeDocument(document));
            using var session = VariantEditingSession.Acquire(path);
            ((ScriptableVariantTestAsset)session.WorkingCopy).PublicNumber = 5;
            Assert.Throws<InvalidOperationException>(() => session.CommitValues());
            Assert.That(Read(path).FindValue("DeletedField").Value.Value<string>(), Is.EqualTo("keep me"));
            session.RemoveOrphans();
            Assert.That(Read(path).FindValue("DeletedField"), Is.Null);
            Assert.That(Read(path).FindValue("PublicNumber").Value.Value<int>(), Is.EqualTo(5));
            Assert.That(session.HasPendingChanges, Is.False);
        }

        private static string Create(string name)
        {
            var path = Folder + "/" + name + ".svariant";
            ScriptableVariantAssetUtility.CreateRoot(typeof(ScriptableVariantTestAsset), path);
            return path;
        }

        private static VariantSourceDocument Read(string path)
        {
            Assert.That(VariantSourceDatabase.TryLoadUncached(path, out var document, out var error), Is.True, error);
            return document;
        }

        [Serializable] private sealed class InvalidAtomicLocal { public List<LocalEntry> Entries; }
        [Serializable] private sealed class LocalEntry { [VariantLocal] public int Number; }
    }
}
