using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// The island's per-member group projection: the count the tile badge shows
    /// and the member names its tooltip adds. Built by the manager and handed
    /// to <see cref="Controls.IslandWindow"/>; the island never reads the group
    /// model itself.
    /// </summary>
    public sealed class WidgetGroupTileInfo
    {
        public int MemberCount { get; set; }
        public string MemberNames { get; set; }
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

        /// <summary>
        /// The island tile ids that carry the open ring: an open group marks
        /// only its active member, an open standalone widget marks itself.
        /// Closed hosts contribute nothing; duplicates collapse to the first
        /// hit in config order.
        /// </summary>
        public static List<string> OpenTileIds(
            IEnumerable<WidgetGroup> groups, IEnumerable<WidgetData> widgets, Func<string, bool> isHostOpen)
        {
            var open = new List<string>();
            if (widgets == null || isHostOpen == null) return open;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in widgets)
            {
                if (widget == null || string.IsNullOrEmpty(widget.Id)) continue;

                WidgetGroup group = FindGroupForWidget(groups, widgets, widget.Id);
                string tileId;
                if (group == null)
                {
                    if (!isHostOpen(widget.Id)) continue;
                    tileId = widget.Id;
                }
                else
                {
                    if (!isHostOpen(group.Id)) continue;
                    tileId = ActiveMemberId(group, widgets);
                }

                if (!string.IsNullOrEmpty(tileId) && seen.Add(tileId)) open.Add(tileId);
            }
            return open;
        }
    }
}
