using System;
using System.Collections.Generic;
using Kobold.Controls;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    /// <summary>
    /// The control plane's widget and group commands. Every method runs on the
    /// UI thread (the router dispatches there) and drives the existing
    /// <see cref="WidgetManager"/> API, so the live views, folder eject path,
    /// group dissolve and active-tab fallback stay exactly what the UI does.
    /// No method opens a panel or steals focus.
    /// </summary>
    public sealed class WidgetGroupControlService
    {
        /// <summary>
        /// Fields that describe identity, never a panel edit. They are spelled
        /// like the model's PascalCase properties: the update payload's own
        /// lowercase <c>id</c> is the addressed widget, not a field edit.
        /// </summary>
        private static readonly HashSet<string> ForbiddenUpdateFields =
            new HashSet<string>(StringComparer.Ordinal) { "Id", "Kind", "kind" };

        /// <summary>
        /// Structure is owned by the group commands; both JSON spellings are
        /// rejected so a caller cannot sneak membership/order past the API.
        /// </summary>
        private static readonly HashSet<string> ForbiddenUpdateStructure =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GroupId", "GroupOrder", "IsExpanded"
            };

        /// <summary>
        /// The complete widget.update payload surface. Anything else is a
        /// typo or a structural request and must fail instead of being ignored.
        /// </summary>
        private static readonly HashSet<string> AllowedUpdateFields =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "id", "name", "color", "locked", "pinned", "posX", "posY",
                "panelWidth", "panelHeight", "folder", "todo", "note"
            };

        private static readonly HashSet<string> HolidayRegions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                PublicHolidays.RegionNone,
                PublicHolidays.RegionCn,
                PublicHolidays.RegionJp,
                PublicHolidays.RegionUs,
                PublicHolidays.RegionUk
            };

        private readonly WidgetManager _manager;

        public WidgetGroupControlService(WidgetManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException("manager");
        }

        /// <summary>
        /// Creates a widget through the shared create core without opening it.
        /// The caller's name/color/position/pin are applied before the one
        /// island refresh, so the tile never shows the default name/color.
        /// </summary>
        public JObject Add(JObject p)
        {
            JObject parameters = p ?? new JObject();
            string kind = RequiredString(parameters, "kind");

            bool? open = OptionalBool(parameters, "open");
            if (open.HasValue && open.Value)
            {
                // A control add must never open a panel or steal focus.
                throw ControlException.Validation("widget.add never opens a panel; pass open:false.");
            }

            bool pinned = OptionalBool(parameters, "pinned") ?? false;
            string name = OptionalString(parameters, "name");
            string color = OptionalString(parameters, "color");
            int? posX = OptionalInt(parameters, "posX");
            int? posY = OptionalInt(parameters, "posY");

            IWidgetWindow widget = _manager.CreateWidgetForControl(kind);
            if (widget == null) throw ControlException.Validation("Unknown widget kind: " + kind);

            WidgetData data = widget.Data;
            if (name != null) data.Name = name.Trim();
            if (color != null) data.Color = color;
            if (posX.HasValue) data.PosX = posX.Value;
            if (posY.HasValue) data.PosY = posY.Value;
            data.IsPanelPinned = pinned;

            widget.UpdateUI();
            _manager.RefreshIsland();
            _manager.SaveConfig();

            AppDiagnostics.Write("widget.add id=" + data.Id + " kind=" + data.Kind);

            return new JObject
            {
                ["id"] = data.Id,
                ["kind"] = data.Kind,
                ["name"] = data.Name,
                ["pinned"] = data.IsPanelPinned,
                ["revision"] = ConfigRevision.Compute(_manager.Config)
            };
        }

        /// <summary>
        /// Validates the whole payload before touching the live data, then
        /// applies only the passed fields and reports each one that actually
        /// changed. A rejected payload therefore leaves the widget and the
        /// config revision untouched. A grouped member re-derives the group pin
        /// from all members before the host repaints; the island refreshes once.
        /// </summary>
        public JObject Update(JObject p)
        {
            JObject parameters = p ?? new JObject();
            foreach (var property in parameters.Properties())
            {
                if (ForbiddenUpdateFields.Contains(property.Name))
                {
                    throw ControlException.Forbidden(
                        "The widget identity is immutable: " + property.Name + ".");
                }
                if (ForbiddenUpdateStructure.Contains(property.Name))
                {
                    throw ControlException.Forbidden(
                        "Use the group commands for " + property.Name + ".");
                }
                if (!AllowedUpdateFields.Contains(property.Name))
                {
                    throw ControlException.Validation(
                        "Unknown widget.update field: " + property.Name + ".");
                }
            }

            string id = RequiredString(parameters, "id");
            WidgetData data = FindData(id);
            if (data == null) throw ControlException.Validation("Unknown widget: " + id);

            // --- Validate every field into locals before mutating anything. ---
            string name = OptionalString(parameters, "name");
            string trimmedName = name == null ? null : name.Trim();
            string color = OptionalString(parameters, "color");
            bool? locked = OptionalBool(parameters, "locked");
            bool? pinned = OptionalBool(parameters, "pinned");
            int? posX = OptionalInt(parameters, "posX");
            int? posY = OptionalInt(parameters, "posY");

            double? panelWidth = OptionalDouble(parameters, "panelWidth");
            if (panelWidth.HasValue) RequirePositive(panelWidth.Value, "panelWidth");

            double? panelHeight = OptionalDouble(parameters, "panelHeight");
            if (panelHeight.HasValue) RequirePositive(panelHeight.Value, "panelHeight");

            JObject folderPatch = OptionalObject(parameters, "folder");
            var folderData = data as FolderData;
            if (folderPatch != null && folderData == null)
            {
                throw ControlException.Validation(
                    "The folder block only applies to a folder widget.");
            }
            int? gridColumns = null;
            if (folderPatch != null)
            {
                gridColumns = OptionalInt(folderPatch, "gridColumns");
                if (gridColumns.HasValue && (gridColumns.Value < 2 || gridColumns.Value > 6))
                    throw ControlException.Validation("folder.gridColumns must be between 2 and 6.");
            }

            JObject todoPatch = OptionalObject(parameters, "todo");
            var todoData = data as TodoData;
            if (todoPatch != null && todoData == null)
            {
                throw ControlException.Validation(
                    "The todo block only applies to a todo widget.");
            }
            string holidayRegion = null;
            if (todoPatch != null)
            {
                holidayRegion = OptionalString(todoPatch, "holidayRegion");
                if (holidayRegion != null && !HolidayRegions.Contains(holidayRegion))
                    throw ControlException.Validation("Unknown todo.holidayRegion: " + holidayRegion);
            }

            JObject notePatch = OptionalObject(parameters, "note");
            var noteData = data as NoteData;
            if (notePatch != null && noteData == null)
            {
                throw ControlException.Validation(
                    "The note block only applies to a note widget.");
            }
            double? fontScale = null;
            if (notePatch != null)
            {
                fontScale = OptionalDouble(notePatch, "fontScale");
                if (fontScale.HasValue)
                {
                    RequirePositive(fontScale.Value, "note.fontScale");
                    fontScale = NoteFontScale.Normalize(fontScale.Value);
                }
            }

            // --- Validation passed: apply, derive the group pin and repaint. ---
            var changed = new List<string>();

            if (trimmedName != null
                && !string.Equals(data.Name, trimmedName, StringComparison.Ordinal))
            {
                data.Name = trimmedName;
                changed.Add("Name");
            }

            if (color != null && !string.Equals(data.Color, color, StringComparison.Ordinal))
            {
                data.Color = color;
                changed.Add("Color");
            }

            if (locked.HasValue && data.IsLocked != locked.Value)
            {
                data.IsLocked = locked.Value;
                changed.Add("IsLocked");
            }

            if (pinned.HasValue && data.IsPanelPinned != pinned.Value)
            {
                data.IsPanelPinned = pinned.Value;
                changed.Add("IsPanelPinned");
            }

            if (posX.HasValue && data.PosX != posX.Value)
            {
                data.PosX = posX.Value;
                changed.Add("PosX");
            }

            if (posY.HasValue && data.PosY != posY.Value)
            {
                data.PosY = posY.Value;
                changed.Add("PosY");
            }

            if (panelWidth.HasValue
                && (!data.PanelWidth.HasValue || data.PanelWidth.Value != panelWidth.Value))
            {
                data.PanelWidth = panelWidth.Value;
                changed.Add("PanelWidth");
            }

            if (panelHeight.HasValue
                && (!data.PanelHeight.HasValue || data.PanelHeight.Value != panelHeight.Value))
            {
                data.PanelHeight = panelHeight.Value;
                changed.Add("PanelHeight");
            }

            if (gridColumns.HasValue && folderData != null
                && folderData.GridColumns != gridColumns.Value)
            {
                folderData.GridColumns = gridColumns.Value;
                changed.Add("folder.GridColumns");
            }

            if (holidayRegion != null && todoData != null
                && !string.Equals(todoData.HolidayRegion, holidayRegion, StringComparison.Ordinal))
            {
                todoData.HolidayRegion = holidayRegion;
                changed.Add("todo.HolidayRegion");
            }

            if (fontScale.HasValue && noteData != null && noteData.FontScale != fontScale.Value)
            {
                noteData.FontScale = fontScale.Value;
                changed.Add("note.FontScale");
            }

            IWidgetWindow host = _manager.FindHostForWidget(id);
            var group = host as WidgetGroupWindow;
            if (group != null)
            {
                List<WidgetData> members = GroupHostPolicy.MembersOf(
                    group.Group, _manager.Config.Widgets);
                bool derivedPin = GroupHostPolicy.AnyMemberPinned(members);
                if (group.Group.IsPanelPinned != derivedPin)
                {
                    group.Group.IsPanelPinned = derivedPin;
                    if (!changed.Contains("IsPanelPinned")) changed.Add("IsPanelPinned");
                }
            }

            host?.UpdateUI();
            _manager.SaveConfig();
            _manager.RefreshIsland();

            AppDiagnostics.Write("widget.update id=" + id
                + " changed=" + string.Join(",", changed.ToArray()));

            return new JObject
            {
                ["changed"] = new JArray(changed),
                ["revision"] = ConfigRevision.Compute(_manager.Config)
            };
        }

        /// <summary>
        /// Deletes through <see cref="WidgetManager.RemoveWidget"/>, preserving
        /// folder eject-all and the group dissolve/active-tab fallback.
        /// </summary>
        public JObject Delete(JObject p)
        {
            JObject parameters = p ?? new JObject();
            if (!Confirmed(parameters))
                throw ControlException.Forbidden("widget.delete requires confirm:true.");

            string id = RequiredString(parameters, "id");
            IWidgetWindow host = _manager.FindHostForWidget(id);
            if (host == null)
                throw ControlException.Validation("Unknown widget: " + id);

            // A group id is a valid host id but not a widget: deleting it
            // through RemoveWidget would take every member. Group structure
            // leaves through group.dissolve instead. A grouped *member* id
            // resolves to the group host with a different WidgetId and stays
            // a valid delete target.
            var groupHost = host as WidgetGroupWindow;
            if (groupHost != null
                && string.Equals(groupHost.WidgetId, id, StringComparison.Ordinal))
            {
                throw ControlException.Validation(
                    "Widget id is a group; use group.dissolve for " + id + ".");
            }

            _manager.RemoveWidget(id);
            AppDiagnostics.Write("widget.delete id=" + id);

            return new JObject
            {
                ["removed"] = id,
                ["revision"] = ConfigRevision.Compute(_manager.Config)
            };
        }

        /// <summary>
        /// Merges standalone widgets into a standalone target or an existing
        /// group in the order supplied. Every source is validated before the
        /// first merge, so a bad member never leaves a partial group.
        /// </summary>
        public JObject Merge(JObject p)
        {
            JObject parameters = p ?? new JObject();
            string targetId = RequiredString(parameters, "targetId");

            var memberTokens = parameters["memberIds"] as JArray;
            if (memberTokens == null || memberTokens.Count == 0)
                throw ControlException.Validation("memberIds must contain at least one widget.");

            var memberIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in memberTokens)
            {
                if (token == null || token.Type != JTokenType.String)
                    throw ControlException.Validation("memberIds must contain widget id strings.");
                string memberId = (string)token;
                if (string.IsNullOrWhiteSpace(memberId))
                    throw ControlException.Validation("memberIds must not contain an empty id.");
                if (!seen.Add(memberId))
                    throw ControlException.Validation("Duplicate merge member: " + memberId);
                memberIds.Add(memberId);
            }

            IWidgetWindow target = _manager.FindHostForWidget(targetId);
            if (target == null)
                throw ControlException.Validation("Unknown target: " + targetId);
            if (target.Data == null || target.Data.IsLocked)
                throw ControlException.Validation("The target is locked: " + targetId);
            var targetGroup = target as WidgetGroupWindow;
            if (targetGroup != null && targetGroup.Group.IsLocked)
                throw ControlException.Validation("The target group is locked: " + targetId);

            foreach (string memberId in memberIds)
            {
                if (string.Equals(memberId, targetId, StringComparison.Ordinal))
                    throw ControlException.Validation("A widget cannot merge into itself: " + memberId);

                IWidgetWindow source = _manager.FindHostForWidget(memberId);
                if (source == null)
                    throw ControlException.Validation("Unknown merge member: " + memberId);
                if (source is WidgetGroupWindow)
                    throw ControlException.Validation("Source must be a standalone widget: " + memberId);
                if (source.Data == null || source.Data.IsLocked)
                    throw ControlException.Validation("The source is locked: " + memberId);
                if (!GroupDragPolicy.CanMerge(source, target))
                    throw ControlException.Validation("Merge refused for " + memberId);
            }

            foreach (string memberId in memberIds)
            {
                IWidgetWindow source = _manager.FindHostForWidget(memberId);
                if (source == null || source is WidgetGroupWindow)
                    throw ControlException.Validation("Source is no longer a standalone widget: " + memberId);
                if (!_manager.MergeWidgets(source, target))
                    throw ControlException.Validation("Merge refused for " + memberId);

                // The first merge replaces a standalone target with a group
                // window, so every following member targets the new host.
                target = _manager.FindHostForWidget(targetId);
                if (target == null)
                    throw ControlException.Validation("The merge target disappeared: " + targetId);
            }

            var mergedGroup = _manager.FindHostForWidget(targetId) as WidgetGroupWindow;
            if (mergedGroup == null)
                throw ControlException.Validation("The merge produced no group for " + targetId);

            AppDiagnostics.Write("group.merge target=" + targetId
                + " members=" + memberIds.Count.ToString());

            return new JObject
            {
                ["groupId"] = mergedGroup.WidgetId,
                ["revision"] = ConfigRevision.Compute(_manager.Config)
            };
        }

        /// <summary>
        /// Detaches one tab. The group survives when at least two tabs remain;
        /// the last pair reports <c>dissolved:true</c>.
        /// </summary>
        public JObject Detach(JObject p)
        {
            JObject parameters = p ?? new JObject();
            string groupId = RequiredString(parameters, "groupId");
            string widgetId = RequiredString(parameters, "widgetId");

            var host = _manager.FindHostForWidget(groupId) as WidgetGroupWindow;
            if (host == null) throw ControlException.Validation("Unknown group: " + groupId);
            if (host.Group == null || host.Group.IsLocked)
                throw ControlException.Validation("The group is locked: " + groupId);
            if (!host.ContainsWidget(widgetId))
                throw ControlException.Validation("Widget is not a group member: " + widgetId);

            int memberCount = GroupHostPolicy.MembersOf(host.Group, _manager.Config.Widgets).Count;
            if (memberCount < 2)
                throw ControlException.Validation("The group has fewer than two members: " + groupId);
            bool dissolved = memberCount == 2;

            if (!_manager.DetachWidget(groupId, widgetId))
                throw ControlException.Validation("Detach refused for " + widgetId);

            AppDiagnostics.Write("group.detach group=" + groupId + " widget=" + widgetId
                + " dissolved=" + (dissolved ? "true" : "false"));

            return new JObject
            {
                ["standaloneId"] = widgetId,
                ["dissolved"] = dissolved,
                ["revision"] = ConfigRevision.Compute(_manager.Config)
            };
        }

        /// <summary>Reorders a tab; locked groups, bad indices and no-ops are refused.</summary>
        public JObject Reorder(JObject p)
        {
            JObject parameters = p ?? new JObject();
            string groupId = RequiredString(parameters, "groupId");
            int from = RequiredInt(parameters, "from");
            int to = RequiredInt(parameters, "to");

            var host = _manager.FindHostForWidget(groupId) as WidgetGroupWindow;
            if (host == null) throw ControlException.Validation("Unknown group: " + groupId);
            if (host.Group == null || host.Group.IsLocked)
                throw ControlException.Validation("The group is locked: " + groupId);

            if (!_manager.ReorderGroupMember(groupId, from, to))
                throw ControlException.Validation("Reorder refused: " + from + " -> " + to);

            AppDiagnostics.Write("group.reorder group=" + groupId + " from=" + from + " to=" + to);

            return new JObject
            {
                ["revision"] = ConfigRevision.Compute(_manager.Config)
            };
        }

        /// <summary>
        /// Detaches every member until the group is gone and returns every
        /// original member id, including the survivor that was never detached.
        /// </summary>
        public JObject Dissolve(JObject p)
        {
            JObject parameters = p ?? new JObject();
            if (!Confirmed(parameters))
                throw ControlException.Forbidden("group.dissolve requires confirm:true.");

            string groupId = RequiredString(parameters, "groupId");
            var host = _manager.FindHostForWidget(groupId) as WidgetGroupWindow;
            if (host == null) throw ControlException.Validation("Unknown group: " + groupId);
            if (host.Group == null || host.Group.IsLocked)
                throw ControlException.Validation("The group is locked: " + groupId);

            List<WidgetData> members = GroupHostPolicy.MembersOf(host.Group, _manager.Config.Widgets);
            if (members.Count < 2)
                throw ControlException.Validation("The group has fewer than two members: " + groupId);

            var originalIds = new List<string>(members.Count);
            foreach (WidgetData member in members)
            {
                if (member == null || string.IsNullOrEmpty(member.Id))
                    throw ControlException.Validation("The group has a member without an id: " + groupId);
                originalIds.Add(member.Id);
            }

            while (true)
            {
                var current = _manager.FindHostForWidget(groupId) as WidgetGroupWindow;
                if (current == null) break;
                if (current.Members.Count < 2) break;

                string memberId = current.Members[0].Id;
                if (!_manager.DetachWidget(groupId, memberId))
                    throw ControlException.Validation("Dissolve refused for " + memberId);
            }

            if (_manager.FindHostForWidget(groupId) is WidgetGroupWindow)
                throw ControlException.Validation("Dissolve left the group alive: " + groupId);

            AppDiagnostics.Write("group.dissolve group=" + groupId
                + " members=" + originalIds.Count.ToString());

            return new JObject
            {
                ["standaloneIds"] = new JArray(originalIds),
                ["revision"] = ConfigRevision.Compute(_manager.Config)
            };
        }

        /// <summary>The config's widget with this id, or null.</summary>
        private WidgetData FindData(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (WidgetData widget in _manager.Config.Widgets)
            {
                if (widget != null && string.Equals(widget.Id, id, StringComparison.Ordinal))
                    return widget;
            }
            return null;
        }

        private static bool Confirmed(JObject p)
        {
            JToken token = p == null ? null : p["confirm"];
            return token != null && token.Type == JTokenType.Boolean && (bool)token;
        }

        private static JToken Token(JObject p, string name)
        {
            return p == null ? null : p[name];
        }

        private static bool HasValue(JObject p, string name)
        {
            JToken token = Token(p, name);
            return token != null && token.Type != JTokenType.Null;
        }

        private static string RequiredString(JObject p, string name)
        {
            JToken token = Token(p, name);
            if (token == null || token.Type == JTokenType.Null)
                throw ControlException.Validation("Missing '" + name + "'.");
            if (token.Type != JTokenType.String)
                throw ControlException.Validation("'" + name + "' must be a string.");

            string value = (string)token;
            if (string.IsNullOrWhiteSpace(value))
                throw ControlException.Validation("'" + name + "' must not be empty.");
            return value;
        }

        private static string OptionalString(JObject p, string name)
        {
            if (!HasValue(p, name)) return null;
            JToken token = Token(p, name);
            if (token.Type != JTokenType.String)
                throw ControlException.Validation("'" + name + "' must be a string.");
            return (string)token;
        }

        private static bool? OptionalBool(JObject p, string name)
        {
            if (!HasValue(p, name)) return null;
            JToken token = Token(p, name);
            if (token.Type != JTokenType.Boolean)
                throw ControlException.Validation("'" + name + "' must be a boolean.");
            return (bool)token;
        }

        private static int? OptionalInt(JObject p, string name)
        {
            if (!HasValue(p, name)) return null;
            JToken token = Token(p, name);

            try
            {
                if (token.Type == JTokenType.Integer)
                {
                    long value = token.Value<long>();
                    if (value < int.MinValue || value > int.MaxValue)
                        throw ControlException.Validation("'" + name + "' is out of range.");
                    return (int)value;
                }

                if (token.Type == JTokenType.Float)
                {
                    double value = token.Value<double>();
                    if (double.IsNaN(value) || double.IsInfinity(value)
                        || value != Math.Floor(value)
                        || value < int.MinValue || value > int.MaxValue)
                    {
                        throw ControlException.Validation("'" + name + "' must be an integer.");
                    }
                    return (int)value;
                }
            }
            catch (ControlException)
            {
                throw;
            }
            catch (Exception)
            {
                throw ControlException.Validation("'" + name + "' must be an integer.");
            }

            throw ControlException.Validation("'" + name + "' must be an integer.");
        }

        private static int RequiredInt(JObject p, string name)
        {
            int? value = OptionalInt(p, name);
            if (!value.HasValue)
                throw ControlException.Validation("Missing '" + name + "'.");
            return value.Value;
        }

        private static double? OptionalDouble(JObject p, string name)
        {
            if (!HasValue(p, name)) return null;
            JToken token = Token(p, name);
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw ControlException.Validation("'" + name + "' must be a number.");

            double value;
            try
            {
                value = token.Value<double>();
            }
            catch (Exception)
            {
                throw ControlException.Validation("'" + name + "' must be a number.");
            }
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw ControlException.Validation("'" + name + "' must be finite.");
            return value;
        }

        private static JObject OptionalObject(JObject p, string name)
        {
            if (!HasValue(p, name)) return null;
            var value = Token(p, name) as JObject;
            if (value == null)
                throw ControlException.Validation("'" + name + "' must be an object.");
            return value;
        }

        private static void RequirePositive(double value, string name)
        {
            if (value <= 0)
                throw ControlException.Validation("'" + name + "' must be greater than zero.");
        }
    }
}
