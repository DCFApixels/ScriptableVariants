using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;

namespace DCFApixels.ScriptableVariants.Editor
{
    // These stateless converters are attached to cached contracts, not scanned via CanConvert
    // for every value. The new array path uses direct components: no reflection or float[] buffers.
    internal static class VariantNumericConverters
    {
        private static readonly Dictionary<Type, JsonConverter> Converters = new Dictionary<Type, JsonConverter>
        {
            {typeof(Vector2), new Vector2Converter()},
            {typeof(Vector3), new Vector3Converter()},
            {typeof(Vector4), new Vector4Converter()},
            {typeof(Vector2Int), new Vector2IntConverter()},
            {typeof(Vector3Int), new Vector3IntConverter()},
            {typeof(Quaternion), new QuaternionConverter()},
            {typeof(Color), new ColorConverter()},
            {typeof(Color32), new Color32Converter()},
        };

        internal static JsonConverter Get(Type type) => Converters.TryGetValue(type, out var converter) ? converter : null;

        private abstract class TupleConverter<T> : JsonConverter<T> where T : struct
        {
            private readonly int _componentCount;
            protected TupleConverter(int componentCount) { _componentCount = componentCount; }

            public sealed override void WriteJson(JsonWriter writer, T value, JsonSerializer serializer)
            {
                writer.WriteStartArray();
                WriteComponents(writer, value);
                writer.WriteEndArray();
            }

            public sealed override T ReadJson(JsonReader reader, Type objectType, T existingValue,
                bool hasExistingValue, JsonSerializer serializer)
            {
                if (reader.TokenType != JsonToken.StartArray) throw Invalid(reader, $"{typeof(T).Name} must be a numeric array");
                var value = ReadComponents(reader);
                if (!ReadNext(reader) || reader.TokenType != JsonToken.EndArray)
                    throw Invalid(reader, $"{typeof(T).Name} requires exactly {_componentCount} components");
                return value;
            }

            protected abstract void WriteComponents(JsonWriter writer, T value);
            protected abstract T ReadComponents(JsonReader reader);
        }

        private sealed class Vector2Converter : TupleConverter<Vector2>
        {
            internal Vector2Converter() : base(2) { }
            protected override void WriteComponents(JsonWriter writer, Vector2 value)
            { writer.WriteValue(value.x); writer.WriteValue(value.y); }
            protected override Vector2 ReadComponents(JsonReader reader) => new Vector2(ReadSingle(reader), ReadSingle(reader));
        }

        private sealed class Vector3Converter : TupleConverter<Vector3>
        {
            internal Vector3Converter() : base(3) { }
            protected override void WriteComponents(JsonWriter writer, Vector3 value)
            { writer.WriteValue(value.x); writer.WriteValue(value.y); writer.WriteValue(value.z); }
            protected override Vector3 ReadComponents(JsonReader reader) => new Vector3(ReadSingle(reader), ReadSingle(reader), ReadSingle(reader));
        }

        private sealed class Vector4Converter : TupleConverter<Vector4>
        {
            internal Vector4Converter() : base(4) { }
            protected override void WriteComponents(JsonWriter writer, Vector4 value)
            { writer.WriteValue(value.x); writer.WriteValue(value.y); writer.WriteValue(value.z); writer.WriteValue(value.w); }
            protected override Vector4 ReadComponents(JsonReader reader) => new Vector4(ReadSingle(reader), ReadSingle(reader), ReadSingle(reader), ReadSingle(reader));
        }

        private sealed class Vector2IntConverter : TupleConverter<Vector2Int>
        {
            internal Vector2IntConverter() : base(2) { }
            protected override void WriteComponents(JsonWriter writer, Vector2Int value)
            { writer.WriteValue(value.x); writer.WriteValue(value.y); }
            protected override Vector2Int ReadComponents(JsonReader reader) => new Vector2Int(ReadInt32(reader), ReadInt32(reader));
        }

        private sealed class Vector3IntConverter : TupleConverter<Vector3Int>
        {
            internal Vector3IntConverter() : base(3) { }
            protected override void WriteComponents(JsonWriter writer, Vector3Int value)
            { writer.WriteValue(value.x); writer.WriteValue(value.y); writer.WriteValue(value.z); }
            protected override Vector3Int ReadComponents(JsonReader reader) => new Vector3Int(ReadInt32(reader), ReadInt32(reader), ReadInt32(reader));
        }

        private sealed class QuaternionConverter : TupleConverter<Quaternion>
        {
            internal QuaternionConverter() : base(4) { }
            protected override void WriteComponents(JsonWriter writer, Quaternion value)
            { writer.WriteValue(value.x); writer.WriteValue(value.y); writer.WriteValue(value.z); writer.WriteValue(value.w); }
            protected override Quaternion ReadComponents(JsonReader reader) => new Quaternion(ReadSingle(reader), ReadSingle(reader), ReadSingle(reader), ReadSingle(reader));
        }

        private sealed class ColorConverter : TupleConverter<Color>
        {
            internal ColorConverter() : base(4) { }
            protected override void WriteComponents(JsonWriter writer, Color value)
            { writer.WriteValue(value.r); writer.WriteValue(value.g); writer.WriteValue(value.b); writer.WriteValue(value.a); }
            protected override Color ReadComponents(JsonReader reader) => new Color(ReadSingle(reader), ReadSingle(reader), ReadSingle(reader), ReadSingle(reader));
        }

        private sealed class Color32Converter : TupleConverter<Color32>
        {
            internal Color32Converter() : base(4) { }
            protected override void WriteComponents(JsonWriter writer, Color32 value)
            { writer.WriteValue(value.r); writer.WriteValue(value.g); writer.WriteValue(value.b); writer.WriteValue(value.a); }
            protected override Color32 ReadComponents(JsonReader reader) => new Color32(ReadByte(reader), ReadByte(reader), ReadByte(reader), ReadByte(reader));
        }

        private static float ReadSingle(JsonReader reader)
        {
            if (!ReadNext(reader)) throw Invalid(reader, "Missing floating-point component");
            if (reader.TokenType == JsonToken.String)
            {
                // JSON has no non-finite number literals. Keep Json.NET's explicit spellings,
                // but never accept localized numbers or format finite numbers through strings.
                switch ((string)reader.Value)
                {
                    case "NaN": return float.NaN;
                    case "Infinity": return float.PositiveInfinity;
                    case "-Infinity": return float.NegativeInfinity;
                }
            }
            if (reader.TokenType != JsonToken.Integer && reader.TokenType != JsonToken.Float)
                throw Invalid(reader, "Expected a floating-point number");
            if (reader.Value is float single) return single;
            if (reader.Value is System.Numerics.BigInteger integer)
            {
                var number = (float)integer;
                if (float.IsInfinity(number)) throw Invalid(reader, "Component exceeds the Single range");
                return number;
            }
            var wide = Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
            var result = (float)wide;
            if (float.IsInfinity(result) && !double.IsInfinity(wide))
                throw Invalid(reader, "Component exceeds the Single range");
            return result;
        }

        private static int ReadInt32(JsonReader reader)
        {
            if (!ReadNext(reader) || reader.TokenType != JsonToken.Integer)
                throw Invalid(reader, "Expected an integer component");
            if (reader.Value is int integer) return integer;
            if (reader.Value is System.Numerics.BigInteger big)
            {
                if (big < int.MinValue || big > int.MaxValue) throw Invalid(reader, "Component exceeds the Int32 range");
                return (int)big;
            }
            try { return Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture); }
            catch (OverflowException) { throw Invalid(reader, "Component exceeds the Int32 range"); }
        }

        private static byte ReadByte(JsonReader reader)
        {
            var value = ReadInt32(reader);
            if (value < byte.MinValue || value > byte.MaxValue) throw Invalid(reader, "Color32 components must be in the range 0..255");
            return (byte)value;
        }

        private static bool ReadNext(JsonReader reader)
        {
            while (reader.Read())
                if (reader.TokenType != JsonToken.Comment) return true;
            return false;
        }

        private static JsonSerializationException Invalid(JsonReader reader, string message) =>
            new JsonSerializationException($"{message} (path '{reader.Path}').");
    }
}
