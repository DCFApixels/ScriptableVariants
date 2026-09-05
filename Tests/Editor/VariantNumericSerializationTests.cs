using System;
using System.Collections.Generic;
using System.Globalization;
using DCFApixels.ScriptableVariants.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Tests
{
    public sealed class VariantNumericSerializationTests
    {
        private static IEnumerable<TestCaseData> NumericValues()
        {
            yield return new TestCaseData(new Vector2(1.12f, -2.01f), "[1.12,-2.01]");
            yield return new TestCaseData(new Vector3(1.12f, 2.01f, 0.0012f), "[1.12,2.01,0.0012]");
            yield return new TestCaseData(new Vector4(1.12f, 2.01f, 0.0012f, 1f), "[1.12,2.01,0.0012,1]");
            yield return new TestCaseData(new Vector2Int(int.MinValue, int.MaxValue), "[-2147483648,2147483647]");
            yield return new TestCaseData(new Vector3Int(-13, 0, 17), "[-13,0,17]");
            yield return new TestCaseData(new Quaternion(2, -3, 4, -5), "[2,-3,4,-5]");
            yield return new TestCaseData(new Color(3.5f, -0.25f, 0.0012f, 1.25f), "[3.5,-0.25,0.0012,1.25]");
            yield return new TestCaseData(new Color32(0, 127, 255, 13), "[0,127,255,13]");
        }

        [TestCaseSource(nameof(NumericValues))]
        public void NumericTypesWriteAndReadArrays(object expected, string compact)
        {
            var type = expected.GetType();
            var token = VariantValueSerializer.Serialize(expected, type);
            Assert.That(token, Is.InstanceOf<JArray>());
            var expectedComponents = JArray.Parse(compact);
            Assert.That(((JArray)token).Count, Is.EqualTo(expectedComponents.Count));
            for (var i = 0; i < expectedComponents.Count; i++)
                Assert.That(token[i].Value<double>(), Is.EqualTo(expectedComponents[i].Value<double>()).Within(0.000001));
            Assert.That(VariantValueSerializer.Deserialize(Parse(token.ToString(Formatting.None)), type), Is.EqualTo(expected));
            Assert.That(VariantValueSerializer.Deserialize(Parse(compact), type), Is.EqualTo(expected));
        }

        [TestCase(2f, -3f, 4f, -5f)]
        [TestCase(0f, 0f, 0f, 0f)]
        public void QuaternionsAreNotNormalizedOrReplacedByIdentity(float x, float y, float z, float w)
        {
            var value = new Quaternion(x, y, z, w);
            var token = VariantValueSerializer.Serialize(value, typeof(Quaternion));
            var restored = (Quaternion)VariantValueSerializer.Deserialize(Parse(token.ToString(Formatting.None)), typeof(Quaternion));
            Assert.That(restored.x, Is.EqualTo(x));
            Assert.That(restored.y, Is.EqualTo(y));
            Assert.That(restored.z, Is.EqualTo(z));
            Assert.That(restored.w, Is.EqualTo(w));
        }

        [Test]
        public void Color32ComponentsStayIntegersAndAreNotNormalizedToFloats()
        {
            var token = VariantValueSerializer.Serialize(new Color32(0, 127, 255, 13), typeof(Color32));
            foreach (var component in token.Children()) Assert.That(component.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(token[2].Value<int>(), Is.EqualTo(255));
        }

        [TestCase("ru-RU")]
        [TestCase("en-US")]
        public void FiniteFloatsRoundTripWithoutDecimalRoundingOrLocaleDependence(string culture)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var value = new Vector4(float.MaxValue, float.Epsilon, -123.123456f, 0.0012345678f);
                var json = VariantValueSerializer.Serialize(value, typeof(Vector4)).ToString(Formatting.None);
                var restored = (Vector4)VariantValueSerializer.Deserialize(Parse(json), typeof(Vector4));
                Assert.That(restored.x, Is.EqualTo(value.x));
                Assert.That(restored.y, Is.EqualTo(value.y));
                Assert.That(restored.z, Is.EqualTo(value.z));
                Assert.That(restored.w, Is.EqualTo(value.w));
                Assert.That(json, Does.Contain("0.001234"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void NonFiniteFloatsRetainExplicitJsonNetSpellings()
        {
            var token = VariantValueSerializer.Serialize(
                new Vector3(float.NaN, float.PositiveInfinity, float.NegativeInfinity), typeof(Vector3));
            var json = token.ToString(Formatting.None);
            Assert.That(json, Does.Contain("\"NaN\"").And.Contain("\"Infinity\"").And.Contain("\"-Infinity\""));
            var restored = (Vector3)VariantValueSerializer.Deserialize(Parse(json), typeof(Vector3));
            Assert.That(float.IsNaN(restored.x), Is.True);
            Assert.That(restored.y, Is.EqualTo(float.PositiveInfinity));
            Assert.That(restored.z, Is.EqualTo(float.NegativeInfinity));
        }

        [TestCase(typeof(Vector3), "[1,2]")]
        [TestCase(typeof(Vector3), "[1,2,3,4]")]
        [TestCase(typeof(Vector2), "[]")]
        [TestCase(typeof(Quaternion), "[1,2,3]")]
        [TestCase(typeof(Quaternion), "[1,2,3,4,5]")]
        [TestCase(typeof(Quaternion), "{\"x\":1,\"y\":2,\"z\":3,\"w\":4}")]
        [TestCase(typeof(Color), "[1,2,3]")]
        [TestCase(typeof(Color), "[1,2,3,4,5]")]
        [TestCase(typeof(Vector2), "[null,2]")]
        [TestCase(typeof(Vector2), "[true,2]")]
        [TestCase(typeof(Vector2), "[\"1.12\",2]")]
        [TestCase(typeof(Vector2), "[[1],2]")]
        [TestCase(typeof(Vector2), "[1e100,2]")]
        [TestCase(typeof(Vector2), "\"(1.12, 2.01)\"")]
        [TestCase(typeof(Vector2), "null")]
        [TestCase(typeof(Vector2Int), "[1.5,2]")]
        [TestCase(typeof(Vector2Int), "[1.0,2]")]
        [TestCase(typeof(Vector2Int), "[2147483648,2]")]
        [TestCase(typeof(Vector2Int), "[-2147483649,2]")]
        [TestCase(typeof(Vector2Int), "[9999999999999999999999999999,2]")]
        [TestCase(typeof(Color32), "[-1,2,3,4]")]
        [TestCase(typeof(Color32), "[256,2,3,4]")]
        [TestCase(typeof(Color32), "[1,2,3,0.5]")]
        [TestCase(typeof(Vector3), "{\"x\":1,\"y\":2}")]
        [TestCase(typeof(Vector2), "{\"x\":1,\"y\":2}")]
        [TestCase(typeof(Vector2), "{\"x\":1,\"y\":2,\"other\":3}")]
        [TestCase(typeof(Color32), "{\"r\":256,\"g\":0,\"b\":0,\"a\":255}")]
        public void InvalidComponentsAreRejectedInsteadOfTruncatedOrClamped(Type type, string json)
        {
            Assert.Throws<JsonSerializationException>(() => VariantValueSerializer.Deserialize(Parse(json), type));
        }

        [Test]
        public void VectorsAndColorsUseTheSameConvertersInsideCollectionsAndNativeValues()
        {
            var data = new NestedValues
            {
                Positions = new List<Vector3> {new Vector3(1, 2, 3), new Vector3(4, 5, 6)},
                Colors = new[] {new Color32(1, 2, 3, 255)},
                Rotations = new[] {new Quaternion(2, -3, 4, -5)},
                Bounds = new Bounds(new Vector3(1, 2, 3), new Vector3(4, 6, 8)),
                Gradient = new Gradient(),
            };
            data.Gradient.SetKeys(new[] {new GradientColorKey(Color.red, 0), new GradientColorKey(Color.blue, 1)},
                new[] {new GradientAlphaKey(0.25f, 0), new GradientAlphaKey(1, 1)});
            var token = VariantValueSerializer.Serialize(data, typeof(NestedValues));
            Assert.That(token["Positions"][0], Is.InstanceOf<JArray>());
            Assert.That(token["Colors"][0], Is.InstanceOf<JArray>());
            Assert.That(token["Rotations"][0], Is.InstanceOf<JArray>());
            Assert.That(token["Bounds"]["center"], Is.InstanceOf<JArray>());
            Assert.That(token["Bounds"]["extents"], Is.InstanceOf<JArray>());
            Assert.That(token["Gradient"]["colorKeys"][0]["color"], Is.InstanceOf<JArray>());
            var restored = (NestedValues)VariantValueSerializer.Deserialize(Parse(token.ToString(Formatting.None)), typeof(NestedValues));
            Assert.That(restored.Positions, Is.EqualTo(data.Positions));
            Assert.That(restored.Colors, Is.EqualTo(data.Colors));
            Assert.That(restored.Rotations, Is.EqualTo(data.Rotations));
            Assert.That(restored.Bounds, Is.EqualTo(data.Bounds));
            Assert.That(restored.Gradient.colorKeys, Is.EqualTo(data.Gradient.colorKeys));
            Assert.That(restored.Gradient.alphaKeys, Is.EqualTo(data.Gradient.alphaKeys));
        }

        [Test]
        public void LegacyGradientFlatColorsAreRejectedWithoutMigration()
        {
            var token = Parse("{\"mode\":0,\"colorKeys\":[{\"r\":1,\"g\":0,\"b\":0,\"a\":1,\"time\":0}," +
                "{\"r\":0,\"g\":0,\"b\":1,\"a\":1,\"time\":1}],\"alphaKeys\":[{\"alpha\":1,\"time\":0},{\"alpha\":1,\"time\":1}]}");
            Assert.Throws<JsonSerializationException>(() => VariantValueSerializer.Deserialize(token, typeof(Gradient)));
        }

        [Test]
        public void CapturedDocumentRoundTripsWithTheCurrentFormatAndNumericArrays()
        {
            var document = new VariantSourceDocument();
            var asset = ScriptableObject.CreateInstance<ScriptableVariantTestAsset>();
            try
            {
                asset.Bounds = new Bounds(new Vector3(1, 2, 3), new Vector3(4, 6, 8));
                VariantValueSerializer.CaptureValues(document, asset, new[] {"Bounds"});
                Assert.That(document.FormatVersion, Is.EqualTo(3));
                Assert.That(document.FindValue("Bounds").Value["center"], Is.InstanceOf<JArray>());
                var restored = VariantSourceDatabase.DeserializeDocument(VariantSourceDatabase.SerializeDocument(document));
                var values = VariantValueSerializer.ReadValues(restored, typeof(ScriptableVariantTestAsset), new[] {"Bounds"});
                Assert.That(values["Bounds"], Is.EqualTo(asset.Bounds));
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }

        private static JToken Parse(string json)
        {
            using var text = new System.IO.StringReader(json);
            using var reader = new JsonTextReader(text) {DateParseHandling = DateParseHandling.None};
            return JToken.Load(reader);
        }

        [Serializable]
        private sealed class NestedValues
        {
            public List<Vector3> Positions;
            public Color32[] Colors;
            public Quaternion[] Rotations;
            public Bounds Bounds;
            public Gradient Gradient;
        }
    }
}
