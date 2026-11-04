using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// Pure startup rule for widget groups: a group is reopened only when it
    /// was expanded and still has at least two members. The result follows the
    /// first member's position in the widget list (config order).
    /// </summary>
    public static class GroupStartupPolicy
    {
        public static List<WidgetGroup> ToRestoreGroups(
            IEnumerable<WidgetGroup> groups, IEnumerable<WidgetData> widgets)
        {
            var restore = new List<WidgetGroup>();
            if (groups == null || widgets == null) return restore;

            var byId = new Dictionary<string, WidgetGroup>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                if (group == null || string.IsNullOrEmpty(group.Id)) continue;
                if (!byId.ContainsKey(group.Id)) byId.Add(group.Id, group);
            }
            if (byId.Count == 0) return restore;

            var members = new List<WidgetData>();
            foreach (var widget in widgets)
            {
                if (widget != null && widget.GroupId != null) members.Add(widget);
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var widget in members)
            {
                int count;
                counts.TryGetValue(widget.GroupId, out count);
                counts[widget.GroupId] = count + 1;
            }

            var added = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in members)
            {
                WidgetGroup group;
                if (!byId.TryGetValue(widget.GroupId, out group)) continue;
                if (!group.IsExpanded) continue;

                int count;
                if (!counts.TryGetValue(group.Id, out count) || count < 2) continue;

                // The first member in config order decides the restore order.
                if (added.Add(group.Id)) restore.Add(group);
            }

            return restore;
        }
    }
}
