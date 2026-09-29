using System;

namespace Kobold.Core
{
    /// <summary>
    /// The carry and completion rules of the todo widget: which day an entry
    /// displays on, how long it has been carried, and what ticking does to the
    /// dates. Display is pure - only <see cref="Toggle"/> writes to the data.
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
    }
}
