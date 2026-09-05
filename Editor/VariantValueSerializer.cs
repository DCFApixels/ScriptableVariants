using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace DCFApixels.ScriptableVariants.Editor
{
    internal static class VariantValueSerializer
    {
        private const string UnityObjectProperty = "$unityObject";

        private static readonly UnitySerializedFieldContractResolver Contracts = new UnitySerializedFieldContractResolver();

        private static JsonSerializer CreateSerializer(IEnumerable<Type> roots, IContractResolver resolver = null,
            ScriptableVariant self = null, string sourcePath = null)
        {
            return JsonSerializer.Create(new JsonSerializerSettings
            {
            ContractResolver = resolver ?? Contracts,
            SerializationBinder = new SchemaBinder(roots),
            ReferenceResolverProvider = () => new StrictReferenceResolver(),
            TypeNameHandling = TypeNameHandling.Auto,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.None,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling = MissingMemberHandling.Error,
            MaxDepth = 128,
            Converters = new List<JsonConverter>
            {
                new UnityObjectConverter(self, sourcePath),
                new AnimationCurveConverter(),
                new GradientConverter(),
                new BoundsConverter(),
                new Hash128Converter(),
                new NativePropertiesConverter(),
            },
            });
        }

        internal static JToken Serialize(object value, Type declaredType)
        {
            var writer = new JTokenWriter();
            CreateSerializer(new[] {declaredType}).Serialize(writer, value, declaredType);
            return writer.Token ?? JValue.CreateNull();
        }

        internal static object Deserialize(JToken token, Type declaredType)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (declaredType.IsValueType && Nullable.GetUnderlyingType(declaredType) == null)
                    throw new JsonSerializationException($"Null is not a value of {declaredType.FullName}.");
                return null;
            }

            using (var reader = token.CreateReader())
            {
                return CreateSerializer(new[] {declaredType}).Deserialize(reader, declaredType);
            }
        }

        // A single Json.NET operation gives every stored field the same reference graph. The
        // envelope itself is not persisted; source values remain readable path/value records.
        internal static void CaptureValues(VariantSourceDocument document, ScriptableVariant source,
            IEnumerable<string> paths)
        {
            VariantSerialization.GetLocalPaths(source.GetType());
            VariantSerialization.ValidateLocalValues(source);
            var schema = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            var values = new ValueBag();
            foreach (var path in paths)
            {
                if (!VariantSerialization.TryGetPathValue(source, path, out var value, out var type))
                    throw new JsonSerializationException($"Cannot capture stored field '{path}'.");
                if (VariantSerialization.IsAtomicOverridePath(source.GetType(), path))
                    VariantSerialization.ValidateAtomicLocalValue(value, path);
                schema[path] = type;
                values.Values[path] = value;
            }
            var serializer = CreateSerializer(schema.Values, new BagContractResolver(schema));
            var writer = new JTokenWriter();
            serializer.Serialize(writer, values, typeof(ValueBag));
            var json = (JObject)writer.Token;
            document.Values = schema.Keys.Select(path => new VariantValueRecord {Path = path, Value = json[path]}).ToList();
            document.FormatVersion = VariantSourceDocument.CurrentFormatVersion;
            document.Normalize();
        }

        internal static Dictionary<string, object> ReadValues(VariantSourceDocument document, Type variantType,
            IEnumerable<string> paths, ScriptableVariant self = null, string sourcePath = null)
        {
            document.ValidateFormat();
            var schema = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            var json = new JObject();
            foreach (var path in paths.OrderBy(value => value, StringComparer.Ordinal))
            {
                var record = document.FindValue(path);
                if (record == null || !VariantSerialization.TryGetPathType(variantType, path, out var type)) continue;
                schema[path] = type;
                json[path] = record.Value.DeepClone();
            }
            var serializer = CreateSerializer(schema.Values, new BagContractResolver(schema), self, sourcePath);
            using (var reader = OrderReferenceDefinitions(json).CreateReader())
                return serializer.Deserialize<ValueBag>(reader).Values;
        }

        private static JObject OrderReferenceDefinitions(JObject values)
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var property in values.Properties())
            {
                dependencies[property.Name] = new HashSet<string>(StringComparer.Ordinal);
                foreach (var obj in Objects(property.Value))
                    if (obj["$id"] is JValue id)
                    {
                        var key = id.Value<string>();
                        if (owners.ContainsKey(key)) throw new JsonSerializationException($"Duplicate managed reference '{key}'.");
                        owners.Add(key, property.Name);
                    }
            }
            foreach (var property in values.Properties())
                foreach (var obj in Objects(property.Value))
                    if (obj["$ref"] is JValue reference)
                    {
                        var key = reference.Value<string>();
                        if (!owners.TryGetValue(key, out var owner))
                            throw new JsonSerializationException($"Missing managed reference '{key}'.");
                        if (owner != property.Name) dependencies[property.Name].Add(owner);
                    }
            var result = new JObject();
            var pending = new HashSet<string>(dependencies.Keys, StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var ready = pending.Where(path => !dependencies[path].Any(pending.Contains))
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
                if (ready.Length == 0) throw new JsonSerializationException("Cyclic forward references between stored records.");
                foreach (var path in ready) { result[path] = values[path].DeepClone(); pending.Remove(path); }
            }
            return result;
        }

        private static IEnumerable<JObject> Objects(JToken token)
        {
            if (token is JObject obj) yield return obj;
            foreach (var child in token.Children())
                foreach (var nested in Objects(child)) yield return nested;
        }

        private sealed class ValueBag
        {
            internal readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private sealed class BagContractResolver : IContractResolver
        {
            private readonly JsonObjectContract _contract = new JsonObjectContract(typeof(ValueBag))
            {DefaultCreator = () => new ValueBag()};

            internal BagContractResolver(IEnumerable<KeyValuePair<string, Type>> schema)
            {
                foreach (var pair in schema)
                    _contract.Properties.Add(new JsonProperty
                    {
                        PropertyName = pair.Key, PropertyType = pair.Value, DeclaringType = typeof(ValueBag),
                        Readable = true, Writable = true, ValueProvider = new BagValueProvider(pair.Key),
                    });
            }
            public JsonContract ResolveContract(Type type) => type == typeof(ValueBag) ? _contract : Contracts.ResolveContract(type);
        }

        private sealed class BagValueProvider : IValueProvider
        {
            private readonly string _path;
            internal BagValueProvider(string path) { _path = path; }
            public object GetValue(object target) => ((ValueBag)target).Values.TryGetValue(_path, out var value) ? value : null;
            public void SetValue(object target, object value) => ((ValueBag)target).Values[_path] = value;
        }

        private sealed class StrictReferenceResolver : IReferenceResolver
        {
            private readonly Dictionary<string, object> _objects = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly Dictionary<object, string> _ids = new Dictionary<object, string>(ReferenceComparer.Instance);
            public object ResolveReference(object context, string reference)
            {
                if (_objects.TryGetValue(reference, out var value)) return value;
                throw new JsonSerializationException($"Missing or forward managed reference '{reference}'.");
            }
            public string GetReference(object context, object value)
            {
                if (!_ids.TryGetValue(value, out var id))
                {
                    id = (_ids.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _ids.Add(value, id);
                }
                return id;
            }
            public bool IsReferenced(object context, object value) => _ids.ContainsKey(value);
            public void AddReference(object context, string reference, object value)
            {
                if (_objects.ContainsKey(reference)) throw new JsonSerializationException($"Duplicate managed reference '{reference}'.");
                _objects.Add(reference, value);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
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

        internal static void AddObjectDependencyGuids(JToken token, Action<GUID> addDependency)
        {
            if (token is JObject obj)
            {
                if (obj[UnityObjectProperty]?.Type == JTokenType.String &&
                    GlobalObjectId.TryParse(obj[UnityObjectProperty].Value<string>(), out var id) && !id.assetGUID.Empty())
                    addDependency(id.assetGUID);
                foreach (var property in obj.Properties()) AddObjectDependencyGuids(property.Value, addDependency);
            }
            else if (token is JArray array)
                foreach (var item in array) AddObjectDependencyGuids(item, addDependency);
        }

        private sealed class UnitySerializedFieldContractResolver : DefaultContractResolver
        {
            internal UnitySerializedFieldContractResolver()
            {
                IgnoreSerializableInterface = true;
                IgnoreSerializableAttribute = true;
            }

            protected override JsonConverter ResolveContractConverter(Type objectType) => null;

            protected override JsonContract CreateContract(Type objectType)
            {
                var converter = VariantNumericConverters.Get(objectType);
                // Known value types need no reflected property contracts or converter-list scans.
                return converter != null
                    ? new JsonObjectContract(objectType) {Converter = converter}
                    : base.CreateContract(objectType);
            }

            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
            {
                if (!ShouldUseUnityFieldRules(type))
                {
                    return base.CreateProperties(type, memberSerialization);
                }

                var fields = VariantSerialization.GetSerializableFields(type);
                if (fields.Length == 0 && type.Namespace != null && type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    type != typeof(Bounds) && type != typeof(Hash128) && !NativePropertiesConverter.Supports(type))
                    throw new JsonSerializationException($"No lossless serializer is registered for native type '{type.FullName}'.");
                var properties = new List<JsonProperty>(fields.Length);
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < fields.Length; i++)
                {
                    var property = CreateFieldProperty(fields[i]);
                    properties.Add(property);
                    names.Add(property.PropertyName);
                }

                // Accept former names on read, but write only the current field name.
                // Resolve current names first so a former name never shadows another real field.
                for (var i = 0; i < fields.Length; i++)
                {
                    foreach (var formerName in fields[i].GetCustomAttributes<FormerlySerializedAsAttribute>(true))
                    {
                        if (string.IsNullOrEmpty(formerName.oldName) || !names.Add(formerName.oldName))
                        {
                            continue;
                        }

                        var alias = CreateFieldProperty(fields[i]);
                        alias.PropertyName = formerName.oldName;
                        alias.Readable = false;
                        alias.Writable = true;
                        properties.Add(alias);
                    }
                }

                return properties;
            }

            private static JsonProperty CreateFieldProperty(FieldInfo field) => new JsonProperty
            {
                PropertyName = field.Name, PropertyType = field.FieldType, DeclaringType = field.DeclaringType,
                Readable = true, Writable = true, ValueProvider = new ReflectionValueProvider(field),
            };

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

        private sealed class SchemaBinder : ISerializationBinder
        {
            private static readonly Dictionary<Type, HashSet<Type>> Schemas = new Dictionary<Type, HashSet<Type>>();
            private readonly Dictionary<string, Type> _allowed = new Dictionary<string, Type>(StringComparer.Ordinal);

            internal SchemaBinder(IEnumerable<Type> roots)
            {
                foreach (var root in roots.Distinct())
                {
                    if (!Schemas.TryGetValue(root, out var schema))
                    {
                        schema = new HashSet<Type>();
                        Collect(root, schema);
                        Schemas.Add(root, schema);
                    }
                    foreach (var type in schema)
                    {
                        _allowed[type.FullName + ", " + type.Assembly.GetName().Name] = type;
                        _allowed[type.FullName + ", " + type.Assembly.FullName] = type;
                    }
                }
            }

            private static void Collect(Type type, HashSet<Type> types)
            {
                if (type == null || !types.Add(type)) return;
                if (type.IsPrimitive || type.IsEnum || type == typeof(string) || typeof(Object).IsAssignableFrom(type)) return;
                if (type.IsArray) { Collect(type.GetElementType(), types); return; }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                { Collect(type.GetGenericArguments()[0], types); return; }
                if (!type.IsSealed && !type.IsValueType)
                {
                    foreach (var derived in TypeCache.GetTypesDerivedFrom(type))
                    {
                        var ns = derived.Namespace ?? string.Empty;
                        if (derived.IsAbstract || derived.ContainsGenericParameters ||
                            !derived.IsDefined(typeof(SerializableAttribute), false) ||
                            typeof(Object).IsAssignableFrom(derived) || typeof(Delegate).IsAssignableFrom(derived) ||
                            ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
                            ns.StartsWith("UnityEditor", StringComparison.Ordinal)) continue;
                        Collect(derived, types);
                    }
                }
                foreach (var field in VariantSerialization.GetSerializableFields(type)) Collect(field.FieldType, types);
            }

            public Type BindToType(string assemblyName, string typeName)
            {
                if (_allowed.TryGetValue(typeName + ", " + assemblyName, out var type)) return type;
                throw new JsonSerializationException(
                    $"Serialized type '{typeName}, {assemblyName}' is not allowed by this field's data schema.");
            }

            public void BindToName(Type serializedType, out string assemblyName, out string typeName)
            {
                assemblyName = serializedType.Assembly.GetName().Name;
                typeName = serializedType.FullName;
                if (!_allowed.ContainsKey(typeName + ", " + assemblyName))
                    throw new JsonSerializationException($"Runtime type '{serializedType.FullName}' is not allowed by this field's data schema.");
            }
        }

        private sealed class UnityObjectConverter : JsonConverter
        {
            private readonly ScriptableVariant _self;
            private readonly string _sourceGuid;
            internal UnityObjectConverter(ScriptableVariant self, string path)
            {
                _self = self;
                _sourceGuid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            }

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
                writer.WritePropertyName("$main");
                writer.WriteValue(AssetDatabase.IsMainAsset(unityObject));
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
                var value = data[UnityObjectProperty]?.Value<string>();
                if (string.IsNullOrEmpty(value) || !GlobalObjectId.TryParse(value, out var globalId))
                {
                    throw new JsonSerializationException("Invalid Unity asset reference. The original source was not changed.");
                }

                // The current import's main object has no persistent identity until publication.
                // An explicit main-asset marker avoids loading this same asset recursively.
                if (_self != null && objectType.IsInstanceOfType(_self) && data["$main"]?.Value<bool>() == true &&
                    string.Equals(globalId.assetGUID.ToString(), _sourceGuid, StringComparison.Ordinal)) return _self;

                var unityObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                if (unityObject == null)
                {
                    var path = AssetDatabase.GUIDToAssetPath(globalId.assetGUID.ToString());
                    if (!string.IsNullOrEmpty(path) && !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        // Resolve subassets on a cold import, not only objects already loaded by an Inspector.
                        foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(path))
                            if (candidate != null && GlobalObjectId.GetGlobalObjectIdSlow(candidate).Equals(globalId))
                            { unityObject = candidate; break; }
                    }
                }
                if (unityObject == null || !objectType.IsInstanceOfType(unityObject))
                    throw new JsonSerializationException($"Missing or incompatible Unity asset reference '{value}'.");
                return unityObject;
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
                if (keyTokens == null) throw new JsonSerializationException("AnimationCurve must contain a keys array.");
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
                writer.WritePropertyName("colorSpace");
                writer.WriteValue((int)gradient.colorSpace);
                writer.WritePropertyName("colorKeys");
                writer.WriteStartArray();
                var colorKeys = gradient.colorKeys;
                for (var i = 0; i < colorKeys.Length; i++)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("color");
                    serializer.Serialize(writer, colorKeys[i].color);
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
                if (!(data["colorKeys"] is JArray) || !(data["alphaKeys"] is JArray))
                    throw new JsonSerializationException("Gradient must contain colorKeys and alphaKeys arrays.");
                var gradient = new Gradient
                {
                    mode = (GradientMode)(data["mode"]?.Value<int>() ?? 0),
                };
                if (data["colorSpace"] != null)
                {
                    gradient.colorSpace = (ColorSpace)data["colorSpace"].Value<int>();
                }
                var colorTokens = data["colorKeys"] as JArray;
                var colorKeys = new GradientColorKey[colorTokens?.Count ?? 0];
                for (var i = 0; i < colorKeys.Length; i++)
                {
                    var keyData = (JObject)colorTokens[i];
                    if (keyData["color"] == null || keyData["r"] != null || keyData["g"] != null ||
                        keyData["b"] != null || keyData["a"] != null)
                        throw new JsonSerializationException("A gradient color key must contain a color array, not flat color components.");
                    colorKeys[i] = new GradientColorKey(
                        keyData["color"].ToObject<Color>(serializer),
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

        }

        private sealed class BoundsConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Bounds);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var bounds = (Bounds)value;
                writer.WriteStartObject();
                writer.WritePropertyName("center");
                serializer.Serialize(writer, bounds.center);
                writer.WritePropertyName("extents");
                serializer.Serialize(writer, bounds.extents);
                writer.WriteEndObject();
            }

            public override object ReadJson(
                JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                var data = JObject.Load(reader);
                if (data["center"] == null || data["extents"] == null)
                {
                    throw new JsonSerializationException("A Bounds value must contain center and extents.");
                }

                return new Bounds
                {
                    center = data["center"].ToObject<Vector3>(serializer),
                    extents = data["extents"].ToObject<Vector3>(serializer),
                };
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
                if (reader.TokenType != JsonToken.String) throw new JsonSerializationException("Hash128 must be a string.");
                return Hash128.Parse((string)reader.Value);
            }
        }

        private sealed class NativePropertiesConverter : JsonConverter
        {
            private static readonly Dictionary<Type, PropertyInfo[]> Properties = BuildProperties();
            private static Dictionary<Type, PropertyInfo[]> BuildProperties()
            {
                var result = new Dictionary<Type, PropertyInfo[]>();
                Add(typeof(RectOffset), "left", "right", "top", "bottom");
                var renderingMask = typeof(LayerMask).Assembly.GetType("UnityEngine.RenderingLayerMask");
                if (renderingMask != null) Add(renderingMask, "value");
                return result;

                void Add(Type type, params string[] names)
                {
                    var properties = names.Select(name => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)).ToArray();
                    if (properties.All(property => property != null && property.CanRead && property.CanWrite)) result.Add(type, properties);
                }
            }
            internal static bool Supports(Type type) => Properties.ContainsKey(type);
            public override bool CanConvert(Type objectType) => Supports(objectType);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                foreach (var property in Properties[value.GetType()])
                {
                    writer.WritePropertyName(property.Name);
                    serializer.Serialize(writer, property.GetValue(value), property.PropertyType);
                }
                writer.WriteEndObject();
            }
            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null && !objectType.IsValueType) return null;
                var data = JObject.Load(reader);
                var value = Activator.CreateInstance(objectType);
                foreach (var property in Properties[objectType])
                {
                    var token = data[property.Name];
                    if (token == null) throw new JsonSerializationException($"{objectType.Name} is missing '{property.Name}'.");
                    property.SetValue(value, token.ToObject(property.PropertyType, serializer));
                }
                return value;
            }
        }

        private static void WriteNumber(JsonWriter writer, string propertyName, float value)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteValue(value);
        }
    }
}
