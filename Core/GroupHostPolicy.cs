using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// One member descriptor the folded island group tile draws: its kind for
    /// the thumbnail renderer, its colour, its display name and its own pin.
    /// </summary>
    public sealed class WidgetGroupTileMember
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public bool IsPinned { get; set; }
    }

    /// <summary>
    /// The island's view-model for one folded group tile: identity, active
    /// member, the member names its tooltip shows, its open state, the pinned
    /// count and the ordered member descriptors its 2x2 thumbnails use. Built
    /// by the manager and handed to <see cref="Controls.IslandWindow"/>; the
    /// island never reads the group model itself.
    /// </summary>
    public sealed class WidgetGroupTileInfo
    {
        public string GroupId { get; set; }
        public string ActiveWidgetId { get; set; }
        public int MemberCount { get; set; }
        public string MemberNames { get; set; }
        public bool IsOpen { get; set; }
        public int PinnedCount { get; set; }

        /// <summary>
        /// True while the island row shows this group's member icon tiles
        /// inline, immediately after the folded group tile.
        /// </summary>
        public bool IslandExpanded { get; set; }

        public List<WidgetGroupTileMember> Members { get; set; } = new List<WidgetGroupTileMember>();
    }

    /// <summary>
    /// Pure membership rules shared by the manager and the island. Membership
    /// lives on the widgets (<see cref="WidgetData.GroupId"/> /
    /// <see cref="WidgetData.GroupOrder"/>); these helpers only project it, so
    /// they stay free of windows, config and WPF types.
    /// </summary>
    public static class GroupHostPolicy
    {
        /// <summary>
        /// True when at least one member carries its own pin. The group's pin
        /// is derived from this: a pinned member keeps the whole group window
        /// open on blur, whichever tab is currently active.
        /// </summary>
        public static bool AnyMemberPinned(IEnumerable<WidgetData> members)
        {
            if (members == null) return false;
            foreach (var member in members)
            {
                if (member != null && member.IsPanelPinned) return true;
            }
            return false;
        }

        /// <summary>The number of members carrying their own pin.</summary>
        public static int PinnedCount(IEnumerable<WidgetData> members)
        {
            if (members == null) return 0;

            int count = 0;
            foreach (var member in members)
            {
                if (member != null && member.IsPanelPinned) count++;
            }
            return count;
        }

        /// <summary>
        /// One folded island tile per group with at least two members. Groups
        /// keep their config order; each tile's members keep GroupOrder so the
        /// thumbnails never reshuffle when the active tab changes. The island
        /// consumes this projection instead of reading the group model.
        /// </summary>
        public static List<WidgetGroupTileInfo> BuildIslandGroupTiles(
            IEnumerable<WidgetGroup> groups, IEnumerable<WidgetData> widgets,
            Func<string, bool> isHostOpen)
        {
            var tiles = new List<WidgetGroupTileInfo>();
            if (groups == null || widgets == null) return tiles;

            var widgetList = widgets as IList<WidgetData> ?? new List<WidgetData>(widgets);
            foreach (var group in groups)
            {
                if (group == null || string.IsNullOrEmpty(group.Id)) continue;

                var members = MembersOf(group, widgetList);
                if (members.Count < 2) continue;

                var names = new List<string>();
                var tile = new WidgetGroupTileInfo
                {
                    GroupId = group.Id,
                    ActiveWidgetId = ActiveMemberId(group, widgetList),
                    MemberCount = members.Count,
                    IsOpen = isHostOpen != null && isHostOpen(group.Id),
                    PinnedCount = PinnedCount(members),
                    IslandExpanded = group.IslandExpanded,
                    Members = new List<WidgetGroupTileMember>(members.Count)
                };

                foreach (var member in members)
                {
                    tile.Members.Add(new WidgetGroupTileMember
                    {
                        Id = member.Id,
                        Kind = member.Kind,
                        Name = member.Name,
                        Color = member.Color,
                        IsPinned = member.IsPanelPinned
                    });
                    names.Add(MemberLabel(member));
                }

                tile.MemberNames = string.Join(" / ", names);
                tiles.Add(tile);
            }
            return tiles;
        }

        /// <summary>
        /// The island tile ids that carry the open ring: an open group keys its
        /// one folded tile, an open standalone widget keys itself. A group
        /// member never contributes its own tile, so every open group appears
        /// exactly once regardless of how many members are in config order.
        /// </summary>
        public static List<string> OpenProjectedTileIds(
            IEnumerable<WidgetGroup> groups, IEnumerable<WidgetData> widgets, Func<string, bool> isHostOpen)
        {
            var open = new List<string>();
            if (groups == null || widgets == null || isHostOpen == null) return open;

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                if (group != null && !string.IsNullOrEmpty(group.Id)) groupIds.Add(group.Id);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in widgets)
            {
                if (widget == null || string.IsNullOrEmpty(widget.Id)) continue;

                bool grouped = !string.IsNullOrEmpty(widget.GroupId) && groupIds.Contains(widget.GroupId);
                string tileId;
                if (!grouped)
                {
                    if (!isHostOpen(widget.Id)) continue;
                    tileId = widget.Id;
                }
                else
                {
                    if (!isHostOpen(widget.GroupId)) continue;
                    tileId = widget.GroupId;
                }

                if (!string.IsNullOrEmpty(tileId) && seen.Add(tileId)) open.Add(tileId);
            }
            return open;
        }

        /// <summary>The member's display label; the localized kind is the fallback.</summary>
        private static string MemberLabel(WidgetData member)
        {
            if (member == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(member.Name)) return member.Name.Trim();

            switch (member.Kind)
            {
                case "todo": return Localization.Get("UI_WidgetKindTodo");
                case "note": return Localization.Get("UI_WidgetKindNote");
                case "search": return Localization.Get("UI_WidgetKindSearch");
                default: return Localization.Get("UI_WidgetKindFolder");
            }
        }

        /// <summary>
        /// The group's members in tab order: the stored order first, the config
        /// index breaking ties (and standing in for a missing order). An
        /// unknown/null group gives an empty list.
        /// </summary>
        public static List<WidgetData> MembersOf(WidgetGroup group, IEnumerable<WidgetData> widgets)
        {
            var members = new List<WidgetData>();
            if (group == null || string.IsNullOrEmpty(group.Id) || widgets == null) return members;

            var indexed = new List<KeyValuePair<int, WidgetData>>();
            int index = 0;
            foreach (var widget in widgets)
            {
                if (widget != null && string.Equals(widget.GroupId, group.Id, StringComparison.Ordinal))
                    indexed.Add(new KeyValuePair<int, WidgetData>(index, widget));
                index++;
            }

            indexed.Sort((a, b) =>
            {
                int left = a.Value.GroupOrder ?? int.MaxValue;
                int right = b.Value.GroupOrder ?? int.MaxValue;
                return left != right ? left.CompareTo(right) : a.Key.CompareTo(b.Key);
            });

            foreach (var entry in indexed) members.Add(entry.Value);
            return members;
        }

        /// <summary>The widget's tab index inside the group; -1 when it is not a member.</summary>
        public static int IndexOfMember(WidgetGroup group, IEnumerable<WidgetData> widgets, string widgetId)
        {
            if (string.IsNullOrEmpty(widgetId)) return -1;

            var members = MembersOf(group, widgets);
            for (int i = 0; i < members.Count; i++)
            {
                if (string.Equals(members[i].Id, widgetId, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>The group the widget belongs to, or null for an ungrouped/unknown widget.</summary>
        public static WidgetGroup FindGroupForWidget(
            IEnumerable<WidgetGroup> groups, IEnumerable<WidgetData> widgets, string widgetId)
        {
            if (groups == null || widgets == null || string.IsNullOrEmpty(widgetId)) return null;

            string groupId = null;
            foreach (var widget in widgets)
            {
                if (widget != null && string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
                {
                    groupId = widget.GroupId;
                    break;
                }
            }
            if (string.IsNullOrEmpty(groupId)) return null;

            foreach (var group in groups)
            {
                if (group != null && string.Equals(group.Id, groupId, StringComparison.Ordinal)) return group;
            }
            return null;
        }

        /// <summary>
        /// The tab the group shows: its stored active id while that is still a
        /// member, otherwise the first member (and null for an empty group).
        /// </summary>
        public static string ActiveMemberId(WidgetGroup group, IEnumerable<WidgetData> widgets)
        {
            var members = MembersOf(group, widgets);
            if (members.Count == 0) return null;

            if (!string.IsNullOrEmpty(group.ActiveWidgetId))
            {
                foreach (var member in members)
                {
                    if (string.Equals(member.Id, group.ActiveWidgetId, StringComparison.Ordinal))
                        return member.Id;
                }
            }
            return members[0].Id;
        }
    }
}
