using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.Core
{
    /// <summary>
    /// State of one tab group: the active member, the shared panel geometry
    /// and the group-level open/lock/pin flags. Membership itself lives on the
    /// widgets (<see cref="WidgetData.GroupId"/> and
    /// <see cref="WidgetData.GroupOrder"/>); this type never holds a child list.
    /// </summary>
    public class WidgetGroup
    {
        public string Id { get; set; }
        public string ActiveWidgetId { get; set; }
        public double? PanelX { get; set; }
        public double? PanelY { get; set; }
        public double? PanelDpiScale { get; set; }
        public double? PanelWidth { get; set; }
        public double? PanelHeight { get; set; }
        public bool IsExpanded { get; set; }
        public bool IsLocked { get; set; }
        public bool IsPanelPinned { get; set; }
    }

    /// <summary>
    /// Tolerant reader for the config's "Groups" block. A hand-edited or
    /// damaged block must never make the whole config unreadable (which would
    /// hide the widgets behind the evidence/default path): a non-array reads
    /// as no groups, and each bad array entry - a null, a non-object, or an
    /// object with a wrong-typed field - is skipped on its own. Any
    /// memberships pointing at a skipped group are cleared later by
    /// <see cref="AppConfig"/>'s repair, so the widgets themselves survive.
    /// </summary>
    public class WidgetGroupListConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(List<WidgetGroup>);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            JToken token = JToken.Load(reader);
            if (token.Type != JTokenType.Array) return null;

            var groups = new List<WidgetGroup>();
            foreach (JToken entry in (JArray)token)
            {
                if (entry.Type != JTokenType.Object) continue;

                try
                {
                    var group = entry.ToObject<WidgetGroup>(serializer);
                    if (group != null) groups.Add(group);
                }
                catch (Exception)
                {
                    // A bad field type skips this group only; the rest of the
                    // config (and this array) stays usable.
                }
            }
            return groups;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var groups = value as List<WidgetGroup>;
            if (groups == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartArray();
            foreach (WidgetGroup group in groups)
            {
                // WidgetGroup itself never matches CanConvert, so this does not
                // recurse back into this converter.
                serializer.Serialize(writer, group);
            }
            writer.WriteEndArray();
        }
    }
}
