using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.Core
{
    /// <summary>
    /// Shared data for every widget kind: identity, panel geometry and the
    /// "Kind" discriminator that <see cref="WidgetDataConverter"/> uses to pick
    /// the concrete type on load.
    /// </summary>
    [JsonConverter(typeof(WidgetDataConverter))]
    public abstract class WidgetData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public int PosX { get; set; }
        public int PosY { get; set; }
        public bool IsExpanded { get; set; }
        public bool IsLocked { get; set; }
        public bool IsPanelPinned { get; set; }
        public double? PanelX { get; set; }
        public double? PanelY { get; set; }
        public double? PanelDpiScale { get; set; }
        public double? PanelWidth { get; set; }
        public double? PanelHeight { get; set; }
        public string Kind { get; set; }

        /// <summary>
        /// Tab-group membership: the group this widget shows in, null while it
        /// is its own panel. The group state itself lives in
        /// <see cref="WidgetGroup"/>; these two fields are only the index.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string GroupId { get; set; }

        /// <summary>Position of this widget's tab inside its group (0-based).</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? GroupOrder { get; set; }
    }

    /// <summary>
    /// A widget written by a newer/unknown kind. It is never turned into a
    /// window; its raw JSON is kept so a save writes it back untouched.
    /// </summary>
    public class UnknownData : WidgetData
    {
        public JObject Raw { get; set; }
    }
}
