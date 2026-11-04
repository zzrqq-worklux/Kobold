using System;
using System.Collections.Generic;
using System.Windows;
using Kobold.Controls;

namespace Kobold.Core
{
    /// <summary>
    /// The one group fact the drag policy needs from a group host, so the
    /// policy itself stays a pure data rule: <see cref="Controls.WidgetGroupWindow"/>
    /// is the only production implementation, and a check can stand in for it.
    /// </summary>
    public interface IGroupDragHost
    {
        /// <summary>True while the group refuses merges/detaches.</summary>
        bool IsGroupLocked { get; }
    }

    /// <summary>
    /// The pure merge/tab-drag rules shared by the group window, the widget
    /// windows and the manager: the drag-out threshold, the last-pair
    /// dissolve, the append index, the merge guards and the screen hit test.
    /// No window or config is touched here, which keeps the state machine
    /// testable without a running app.
    /// </summary>
    public static class GroupDragPolicy
    {
        /// <summary>Screen movement (DIP) that turns a press into a drag.</summary>
        public const double DefaultDragThreshold = 12;

        /// <summary>
        /// True when the press moved far enough on either axis to count as a
        /// drag-out. A negative delta counts by its magnitude; the threshold
        /// is inclusive.
        /// </summary>
        public static bool IsDragOut(double dx, double dy, double threshold = DefaultDragThreshold)
        {
            double limit = threshold > 0 ? threshold : DefaultDragThreshold;
            return Math.Abs(dx) >= limit || Math.Abs(dy) >= limit;
        }

        /// <summary>A group with fewer than two tabs is no group at all.</summary>
        public static bool ShouldDissolve(int memberCount) => memberCount < 2;

        /// <summary>
        /// The tab index a merged source takes: after the last target tab.
        /// A negative/empty target clamps to the first slot.
        /// </summary>
        public static int InsertIndex(int targetMemberCount)
        {
            return targetMemberCount > 0 ? targetMemberCount : 0;
        }

        /// <summary>
        /// Whether a header drag from <paramref name="source"/> may merge into
        /// <paramref name="target"/>: never for null/same, never from a group,
        /// never with a locked source/target or a locked target group, and
        /// only for the tab-able kinds (Folder is deferred to T3/Ruling 7).
        /// </summary>
        public static bool CanMerge(IWidgetWindow source, IWidgetWindow target)
        {
            if (source == null || target == null) return false;
            if (ReferenceEquals(source, target)) return false;
            if (source is IGroupDragHost) return false; // no group -> group

            WidgetData sourceData = source.Data;
            WidgetData targetData = target.Data;
            if (!IsMergeableKind(sourceData) || !IsMergeableKind(targetData)) return false;
            if (sourceData.IsLocked || targetData.IsLocked) return false;

            var targetGroup = target as IGroupDragHost;
            if (targetGroup != null && targetGroup.IsGroupLocked) return false;

            return true;
        }

        /// <summary>
        /// The first candidate whose supplied screen rectangle contains the
        /// point, skipping the source. Null candidates, a null/throwing bounds
        /// callback and empty rectangles are ignored.
        /// </summary>
        public static IWidgetWindow FindTarget(Point point, IWidgetWindow source,
            IEnumerable<IWidgetWindow> candidates, Func<IWidgetWindow, Rect> bounds)
        {
            if (candidates == null || bounds == null) return null;

            foreach (var candidate in candidates)
            {
                if (candidate == null || ReferenceEquals(candidate, source)) continue;

                Rect rect;
                try
                {
                    rect = bounds(candidate);
                }
                catch (Exception)
                {
                    continue;
                }

                if (rect.IsEmpty) continue;
                if (rect.Contains(point)) return candidate;
            }
            return null;
        }

        /// <summary>The kinds a group may host; Folder joins after T3.</summary>
        private static bool IsMergeableKind(WidgetData data)
        {
            return data is TodoData || data is NoteData || data is SearchData;
        }
    }
}
