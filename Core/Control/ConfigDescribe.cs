using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Kobold.Core.FileSearch;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Reflection-generated schema for the config document (spec 5.2). It only
    /// publishes field names, JSON types, enum values and ranges - never user
    /// data - so a new config field or widget kind shows up here automatically.
    /// </summary>
    public static class ConfigDescribe
    {
        private static readonly Dictionary<string, string[]> AllowedValues =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "Language", new[] { "en", "zh", "ja" } },
                { "Theme", new[] { "dark", "light", "high contrast" } },
                { "IconStyle", new[] { "classic", "handdrawn" } }
            };

        private static readonly Dictionary<string, double[]> Ranges =
            new Dictionary<string, double[]>(StringComparer.Ordinal)
            {
                { "PanelOpacity", new[] { 0.2, 1.0 } }
            };

        public static JObject Build()
        {
            var topLevel = new JArray();
            foreach (var property in PublicInstanceProperties(typeof(AppConfig)))
            {
                topLevel.Add(DescribeProperty(property));
            }

            var widgetKinds = new JArray();
            foreach (var kind in WidgetKindRegistry.All)
            {
                var fields = new JArray();
                if (kind.DataType != null)
                {
                    foreach (var property in PublicInstanceProperties(kind.DataType, declaredOnly: true))
                    {
                        fields.Add(property.Name);
                    }
                }
                widgetKinds.Add(new JObject
                {
                    ["id"] = kind.Id,
                    ["fields"] = fields
                });
            }

            return new JObject
            {
                ["schemaVersion"] = AppConfig.CurrentSchemaVersion,
                ["topLevel"] = topLevel,
                ["widgetKinds"] = widgetKinds,
                ["groups"] = new JArray(PublicInstanceProperties(typeof(WidgetGroup))
                    .Select(property => property.Name)),
                ["fileSearch"] = new JArray(PublicInstanceProperties(typeof(FileSearchSettings))
                    .Select(property => property.Name))
            };
        }

        private static JObject DescribeProperty(PropertyInfo property)
        {
            var entry = new JObject
            {
                ["name"] = property.Name,
                ["type"] = JsonTypeOf(property.PropertyType)
            };

            string[] allowed;
            if (AllowedValues.TryGetValue(property.Name, out allowed))
            {
                entry["allowed"] = new JArray(allowed);
            }

            double[] range;
            if (Ranges.TryGetValue(property.Name, out range))
            {
                entry["range"] = new JArray(range);
            }

            return entry;
        }

        /// <summary>
        /// Public instance properties in declaration order; indexers are not
        /// fields and are skipped.
        /// </summary>
        private static IEnumerable<PropertyInfo> PublicInstanceProperties(
            Type type, bool declaredOnly = false)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (declaredOnly) flags |= BindingFlags.DeclaredOnly;

            return type.GetProperties(flags)
                .Where(property => property.GetIndexParameters().Length == 0)
                .OrderBy(property => property.MetadataToken);
        }

        /// <summary>
        /// JSON-shaped type name the caller sees in the document, so nullable
        /// numbers and arrays do not leak CLR spellings.
        /// </summary>
        private static string JsonTypeOf(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string) || underlying == typeof(DateTime)) return "string";
            if (underlying == typeof(bool)) return "boolean";
            if (underlying.IsEnum) return "string";
            if (underlying == typeof(int) || underlying == typeof(long)
                || underlying == typeof(short) || underlying == typeof(double)
                || underlying == typeof(float) || underlying == typeof(decimal))
            {
                return "number";
            }
            if (underlying.IsArray) return "array";
            if (typeof(IEnumerable).IsAssignableFrom(underlying)) return "array";
            return "object";
        }
    }
}
