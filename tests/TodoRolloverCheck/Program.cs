using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.TodoRolloverCheck
{
    /// <summary>
    /// Behaviour checks for TodoRollover - the pure carry/complete rules of
    /// the todo widget: overdue open entries display on today (without the
    /// data being touched), future and today entries stay put, completed
    /// entries show on their completion day, ticking a parent cascades Done
    /// to its children while unticking does not. Exit code 0 = all green;
    /// 1 = failures. Run: dotnet run --project tests/TodoRolloverCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static readonly DateTime Today = new DateTime(2026, 9, 28);

        private static int Main()
        {
            RunAll();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL TODO-ROLLOVER CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        private static void RunAll()
        {
            DisplayDates();
            CarryFlags();
            Toggling();
            ChildToggling();
            ToggleSnapshots();
        }

        private static void DisplayDates()
        {
            // Overdue open entries are pulled onto today; data stays as it was.
            var overdue = Item("overdue", "buy milk", new DateTime(2026, 9, 25));
            Check(TodoRollover.DisplayDate(overdue, Today) == new DateTime(2026, 9, 28),
                "an overdue open entry displays on today");
            Check(overdue.Date == new DateTime(2026, 9, 25) && !overdue.Done && overdue.CompletedDate == null,
                "displaying an overdue entry does not touch the data");

            var dueToday = Item("today", "due today", new DateTime(2026, 9, 28));
            Check(TodoRollover.DisplayDate(dueToday, Today) == new DateTime(2026, 9, 28),
                "an entry dated today displays on today");

            var future = Item("future", "next week", new DateTime(2026, 10, 2));
            Check(TodoRollover.DisplayDate(future, Today) == new DateTime(2026, 10, 2),
                "a future entry displays on its own date");

            var done = Item("done", "done", new DateTime(2026, 9, 25), true, new DateTime(2026, 9, 26));
            Check(TodoRollover.DisplayDate(done, Today) == new DateTime(2026, 9, 26),
                "a completed entry displays on its completion date");

            var undated = Item("undated", "no date", null);
            Check(TodoRollover.DisplayDate(undated, Today) == new DateTime(2026, 9, 28),
                "an open entry without a date displays on today");

            var doneNoStamp = Item("done-nostamp", "legacy done", new DateTime(2026, 9, 25), true);
            Check(TodoRollover.DisplayDate(doneNoStamp, Today) == new DateTime(2026, 9, 25),
                "a completed entry without a completion date falls back to its own date");

            var doneNoDates = Item("done-nodates", "legacy done", null, true);
            Check(TodoRollover.DisplayDate(doneNoDates, Today) == new DateTime(2026, 9, 28),
                "a completed entry without any date falls back to today");

            var withTime = Item("with-time", "stamped", new DateTime(2026, 10, 2, 13, 45, 0));
            Check(TodoRollover.DisplayDate(withTime, Today) == new DateTime(2026, 10, 2),
                "a display date drops the time of the stored date");

            Check(TodoRollover.DisplayDate(overdue, new DateTime(2026, 9, 28, 17, 45, 0)) == new DateTime(2026, 9, 28),
                "a display date drops the time of today");
        }

        private static void CarryFlags()
        {
            var overdue = Item("overdue", "buy milk", new DateTime(2026, 9, 25));
            Check(TodoRollover.IsCarried(overdue, Today), "a 3-day overdue entry is carried");
            Check(TodoRollover.CarriedDays(overdue, Today) == 3, "a 3-day overdue entry says 3 carried days");

            var yesterday = Item("yesterday", "yesterday", new DateTime(2026, 9, 27));
            Check(TodoRollover.IsCarried(yesterday, Today) && TodoRollover.CarriedDays(yesterday, Today) == 1,
                "a 1-day overdue entry is carried 1 day");

            var dueToday = Item("today", "due today", new DateTime(2026, 9, 28));
            Check(!TodoRollover.IsCarried(dueToday, Today) && TodoRollover.CarriedDays(dueToday, Today) == 0,
                "an entry dated today is not carried");

            var future = Item("future", "next week", new DateTime(2026, 10, 2));
            Check(!TodoRollover.IsCarried(future, Today) && TodoRollover.CarriedDays(future, Today) == 0,
                "a future entry is not carried");

            var done = Item("done", "done", new DateTime(2026, 9, 25), true, new DateTime(2026, 9, 26));
            Check(!TodoRollover.IsCarried(done, Today) && TodoRollover.CarriedDays(done, Today) == 0,
                "a completed overdue entry is not carried");

            var undated = Item("undated", "no date", null);
            Check(!TodoRollover.IsCarried(undated, Today) && TodoRollover.CarriedDays(undated, Today) == 0,
                "an entry without a date is not carried");

            DateTime original = overdue.Date.Value;
            bool wasDone = overdue.Done;
            TodoRollover.IsCarried(overdue, Today);
            TodoRollover.CarriedDays(overdue, Today);
            Check(overdue.Date.Value == original && overdue.Done == wasDone,
                "carry checks leave the entry untouched");
        }

        private static void Toggling()
        {
            var parent = Item("p1", "parent", new DateTime(2026, 9, 25), false, null,
                new List<SubTodoItem> { Child("c1", "one", true), Child("c2", "two") });

            TodoRollover.Toggle(parent, true, Today);
            Check(parent.Done, "ticking a parent marks it done");
            Check(parent.CompletedDate == new DateTime(2026, 9, 28),
                "ticking a parent stamps today as its completion date");
            Check(parent.Children[0].Done && parent.Children[1].Done,
                "ticking a parent cascades Done to every child");
            Check(TodoRollover.DisplayDate(parent, Today) == new DateTime(2026, 9, 28),
                "a ticked parent displays on its completion day");

            TodoRollover.ToggleChild(parent.Children[1], false);
            Check(parent.Children[0].Done && !parent.Children[1].Done,
                "a child toggle sticks after the cascade");

            TodoRollover.Toggle(parent, false, Today);
            Check(!parent.Done && parent.CompletedDate == null,
                "unticking a parent clears its completion stamp");
            Check(parent.Children[0].Done && !parent.Children[1].Done,
                "unticking a parent keeps each child's own state");
            Check(TodoRollover.DisplayDate(parent, Today) == new DateTime(2026, 9, 28),
                "an unticked overdue parent goes back to being carried on today");

            var future = Item("p2", "future", new DateTime(2026, 10, 2));
            TodoRollover.Toggle(future, true, Today);
            Check(TodoRollover.DisplayDate(future, Today) == new DateTime(2026, 9, 28),
                "a ticked future entry displays on its completion day");
            TodoRollover.Toggle(future, false, Today);
            Check(TodoRollover.DisplayDate(future, Today) == new DateTime(2026, 10, 2),
                "an unticked future entry goes back to its own date");

            var stamped = Item("p3", "stamped", null);
            TodoRollover.Toggle(stamped, true, new DateTime(2026, 9, 28, 17, 45, 0));
            Check(stamped.CompletedDate == new DateTime(2026, 9, 28),
                "the completion stamp keeps only the day");

            var noChildren = Item("p4", "no children", new DateTime(2026, 9, 28));
            noChildren.Children = null;
            TodoRollover.Toggle(noChildren, true, Today);
            Check(noChildren.Done && noChildren.CompletedDate == new DateTime(2026, 9, 28),
                "ticking a parent with a null child list still works");
        }

        private static void ChildToggling()
        {
            var parent = Item("p1", "parent", new DateTime(2026, 9, 25), false, null,
                new List<SubTodoItem> { Child("c1", "one"), Child("c2", "two") });
            var child = parent.Children[0];

            TodoRollover.ToggleChild(child, true);
            Check(child.Done, "toggling a child marks it done");
            Check(!parent.Done && parent.CompletedDate == null,
                "toggling a child leaves the parent open");
            Check(!parent.Children[1].Done, "toggling a child leaves its siblings alone");
            Check(child.Id == "c1" && child.Text == "one", "toggling a child keeps its id and text");

            TodoRollover.ToggleChild(child, false);
            Check(!child.Done && !parent.Done, "unticking a child clears just the child");

            var doneParent = Item("p2", "done parent", new DateTime(2026, 9, 25), true,
                new DateTime(2026, 9, 26), new List<SubTodoItem> { Child("c1", "one") });
            TodoRollover.ToggleChild(doneParent.Children[0], true);
            Check(doneParent.Done && doneParent.CompletedDate == new DateTime(2026, 9, 26),
                "toggling a child leaves its parent's completion alone");
        }

        private static void ToggleSnapshots()
        {
            // Capture: a parent snapshot holds its stamp and every child's flag.
            var parent = Item("p1", "parent", new DateTime(2026, 9, 25), true, new DateTime(2026, 9, 26),
                new List<SubTodoItem> { Child("c1", "one", true), Child("c2", "two") });
            var snapshot = TodoRollover.CaptureToggle(parent);
            Check(snapshot.Item == parent && snapshot.Child == null && snapshot.Done &&
                  snapshot.CompletedDate == new DateTime(2026, 9, 26) &&
                  snapshot.Children.Count == 2 &&
                  snapshot.Children[0].Child == parent.Children[0] && snapshot.Children[0].Done &&
                  snapshot.Children[1].Child == parent.Children[1] && !snapshot.Children[1].Done,
                "toggle capture: a parent snapshot keeps its stamp and every child's flag");

            var childSnapshot = TodoRollover.CaptureChildToggle(parent.Children[1]);
            Check(childSnapshot.Child == parent.Children[1] && childSnapshot.Item == null &&
                  !childSnapshot.Done && childSnapshot.CompletedDate == null &&
                  childSnapshot.Children.Count == 0,
                "toggle capture: a child snapshot keeps only the child");

            // The cascade is undone exactly - flags and stamp both come back.
            var cascaded = Item("p2", "parent", new DateTime(2026, 9, 25), false, null,
                new List<SubTodoItem> { Child("c1", "one", true), Child("c2", "two") });
            var beforeTick = TodoRollover.CaptureToggle(cascaded);
            TodoRollover.Toggle(cascaded, true, Today);
            TodoRollover.RestoreToggle(beforeTick);
            Check(!cascaded.Done && cascaded.CompletedDate == null &&
                  cascaded.Children[0].Done && !cascaded.Children[1].Done,
                "restore: a cascaded parent goes back exactly (flags + stamp)");

            // Unticking never touches the children, so the restore puts only the
            // parent's own state back.
            var doneParent = Item("p3", "done parent", new DateTime(2026, 9, 25), true, new DateTime(2026, 9, 26),
                new List<SubTodoItem> { Child("c1", "one", true), Child("c2", "two") });
            var beforeUntick = TodoRollover.CaptureToggle(doneParent);
            TodoRollover.Toggle(doneParent, false, Today);
            TodoRollover.RestoreToggle(beforeUntick);
            Check(doneParent.Done && doneParent.CompletedDate == new DateTime(2026, 9, 26) &&
                  doneParent.Children[0].Done && !doneParent.Children[1].Done,
                "restore: an untick keeps the children as captured");

            var single = Item("p4", "parent", new DateTime(2026, 9, 28), false, null,
                new List<SubTodoItem> { Child("c1", "one") });
            var beforeChild = TodoRollover.CaptureChildToggle(single.Children[0]);
            TodoRollover.ToggleChild(single.Children[0], true);
            TodoRollover.RestoreToggle(beforeChild);
            Check(!single.Children[0].Done && !single.Done,
                "restore: a child toggle goes back");

            var noChildren = Item("p5", "no children", new DateTime(2026, 9, 28));
            noChildren.Children = null;
            TodoToggleSnapshot nullSnapshot = null;
            bool threw = false;
            try { nullSnapshot = TodoRollover.CaptureToggle(noChildren); }
            catch (Exception) { threw = true; }
            Check(!threw && nullSnapshot != null && nullSnapshot.Children.Count == 0,
                "capture: a null child list captures empty (no throw)");
        }

        private static TodoItem Item(string id, string text, DateTime? date,
            bool done = false, DateTime? completed = null, List<SubTodoItem> children = null)
        {
            return new TodoItem
            {
                Id = id,
                Text = text,
                Date = date,
                Done = done,
                CompletedDate = completed,
                Children = children ?? new List<SubTodoItem>()
            };
        }

        private static SubTodoItem Child(string id, string text, bool done = false)
        {
            return new SubTodoItem { Id = id, Text = text, Done = done };
        }
    }
}
