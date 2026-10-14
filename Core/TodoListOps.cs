using System;
using System.Collections.Generic;
using System.Linq;

namespace Kobold.Core
{
    /// <summary>One removed top-level entry and where it sat.</summary>
    public sealed class TodoParentRemoval
    {
        public TodoItem Item;
        public int Index;
    }

    /// <summary>One removed sub-todo and where it sat under its parent.</summary>
    public sealed class TodoChildRemoval
    {
        public TodoItem Parent;
        public SubTodoItem Child;
        public int Index;
    }

    /// <summary>A deletion (one entry, one sub-todo, or a day's clear) kept in
    /// the shape <see cref="TodoListOps.RestoreRemoval"/> can undo.</summary>
    public sealed class TodoRemoval
    {
        public readonly List<TodoParentRemoval> Parents = new List<TodoParentRemoval>();
        public readonly List<TodoChildRemoval> Children = new List<TodoChildRemoval>();
        public bool IsEmpty => Parents.Count == 0 && Children.Count == 0;
    }

    /// <summary>One undoable structural edit: exactly one of the two payloads
    /// is set. The stack lives in the view; the payloads are the pure cores.</summary>
    public sealed class TodoUndoEntry
    {
        public readonly TodoRemoval Removal;
        public readonly TodoToggleSnapshot Toggle;

        public TodoUndoEntry(TodoRemoval removal) { Removal = removal; }
        public TodoUndoEntry(TodoToggleSnapshot toggle) { Toggle = toggle; }
    }

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

        /// <summary>Removes a top-level entry and records where it sat; an item that
        /// is not in the list yields an empty removal and changes nothing.</summary>
        public static TodoRemoval RemoveParent(IList<TodoItem> items, TodoItem item)
        {
            var removal = new TodoRemoval();
            if (items == null || item == null) return removal;
            int index = items.IndexOf(item);
            if (index < 0) return removal;
            items.RemoveAt(index);
            removal.Parents.Add(new TodoParentRemoval { Item = item, Index = index });
            return removal;
        }

        /// <summary>Removes one sub-todo and records its position under the parent.</summary>
        public static TodoRemoval RemoveChildItem(TodoItem parent, SubTodoItem child)
        {
            var removal = new TodoRemoval();
            if (parent == null || parent.Children == null || child == null) return removal;
            int index = parent.Children.IndexOf(child);
            if (index < 0) return removal;
            parent.Children.RemoveAt(index);
            removal.Children.Add(new TodoChildRemoval { Parent = parent, Child = child, Index = index });
            return removal;
        }

        /// <summary>The day-scoped clear: every completed row on the day list. A
        /// completed top-level entry goes whole (sub-todos ride along); under the
        /// day's open entries, completed sub-todos go individually.</summary>
        public static TodoRemoval RemoveCompletedOn(IList<TodoItem> items, DateTime day, DateTime today)
        {
            var removal = new TodoRemoval();
            if (items == null) return removal;

            var dayList = ForDay(items, day, today);
            var parents = new List<TodoItem>();
            var children = new List<KeyValuePair<TodoItem, SubTodoItem>>();
            foreach (var item in dayList)
            {
                if (item.Done) { parents.Add(item); continue; }
                if (item.Children == null) continue;
                foreach (var child in item.Children)
                    if (child != null && child.Done) children.Add(new KeyValuePair<TodoItem, SubTodoItem>(item, child));
            }

            foreach (var item in parents)
            {
                int index = items.IndexOf(item);
                if (index >= 0) removal.Parents.Add(new TodoParentRemoval { Item = item, Index = index });
            }
            foreach (var entry in removal.Parents.OrderByDescending(e => e.Index)) items.RemoveAt(entry.Index);

            foreach (var pair in children)
            {
                int index = pair.Key.Children.IndexOf(pair.Value);
                if (index >= 0) removal.Children.Add(new TodoChildRemoval { Parent = pair.Key, Child = pair.Value, Index = index });
            }
            foreach (var entry in removal.Children.OrderByDescending(e => e.Index))
                entry.Parent.Children.RemoveAt(entry.Index);

            return removal;
        }

        /// <summary>True when the day's clear would remove something (the menu gate).</summary>
        public static bool HasCompletedOn(IList<TodoItem> items, DateTime day, DateTime today)
        {
            if (items == null) return false;
            foreach (var item in ForDay(items, day, today))
            {
                if (item.Done) return true;
                if (item.Children == null) continue;
                foreach (var child in item.Children)
                    if (child != null && child.Done) return true;
            }
            return false;
        }

        /// <summary>Puts a removal back: ascending inserts at the recorded indices
        /// reproduce the original order; missing slots are clamped to the ends.</summary>
        public static void RestoreRemoval(TodoRemoval removal, IList<TodoItem> items)
        {
            if (removal == null || items == null) return;
            foreach (var entry in removal.Parents.OrderBy(e => e.Index))
                items.Insert(Math.Min(entry.Index, items.Count), entry.Item);
            foreach (var entry in removal.Children.OrderBy(e => e.Index))
            {
                if (entry.Parent == null) continue;
                if (entry.Parent.Children == null) entry.Parent.Children = new List<SubTodoItem>();
                entry.Parent.Children.Insert(Math.Min(entry.Index, entry.Parent.Children.Count), entry.Child);
            }
        }
    }
}
