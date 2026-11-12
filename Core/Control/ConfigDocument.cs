using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Reads, validates and renders a config document for the control plane.
    /// Everything here works on a temporary copy: nothing is read from or
    /// written to disk, and the live config is never touched.
    /// </summary>
    public static class ConfigDocument
    {
        /// <summary>
        /// Parses a complete config document and runs the same repair/normalize
        /// pass <see cref="AppConfig"/> applies on load. Returns null when the
        /// document cannot be read; the reason is then the only warning.
        /// </summary>
        public static AppConfig Parse(string json, out List<string> warnings)
        {
            warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var config = JsonConvert.DeserializeObject<AppConfig>(json);
                if (config == null) return null;
                if (!IsStructurallySound(config, warnings)) return null;
                config.RepairForControl();
                return config;
            }
            catch (Exception ex)
            {
                warnings.Add(ex.Message);
                return null;
            }
        }

        /// <summary>True when <see cref="Parse"/> produced a usable config.</summary>
        public static bool Validate(string json, out List<string> warnings)
        {
            return Parse(json, out warnings) != null;
        }

        /// <summary>
        /// Mirrors the structural checks <see cref="AppConfig"/> applies on load
        /// before repair: an explicitly null widget list, a null entry, or a
        /// null per-kind item list would make the next startup treat the file
        /// as unreadable, so the control plane must reject it here.
        /// </summary>
        private static bool IsStructurallySound(AppConfig config, List<string> warnings)
        {
            if (config.Widgets == null)
            {
                warnings.Add("config Widgets is null");
                return false;
            }

            foreach (var widget in config.Widgets)
            {
                if (widget == null)
                {
                    warnings.Add("config Widgets holds a null entry");
                    return false;
                }
                if (widget is FolderData folder && folder.Items == null)
                {
                    warnings.Add("config folder Items is null");
                    return false;
                }
                if (widget is TodoData todo && todo.Items == null)
                {
                    warnings.Add("config todo Items is null");
                    return false;
                }
                if (widget is NoteData note && note.Notes == null)
                {
                    warnings.Add("config note Notes is null");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Renders a config as JSON. Full keeps every field (note/todo/folder
        /// contents included); the summary drops the payload lists, the legacy
        /// <c>Folders</c> block and the internal <c>HideIconsOriginal</c>
        /// record so only structure and settings remain.
        /// </summary>
        public static string ToJson(AppConfig config, bool full)
        {
            if (config == null) return "{}";
            if (full)
                return JsonConvert.SerializeObject(config, Formatting.Indented);

            var root = JObject.FromObject(config);
            if (root["Widgets"] is JArray widgets)
            {
                foreach (var widget in widgets.OfType<JObject>())
                {
                    widget.Remove("Items");
                    widget.Remove("Notes");
                    widget.Remove("BrowseStack");
                }
            }
            root.Remove("FrequentFiles");
            // The legacy widget list is migrated into Widgets on load; the
            // summary never returns it (spec 5.1).
            root.Remove("Folders");
            // Internal bookkeeping for the desktop-icon guard; the summary
            // never returns it (spec 5.1).
            root.Remove("HideIconsOriginal");
            return root.ToString(Formatting.Indented);
        }
    }
}
