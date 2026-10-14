using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// The carry and completion rules of the todo widget: which day an entry
    /// displays on, how long it has been carried, and what ticking does to the
    /// dates. Display is pure - only <see cref="Toggle"/> and the snapshot's
    /// <see cref="RestoreToggle"/> write to the data.
    /// Every DateTime parameter and result carries date semantics only.
    /// </summary>
    public static class TodoRollover
    {
        /// <summary>
        /// The day an entry shows on: a completed entry on its CompletedDate
        /// (falling back to its own date, then today, for hand-edited data),
        /// an open entry on max(Date, today); an entry without a Date shows
        /// today.
        /// </summary>
        public static DateTime DisplayDate(TodoItem item, DateTime today)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            DateTime day = today.Date;
            if (item.Done)
            {
                if (item.CompletedDate.HasValue) return item.CompletedDate.Value.Date;
                return item.Date.HasValue ? item.Date.Value.Date : day;
            }

            if (!item.Date.HasValue) return day;
            DateTime date = item.Date.Value.Date;
            return date < day ? day : date;
        }

        /// <summary>
        /// True when an open entry's original date lies before today, i.e. the
        /// entry is shown carried on today.
        /// </summary>
        public static bool IsCarried(TodoItem item, DateTime today)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            return !item.Done && item.Date.HasValue && item.Date.Value.Date < today.Date;
        }

        /// <summary>Days an entry has been carried; 0 when it is not carried.</summary>
        public static int CarriedDays(TodoItem item, DateTime today)
        {
            if (!IsCarried(item, today)) return 0;
            return (today.Date - item.Date.Value.Date).Days;
        }

        /// <summary>
        /// Ticks or unticks a top-level entry. Ticking stamps today as the
        /// completion date and cascades Done to every child; unticking clears
        /// the stamp and leaves the children at their own states.
        /// </summary>
        public static void Toggle(TodoItem item, bool done, DateTime today)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            item.Done = done;
            item.CompletedDate = done ? (DateTime?)today.Date : null;

            if (done && item.Children != null)
            {
                foreach (var child in item.Children) ToggleChild(child, true);
            }
        }

        /// <summary>
        /// Ticks or unticks one sub-todo. Sub-todos have no dates and never
        /// cascade, so this touches only the child itself.
        /// </summary>
        public static void ToggleChild(SubTodoItem child, bool done)
        {
            if (child == null) throw new ArgumentNullException(nameof(child));

            child.Done = done;
        }

        /// <summary>Captures a parent entry's pre-toggle state (stamp plus
        /// every child's done flag - the completion cascade may flip them all).</summary>
        public static TodoToggleSnapshot CaptureToggle(TodoItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            var snapshot = new TodoToggleSnapshot { Item = item, Done = item.Done, CompletedDate = item.CompletedDate };
            if (item.Children != null)
            {
                foreach (var child in item.Children)
                    if (child != null) snapshot.Children.Add(new TodoChildToggle { Child = child, Done = child.Done });
            }
            return snapshot;
        }

        /// <summary>Captures a single sub-todo's pre-toggle state.</summary>
        public static TodoToggleSnapshot CaptureChildToggle(SubTodoItem child)
        {
            if (child == null) throw new ArgumentNullException(nameof(child));
            return new TodoToggleSnapshot { Child = child, Done = child.Done };
        }

        /// <summary>Puts a captured toggle back exactly as it was.</summary>
        public static void RestoreToggle(TodoToggleSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (snapshot.Item != null)
            {
                snapshot.Item.Done = snapshot.Done;
                snapshot.Item.CompletedDate = snapshot.CompletedDate;
            }
            if (snapshot.Child != null) snapshot.Child.Done = snapshot.Done;
            foreach (var child in snapshot.Children) if (child.Child != null) child.Child.Done = child.Done;
        }
    }

    /// <summary>One sub-todo's done flag before a toggle (the parent
    /// cascade may touch every child).</summary>
    public sealed class TodoChildToggle
    {
        public SubTodoItem Child;
        public bool Done;
    }

    /// <summary>The pre-toggle state of one entry: a parent tick touches
    /// its stamp and may cascade to every child, a child tick only
    /// itself.</summary>
    public sealed class TodoToggleSnapshot
    {
        public TodoItem Item;            // the toggled parent (null for a child toggle)
        public SubTodoItem Child;        // the toggled sub-todo (null for a parent toggle)
        public bool Done;
        public DateTime? CompletedDate;  // the parent's stamp before the toggle
        public readonly List<TodoChildToggle> Children = new List<TodoChildToggle>();
    }
}
