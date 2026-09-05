using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DCFApixels.ScriptableVariants.Editor
{
    internal static class VariantValueSerializer
    {
        private const string UnityObjectProperty = "$unityObject";

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new UnitySerializedFieldContractResolver(),
            SerializationBinder = new LoadedAssemblyBinder(),
            TypeNameHandling = TypeNameHandling.Auto,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
            NullValueHandling = NullValueHandling.Include,
            Converters = new List<JsonConverter>
            {
                new UnityObjectConverter(),
                new AnimationCurveConverter(),
                new GradientConverter(),
                new Hash128Converter(),
            },
        });

        internal static JToken Serialize(object value, Type declaredType)
        {
            var writer = new JTokenWriter();
            Serializer.Serialize(writer, value, declaredType);
            return writer.Token ?? JValue.CreateNull();
        }

        internal static object Deserialize(JToken token, Type declaredType)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            using (var reader = token.CreateReader())
            {
                return Serializer.Deserialize(reader, declaredType);
            }
        }

        internal static void AddObjectDependencies(JToken token, Action<string> addDependency)
        {
            if (token == null)
            {
                return;
            }

            if (token is JObject objectToken)
            {
                foreach (var property in objectToken.Properties())
                {
                    if (string.Equals(property.Name, UnityObjectProperty, StringComparison.Ordinal) &&
                        property.Value.Type == JTokenType.String &&
                        GlobalObjectId.TryParse(property.Value.Value<string>(), out var globalId))
                    {
                        var assetPath = AssetDatabase.GUIDToAssetPath(globalId.assetGUID.ToString());
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            addDependency(assetPath);
                        }
                    }

                    AddObjectDependencies(property.Value, addDependency);
                }
            }
            else if (token is JArray arrayToken)
            {
                for (var i = 0; i < arrayToken.Count; i++)
                {
                    AddObjectDependencies(arrayToken[i], addDependency);
                }
            }
        }

        private sealed class UnitySerializedFieldContractResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
            {
                if (!ShouldUseUnityFieldRules(type))
                {
                    return base.CreateProperties(type, memberSerialization);
                }

                var fields = VariantSerialization.GetSerializableFields(type);
                var properties = new List<JsonProperty>(fields.Length);
                for (var i = 0; i < fields.Length; i++)
                {
                    var property = base.CreateProperty(fields[i], MemberSerialization.Fields);
                    property.Readable = true;
                    property.Writable = true;
                    properties.Add(property);
                }

                return properties;
            }

            private static bool ShouldUseUnityFieldRules(Type type)
            {
                if (type == null || type.IsPrimitive || type.IsEnum || type == typeof(string) ||
                    type.IsArray || typeof(System.Collections.IList).IsAssignableFrom(type) ||
                    typeof(Object).IsAssignableFrom(type))
                {
                    return false;
                }

                return type.IsValueType || type.IsDefined(typeof(SerializableAttribute), false);
            }
        }

        private sealed class LoadedAssemblyBinder : ISerializationBinder
        {
            public Type BindToType(string assemblyName, string typeName)
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (var i = 0; i < assemblies.Length; i++)
                {
                    var assembly = assemblies[i];
                    if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal) &&
                        !string.Equals(assembly.FullName, assemblyName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var type = assembly.GetType(typeName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }

                throw new JsonSerializationException(
                    $"Serialized type '{typeName}, {assemblyName}' is not loaded in the Unity domain.");
            }

            public void BindToName(Type serializedType, out string assemblyName, out string typeName)
            {
                assemblyName = serializedType.Assembly.GetName().Name;
                typeName = serializedType.FullName;
            }
        }

        private sealed class UnityObjectConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(Object).IsAssignableFrom(objectType);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (!(value is Object unityObject) || unityObject == null)
                {
                    writer.WriteNull();
                    return;
                }

                if (unityObject is ScriptableVariant variant && VariantEditingSession.IsWorkingCopy(variant))
                {
                    // A callback may assign 'this'. Persist the source asset reference, never
                    // the Inspector's transient object identity.
                    unityObject = AssetDatabase.LoadAssetAtPath<ScriptableVariant>(
                        VariantEditingSession.GetAssetPath(variant));
                    if (unityObject == null)
                    {
                        throw new JsonSerializationException(
                            $"The source for '{variant.name}' has no imported object to reference yet.");
                    }
                }

                if (!EditorUtility.IsPersistent(unityObject))
                {
                    throw new JsonSerializationException(
                        $"'{unityObject.name}' is not a persistent Unity asset and cannot be stored in a variant.");
                }

                writer.WriteStartObject();
                writer.WritePropertyName(UnityObjectProperty);
                writer.WriteValue(GlobalObjectId.GetGlobalObjectIdSlow(unityObject).ToString());
                writer.WriteEndObject();
            }

            public override object ReadJson(
                JsonReader reader,
                Type objectType,
                object existingValue,
                JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return null;
                }

                var value = JObject.Load(reader)[UnityObjectProperty]?.Value<string>();
                if (string.IsNullOrEmpty(value) || !GlobalObjectId.TryParse(value, out var globalId))
                {
                    return null;
                }

                var unityObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                return unityObject != null && objectType.IsInstanceOfType(unityObject) ? unityObject : null;
            }
        }

        private sealed class AnimationCurveConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(AnimationCurve);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var curve = (AnimationCurve)value;
                writer.WriteStartObject();
                writer.WritePropertyName("keys");
                writer.WriteStartArray();
                var keys = curve.keys;
                for (var i = 0; i < keys.Length; i++)
                {
                    var key = keys[i];
                    writer.WriteStartObject();
                    WriteNumber(writer, "time", key.time);
                    WriteNumber(writer, "value", key.value);
                    WriteNumber(writer, "inTangent", key.inTangent);
                    WriteNumber(writer, "outTangent", key.outTangent);
                    WriteNumber(writer, "inWeight", key.inWeight);
                    WriteNumber(writer, "outWeight", key.outWeight);
                    writer.WritePropertyName("weightedMode");
                    writer.WriteValue((int)key.weightedMode);
                    writer.WritePropertyName("broken");
                    writer.WriteValue(AnimationUtility.GetKeyBroken(curve, i));
                    writer.WritePropertyName("leftTangentMode");
                    writer.WriteValue((int)AnimationUtility.GetKeyLeftTangentMode(curve, i));
                    writer.WritePropertyName("rightTangentMode");
                    writer.WriteValue((int)AnimationUtility.GetKeyRightTangentMode(curve, i));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("preWrapMode");
                writer.WriteValue((int)curve.preWrapMode);
                writer.WritePropertyName("postWrapMode");
                writer.WriteValue((int)curve.postWrapMode);
                writer.WriteEndObject();
            }

            public override object ReadJson(
                JsonReader reader,
                Type objectType,
                object existingValue,
                JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return null;
                }

                var data = JObject.Load(reader);
                var keyTokens = data["keys"] as JArray;
                var keys = new Keyframe[keyTokens?.Count ?? 0];
                for (var i = 0; i < keys.Length; i++)
                {
                    var keyData = (JObject)keyTokens[i];
                    keys[i] = new Keyframe(
                        keyData["time"]?.Value<float>() ?? 0f,
                        keyData["value"]?.Value<float>() ?? 0f,
                        keyData["inTangent"]?.Value<float>() ?? 0f,
                        keyData["outTangent"]?.Value<float>() ?? 0f)
                    {
                        inWeight = keyData["inWeight"]?.Value<float>() ?? 0f,
                        outWeight = keyData["outWeight"]?.Value<float>() ?? 0f,
                        weightedMode = (WeightedMode)(keyData["weightedMode"]?.Value<int>() ?? 0),
                    };
                }

                var curve = new AnimationCurve(keys)
                {
                    preWrapMode = (WrapMode)(data["preWrapMode"]?.Value<int>() ?? 0),
                    postWrapMode = (WrapMode)(data["postWrapMode"]?.Value<int>() ?? 0),
                };
                for (var i = 0; i < keys.Length; i++)
                {
                    var keyData = (JObject)keyTokens[i];
                    AnimationUtility.SetKeyBroken(curve, i, keyData["broken"]?.Value<bool>() ?? false);
                    AnimationUtility.SetKeyLeftTangentMode(
                        curve,
                        i,
                        (AnimationUtility.TangentMode)(keyData["leftTangentMode"]?.Value<int>() ?? 0));
                    AnimationUtility.SetKeyRightTangentMode(
                        curve,
                        i,
                        (AnimationUtility.TangentMode)(keyData["rightTangentMode"]?.Value<int>() ?? 0));
                }

                return curve;
            }
        }

        private sealed class GradientConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Gradient);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var gradient = (Gradient)value;
                writer.WriteStartObject();
                writer.WritePropertyName("mode");
                writer.WriteValue((int)gradient.mode);
                writer.WritePropertyName("colorKeys");
                writer.WriteStartArray();
                var colorKeys = gradient.colorKeys;
                for (var i = 0; i < colorKeys.Length; i++)
                {
                    writer.WriteStartObject();
                    WriteColor(writer, colorKeys[i].color);
                    WriteNumber(writer, "time", colorKeys[i].time);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("alphaKeys");
                writer.WriteStartArray();
                var alphaKeys = gradient.alphaKeys;
                for (var i = 0; i < alphaKeys.Length; i++)
                {
                    writer.WriteStartObject();
                    WriteNumber(writer, "alpha", alphaKeys[i].alpha);
                    WriteNumber(writer, "time", alphaKeys[i].time);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            public override object ReadJson(
                JsonReader reader,
                Type objectType,
                object existingValue,
                JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                {
                    return null;
                }

                var data = JObject.Load(reader);
                var gradient = new Gradient
                {
                    mode = (GradientMode)(data["mode"]?.Value<int>() ?? 0),
                };
                var colorTokens = data["colorKeys"] as JArray;
                var colorKeys = new GradientColorKey[colorTokens?.Count ?? 0];
                for (var i = 0; i < colorKeys.Length; i++)
                {
                    var keyData = (JObject)colorTokens[i];
                    colorKeys[i] = new GradientColorKey(
                        ReadColor(keyData),
                        keyData["time"]?.Value<float>() ?? 0f);
                }

                var alphaTokens = data["alphaKeys"] as JArray;
                var alphaKeys = new GradientAlphaKey[alphaTokens?.Count ?? 0];
                for (var i = 0; i < alphaKeys.Length; i++)
                {
                    var keyData = (JObject)alphaTokens[i];
                    alphaKeys[i] = new GradientAlphaKey(
                        keyData["alpha"]?.Value<float>() ?? 0f,
                        keyData["time"]?.Value<float>() ?? 0f);
                }

                gradient.SetKeys(colorKeys, alphaKeys);
                return gradient;
            }

            private static void WriteColor(JsonWriter writer, Color color)
            {
                WriteNumber(writer, "r", color.r);
                WriteNumber(writer, "g", color.g);
                WriteNumber(writer, "b", color.b);
                WriteNumber(writer, "a", color.a);
            }

            private static Color ReadColor(JObject data)
            {
                return new Color(
                    data["r"]?.Value<float>() ?? 0f,
                    data["g"]?.Value<float>() ?? 0f,
                    data["b"]?.Value<float>() ?? 0f,
                    data["a"]?.Value<float>() ?? 1f);
            }
        }

        private sealed class Hash128Converter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Hash128);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteValue(((Hash128)value).ToString());
            }

            public override object ReadJson(
                JsonReader reader,
                Type objectType,
                object existingValue,
                JsonSerializer serializer)
            {
                return reader.TokenType == JsonToken.String
                    ? Hash128.Parse((string)reader.Value)
                    : default(Hash128);
            }
        }

        private static void WriteNumber(JsonWriter writer, string propertyName, float value)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteValue(value);
        }
    }
}
