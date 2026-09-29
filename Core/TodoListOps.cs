using System;
using System.Collections.Generic;
using System.Linq;

namespace Kobold.Core
{
    /// <summary>
    /// The list operations behind the todo day view: appending and removing
    /// top-level entries and sub-todos, the completed-last display order, and
    /// the day filter. The one-level nesting rule comes from the data types;
    /// all dates stay date-only through <see cref="TodoRollover"/>.
    /// </summary>
    public static class TodoListOps
    {
        /// <summary>
        /// Appends a top-level entry (Enter-continuous order) and returns it.
        /// Empty text is kept; the caller decides whether to delete it.
        /// </summary>
        public static TodoItem AddItem(TodoData data, string text, DateTime? date)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Items == null) data.Items = new List<TodoItem>();

            var item = new TodoItem
            {
                Id = Guid.NewGuid().ToString(),
                Text = text ?? string.Empty,
                Date = date?.Date,
                Done = false,
                CompletedDate = null
            };
            data.Items.Add(item);
            return item;
        }

        /// <summary>
        /// Removes a top-level entry by id; its sub-todos go with it. Returns
        /// false (and changes nothing) when the id is unknown.
        /// </summary>
        public static bool RemoveItem(TodoData data, string itemId)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Items == null || string.IsNullOrEmpty(itemId)) return false;

            for (int i = 0; i < data.Items.Count; i++)
            {
                var item = data.Items[i];
                if (item != null && item.Id == itemId)
                {
                    data.Items.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Appends a sub-todo under its parent (Enter-continuous order) and
        /// returns it. A missing child list is filled in first.
        /// </summary>
        public static SubTodoItem AddChild(TodoItem parent, string text)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (parent.Children == null) parent.Children = new List<SubTodoItem>();

            var child = new SubTodoItem
            {
                Id = Guid.NewGuid().ToString(),
                Text = text ?? string.Empty,
                Done = false
            };
            parent.Children.Add(child);
            return child;
        }

        /// <summary>
        /// Removes one sub-todo by id. Returns false (and changes nothing)
        /// when the id is unknown.
        /// </summary>
        public static bool RemoveChild(TodoItem parent, string childId)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (parent.Children == null || string.IsNullOrEmpty(childId)) return false;

            for (int i = 0; i < parent.Children.Count; i++)
            {
                var child = parent.Children[i];
                if (child != null && child.Id == childId)
                {
                    parent.Children.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Display order: open entries in list order, then completed entries in
        /// list order (stable, so nothing but completion moves a row).
        /// </summary>
        public static List<TodoItem> OrderForDisplay(IEnumerable<TodoItem> items)
        {
            if (items == null) return new List<TodoItem>();
            return items.Where(i => i != null).OrderBy(i => i.Done).ToList();
        }

        /// <summary>
        /// The day list: top-level entries whose display date is the requested
        /// day, completed entries last. Sub-todos never appear as rows, so the
        /// resulting count counts top-level entries only.
        /// </summary>
        public static List<TodoItem> ForDay(IList<TodoItem> items, DateTime day, DateTime today)
        {
            DateTime wanted = day.Date;
            var dayItems = new List<TodoItem>();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null) continue;
                    if (TodoRollover.DisplayDate(item, today) == wanted) dayItems.Add(item);
                }
            }
            return OrderForDisplay(dayItems);
        }
    }
}
