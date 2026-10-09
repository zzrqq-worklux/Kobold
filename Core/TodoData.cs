using System;
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// Data for a todo widget: top-level items plus the day the list last
    /// showed. All dates carry date semantics only.
    /// </summary>
    public class TodoData : WidgetData
    {
        public List<TodoItem> Items { get; set; }
        public DateTime? SelectedDate { get; set; }

        /// <summary>
        /// The day-cell holiday region ("none"/"cn"/"jp"/"us"/"uk"). Plain
        /// JSON property; null or unknown values normalize to "cn" at use
        /// (see <see cref="PublicHolidays.NormalizeRegion"/>).
        /// </summary>
        public string HolidayRegion { get; set; }

        public TodoData()
        {
            Id = Guid.NewGuid().ToString();
            Color = UiTokens.DefaultFolderColor;
            Items = new List<TodoItem>();
            Kind = "todo";
        }
    }

    /// <summary>
    /// One top-level todo entry. <see cref="CompletedDate"/> is null while the
    /// item is open, otherwise the day it was ticked.
    /// </summary>
    public class TodoItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public DateTime? Date { get; set; }
        public bool Done { get; set; }
        public DateTime? CompletedDate { get; set; }
        public List<SubTodoItem> Children { get; set; }

        public TodoItem()
        {
            Children = new List<SubTodoItem>();
        }
    }

    /// <summary>
    /// One sub-todo under a top-level item. Deliberately has no child list:
    /// the one-level nesting rule is enforced by the type itself.
    /// </summary>
    public class SubTodoItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public bool Done { get; set; }
    }
}
