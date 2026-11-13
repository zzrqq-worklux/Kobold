using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Server-side semantic limits for the hot fields a control client may patch
    /// (spec 5.1). <see cref="ConfigDocument"/> only checks shapes, and the
    /// settings UI clamps its own inputs; without this, a raw protocol call could
    /// store an opacity of 5, a negative collapse delay or a theme name no window
    /// knows, and every panel would render with it.
    ///
    /// The rules only apply to the fields the patch names, never to the whole
    /// document: a config that already carries an out-of-range legacy value can
    /// still be patched somewhere else, and config.replace keeps its own path.
    /// </summary>
    internal static class ConfigLimits
    {
        private static readonly Dictionary<string, double[]> Ranges =
            new Dictionary<string, double[]>(StringComparer.Ordinal)
            {
                { "PanelOpacity", new[] { 0.2, 1.0 } },
                { "ItemScale", new[] { 0.5, 3.0 } },
                { "IslandCollapseDelay", new[] { 0.0, 60.0 } },
                { "DefaultGridColumns", new[] { 1.0, 12.0 } }
            };

        private static readonly Dictionary<string, string[]> Choices =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "Language", new[] { "en", "zh", "ja" } },
                { "Theme", new[] { "dark", "light", "high contrast" } },
                { "IconStyle", new[] { "classic", "handdrawn" } }
            };

        /// <summary>
        /// Appends one message per offending field. An explicit null means
        /// "remove the field" (RFC 7386), so it is left to the defaults instead
        /// of being rejected.
        /// </summary>
        public static void ValidatePatch(JObject patch, List<string> errors)
        {
            if (patch == null || errors == null) return;

            foreach (JProperty property in patch.Properties())
            {
                JToken value = property.Value;
                if (value == null || value.Type == JTokenType.Null) continue;

                string[] choices;
                if (Choices.TryGetValue(property.Name, out choices))
                {
                    if (value.Type != JTokenType.String
                        || Array.IndexOf(choices, (string)value) < 0)
                    {
                        errors.Add(property.Name + " must be one of: "
                            + string.Join(", ", choices));
                    }
                    continue;
                }

                if (property.Name == "IslandX")
                {
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                        errors.Add("IslandX must be a number or null.");
                    else if (!IsFinite((double)value))
                        errors.Add("IslandX must be a finite number.");
                    continue;
                }

                double[] range;
                if (!Ranges.TryGetValue(property.Name, out range)) continue;

                if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                {
                    errors.Add(property.Name + " must be a number.");
                    continue;
                }

                double number = (double)value;
                if (!IsFinite(number) || number < range[0] || number > range[1])
                {
                    errors.Add(property.Name + " must be between "
                        + Format(range[0]) + " and " + Format(range[1])
                        + " (got " + Format(number) + ").");
                }
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
