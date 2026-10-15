using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Kobold.Core
{
    /// <summary>
    /// Kind-discriminating converter for <see cref="WidgetData"/>. On write it
    /// puts "Kind" first, then the concrete type's fields; an
    /// <see cref="UnknownData"/> is written back verbatim. On read a missing or
    /// empty Kind means folder, a registered Kind picks the concrete type, and any
    /// other Kind becomes an <see cref="UnknownData"/> holding the raw JSON. The
    /// Kind-to-type mapping comes from <see cref="WidgetKindRegistry"/>.
    /// </summary>
    public sealed class WidgetDataConverter : JsonConverter
    {
        private static readonly DefaultContractResolver PayloadResolver = new PayloadContractResolver();

        public override bool CanConvert(Type objectType)
        {
            return typeof(WidgetData).IsAssignableFrom(objectType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var data = (WidgetData)value;

            // An unknown kind keeps its payload exactly as it was loaded.
            if (data is UnknownData unknown)
            {
                (unknown.Raw ?? new JObject()).WriteTo(writer);
                return;
            }

            var payload = JObject.FromObject(data, CreatePayloadSerializer());
            payload.Property("Kind")?.Remove();

            writer.WriteStartObject();
            writer.WritePropertyName("Kind");
            writer.WriteValue(KindOf(data));
            foreach (var property in payload.Properties())
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            var raw = JObject.Load(reader);
            string kind = (string)raw["Kind"];

            if (string.IsNullOrEmpty(kind)) kind = "folder";
            var entry = WidgetKindRegistry.Find(kind);
            if (entry == null) return new UnknownData { Kind = kind, Raw = raw };

            var data = (WidgetData)raw.ToObject(entry.DataType, CreatePayloadSerializer());
            data.Kind = kind;
            return data;
        }

        private static string KindOf(WidgetData data)
        {
            foreach (var entry in WidgetKindRegistry.All)
            {
                if (entry.DataType == data.GetType()) return entry.Id;
            }
            return data.Kind;
        }

        private static JsonSerializer CreatePayloadSerializer()
        {
            return JsonSerializer.Create(new JsonSerializerSettings { ContractResolver = PayloadResolver });
        }

        /// <summary>
        /// The payload is (de)serialized without this converter: the attribute
        /// on <see cref="WidgetData"/> is inherited by the concrete types, and
        /// leaving it in would recurse forever.
        /// </summary>
        private sealed class PayloadContractResolver : DefaultContractResolver
        {
            protected override JsonConverter ResolveContractConverter(Type objectType)
            {
                var converter = base.ResolveContractConverter(objectType);
                return converter is WidgetDataConverter ? null : converter;
            }
        }
    }
}
