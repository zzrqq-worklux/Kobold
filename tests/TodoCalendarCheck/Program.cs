using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kobold.Core;

namespace Kobold.TodoCalendarCheck
{
    /// <summary>
    /// Behaviour checks for TodoCalendar and TodoListOps: the month grid
    /// (full weeks, unique cells, the caller's FirstDayOfWeek per culture,
    /// cross-year navigation, today/selected/out-of-month markers, top-level
    /// counts) and the list operations (append order, completed-last stable
    /// order, the day filter, removal together with sub-todos). Exit code
    /// 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/TodoCalendarCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static readonly DateTime Today = new DateTime(2026, 9, 28, 17, 45, 0);
        private static readonly DateTime TodayDate = new DateTime(2026, 9, 28);

        private static int Main()
        {
            RunAll();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL TODO-CALENDAR CHECKS PASSED");
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
            CalendarGrid();
            GridSweep();
            CrossYearNavigation();
            CultureGrids();
            ListAdds();
            ListRemoves();
            DayFilter();
            DisplayOrder();
            RemovalSnapshots();
            ChildRemovalSnapshots();
            DayClear();
            WeekCounts();
            CultureLookups();
        }

        private static void WeekCounts()
        {
            Check(TodoCalendar.WeekCount(2026, 2, DayOfWeek.Sunday) == 4,
                "February 2026 (Sunday first) is four weeks");
            Check(TodoCalendar.WeekCount(2026, 2, DayOfWeek.Monday) == 5,
                "February 2026 (Monday first) is five weeks");
            Check(TodoCalendar.WeekCount(2026, 10, DayOfWeek.Sunday) == 5,
                "October 2026 (Sunday first) is five weeks");
            Check(TodoCalendar.WeekCount(2026, 8, DayOfWeek.Sunday) == 6,
                "August 2026 (Sunday first) is six weeks");

            var cells = TodoCalendar.BuildMonth(2026, 8, new List<TodoItem>(), null, Today, DayOfWeek.Sunday);
            Check(cells.Count == TodoCalendar.WeekCount(2026, 8, DayOfWeek.Sunday) * 7,
                "BuildMonth derives its cell count from WeekCount (single source)");
        }

        private static void CultureLookups()
        {
            Check(TodoCalendar.CultureForLanguage("zh").Name == "zh-CN", "zh picks zh-CN");
            Check(TodoCalendar.CultureForLanguage("ja").Name == "ja-JP", "ja picks ja-JP");
            Check(TodoCalendar.CultureForLanguage("en").Name == "en-US", "en picks en-US");
            Check(TodoCalendar.CultureForLanguage(null).Name == "en-US", "a null language picks en-US");
            Check(TodoCalendar.CultureForLanguage("xx").Name == "en-US", "an unknown language picks en-US");
        }

        private static void CalendarGrid()
        {
            var items = MonthFixture();
            var selected = new DateTime(2026, 9, 25, 10, 30, 0);
            var cells = TodoCalendar.BuildMonth(2026, 9, items, selected, Today, DayOfWeek.Sunday);

            Check(cells != null && cells.Count % 7 == 0, "the grid is whole weeks");
            Check(cells != null && cells.Count / 7 >= 4 && cells.Count / 7 <= 6,
                "the grid has 4-6 weeks");
            Check(cells != null && cells.Count == 35, "September 2026 (Sunday first) is five weeks");

            if (cells == null || cells.Count == 0)
            {
                Check(false, "the grid is not empty");
                return;
            }

            Check(cells.All(c => c != null), "every cell is present");
            Check(cells.Select(c => c.Date).Distinct().Count() == cells.Count,
                "every cell has a unique date");

            bool continuous = true;
            for (int i = 1; i < cells.Count; i++)
            {
                if (cells[i].Date != cells[i - 1].Date.AddDays(1)) { continuous = false; break; }
            }
            Check(continuous, "the grid is a continuous run of days");

            Check(cells[0].Date == new DateTime(2026, 8, 30) && cells[0].Date.DayOfWeek == DayOfWeek.Sunday,
                "the first cell is the Sunday of the week holding September 1");
            Check(cells[0].Date <= new DateTime(2026, 9, 1)
                  && (new DateTime(2026, 9, 1) - cells[0].Date).Days < 7,
                "the first cell is less than a week before the 1st");
            Check(cells[cells.Count - 1].Date.DayOfWeek == DayOfWeek.Saturday,
                "the last cell closes the week");
            Check(cells[cells.Count - 1].Date >= new DateTime(2026, 9, 30),
                "the grid covers the whole month");

            var inMonth = cells.Where(c => c.InMonth).ToList();
            Check(inMonth.Count == 30, "InMonth marks exactly the 30 days of September");
            Check(inMonth.All(c => c.Date.Year == 2026 && c.Date.Month == 9),
                "every InMonth cell is a September day");
            Check(inMonth[0].Date.Day == 1 && inMonth[inMonth.Count - 1].Date.Day == 30,
                "September 1 and 30 are InMonth");
            Check(cells.Where(c => !c.InMonth).All(c => c.Date.Month != 9 || c.Date.Year != 2026),
                "out-of-month cells are the padding days");
            Check(Enumerable.Range(1, 30).All(day => cells.Count(c => c.Date == new DateTime(2026, 9, day)) == 1),
                "every day of September appears exactly once");

            var todayCells = cells.Where(c => c.IsToday).ToList();
            Check(todayCells.Count == 1 && todayCells[0].Date == TodayDate,
                "IsToday marks today only");
            var selectedCells = cells.Where(c => c.IsSelected).ToList();
            Check(selectedCells.Count == 1 && selectedCells[0].Date == selected.Date,
                "IsSelected marks the selected date only");
            var noSelection = TodoCalendar.BuildMonth(2026, 9, items, null, Today, DayOfWeek.Sunday);
            Check(noSelection.All(c => !c.IsSelected), "no selected date means no selected cell");

            Check(CountOn(cells, new DateTime(2026, 9, 28)) == 3,
                "September 28 counts the carried, today and undated entries");
            Check(CountOn(cells, new DateTime(2026, 9, 26)) == 1,
                "the completed entry counts on its completion day");
            Check(CountOn(cells, new DateTime(2026, 9, 27)) == 1,
                "the entry completed yesterday counts on its own day");
            Check(CountOn(cells, new DateTime(2026, 9, 29)) == 1,
                "a parent with three sub-todos counts once");
            Check(CountOn(cells, new DateTime(2026, 10, 2)) == 1,
                "the future entry counts on its own day");
            Check(CountOn(cells, new DateTime(2026, 9, 25)) == 0,
                "the carried entry's original day is empty");
            Check(cells.Sum(c => c.TopLevelCount) == items.Count,
                "markers count every top-level entry exactly once");

            var mondayCells = TodoCalendar.BuildMonth(2026, 9, items, selected, Today, DayOfWeek.Monday);
            Check(mondayCells[0].Date == new DateTime(2026, 8, 31)
                  && mondayCells[0].Date.DayOfWeek == DayOfWeek.Monday,
                "a Monday-first grid starts on the Monday of the week holding the 1st");
            Check(mondayCells.Count == 35, "the Monday-first September grid is also five weeks");
        }

        private static void GridSweep()
        {
            var sweepToday = new DateTime(2024, 1, 15);
            var firstDays = new[] { DayOfWeek.Sunday, DayOfWeek.Monday };
            int grids = 0;
            bool weeksOk = true, firstOk = true, uniqueOk = true, coverageOk = true, emptyOk = true;

            foreach (var firstDay in firstDays)
            {
                for (int year = 2025; year <= 2028; year++)
                {
                    for (int month = 1; month <= 12; month++)
                    {
                        var cells = TodoCalendar.BuildMonth(year, month, new List<TodoItem>(), null,
                            sweepToday, firstDay);
                        grids++;

                        int weeks = cells.Count / 7;
                        if (cells.Count % 7 != 0 || weeks < 4 || weeks > 6) weeksOk = false;
                        if (cells[0].Date.DayOfWeek != firstDay) firstOk = false;
                        if (cells.Select(c => c.Date).Distinct().Count() != cells.Count) uniqueOk = false;

                        int daysInMonth = DateTime.DaysInMonth(year, month);
                        var inMonth = cells.Where(c => c.InMonth).ToList();
                        if (inMonth.Count != daysInMonth) coverageOk = false;
                        if (inMonth[0].Date.Day != 1 || inMonth[inMonth.Count - 1].Date.Day != daysInMonth)
                            coverageOk = false;
                        if (cells.Any(c => c.IsToday || c.IsSelected || c.TopLevelCount != 0)) emptyOk = false;
                    }
                }
            }

            Check(grids == 96, "the sweep built 96 month grids");
            Check(weeksOk, "every swept month is 4-6 full weeks");
            Check(firstOk, "every swept grid starts on the requested first day of week");
            Check(uniqueOk, "every swept grid has unique cells");
            Check(coverageOk, "every swept grid covers exactly its month");
            Check(emptyOk, "an empty data set marks no cell");

            var febMonday = TodoCalendar.BuildMonth(2027, 2, new List<TodoItem>(), null, Today, DayOfWeek.Monday);
            Check(febMonday.Count == 28, "February 2027 (Monday first, 28 days) is exactly four weeks");
            var febSunday = TodoCalendar.BuildMonth(2027, 2, new List<TodoItem>(), null, Today, DayOfWeek.Sunday);
            Check(febSunday.Count == 35, "February 2027 (Sunday first) needs a fifth week");
            var augSunday = TodoCalendar.BuildMonth(2026, 8, new List<TodoItem>(), null, Today, DayOfWeek.Sunday);
            Check(augSunday.Count == 42, "August 2026 (Sunday first) needs six weeks");
        }

        private static void CrossYearNavigation()
        {
            Check(TodoCalendar.ShiftMonth(2026, 12, 1) == (2027, 1),
                "December + 1 rolls into January of the next year");
            Check(TodoCalendar.ShiftMonth(2027, 1, -1) == (2026, 12),
                "January - 1 rolls back into December");
            Check(TodoCalendar.ShiftMonth(2026, 1, -1) == (2025, 12),
                "January - 1 crosses into the previous year");
            Check(TodoCalendar.ShiftMonth(2026, 9, -1) == (2026, 8), "September - 1 is August");
            Check(TodoCalendar.ShiftMonth(2026, 9, 1) == (2026, 10), "September + 1 is October");
            Check(TodoCalendar.ShiftMonth(2026, 9, 0) == (2026, 9), "a zero shift keeps the month");
            Check(TodoCalendar.ShiftMonth(2025, 12, 13) == (2027, 1),
                "a shift larger than a year rolls correctly");

            var december = TodoCalendar.BuildMonth(2026, 12, new List<TodoItem>(), null, Today,
                DayOfWeek.Sunday);
            Check(december.Any(c => c.Date == new DateTime(2027, 1, 1) && !c.InMonth),
                "the December grid reaches into January of the next year");
            Check(december.Any(c => c.Date == new DateTime(2026, 12, 31) && c.InMonth),
                "December 31 is marked InMonth");

            var january = TodoCalendar.BuildMonth(2027, 1, new List<TodoItem>(), null, Today,
                DayOfWeek.Monday);
            Check(january[0].Date == new DateTime(2026, 12, 28) && !january[0].InMonth,
                "a January grid can start in the previous December");
            Check(january.Any(c => c.Date == new DateTime(2027, 1, 1) && c.InMonth),
                "January 1 is marked InMonth");
            Check(january.Count == 35, "January 2027 (Monday first) is five weeks");
        }

        private static void CultureGrids()
        {
            var julyFirst = new DateTime(2026, 7, 1);
            foreach (var name in new[] { "en-US", "zh-CN", "ja-JP" })
            {
                var culture = new CultureInfo(name);
                var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
                var cells = TodoCalendar.BuildMonth(2026, 9, new List<TodoItem>(), null, Today, firstDay);
                int lead = ((int)new DateTime(2026, 9, 1).DayOfWeek - (int)firstDay + 7) % 7;

                Check(cells[0].Date == new DateTime(2026, 9, 1).AddDays(-lead),
                    name + " grid starts " + lead + " day(s) before September 1");
                Check(cells[0].Date.DayOfWeek == firstDay,
                    name + " grid starts on its FirstDayOfWeek (" + firstDay + ")");
                Check(cells.Count(c => c.InMonth) == 30, name + " grid still covers all of September");
                Check(cells.Select(c => c.Date).Distinct().Count() == cells.Count,
                    name + " grid has unique cells");

                var july = TodoCalendar.BuildMonth(2026, 7, new List<TodoItem>(), null, Today, firstDay);
                int julyLead = ((int)julyFirst.DayOfWeek - (int)firstDay + 7) % 7;
                Check(july[0].Date == julyFirst.AddDays(-julyLead),
                    name + " July grid honors the same FirstDayOfWeek");
            }
        }

        private static void ListAdds()
        {
            var data = new TodoData();
            var first = TodoListOps.AddItem(data, "one", new DateTime(2026, 9, 28));
            var second = TodoListOps.AddItem(data, "two", null);
            var third = TodoListOps.AddItem(data, "three", new DateTime(2026, 10, 2, 13, 45, 0));

            Check(data.Items.Count == 3, "adding three entries keeps three entries");
            Check(first == data.Items[0] && second == data.Items[1] && third == data.Items[2],
                "entries append in the order they were added");
            Check(data.Items.Select(i => i.Text).SequenceEqual(new[] { "one", "two", "three" }),
                "the appended texts keep their order");
            Check(data.Items.All(i => !string.IsNullOrEmpty(i.Id))
                  && data.Items.Select(i => i.Id).Distinct().Count() == 3,
                "every appended entry gets its own id");
            Check(data.Items.All(i => !i.Done && i.CompletedDate == null),
                "appended entries start open and unstamped");
            Check(first.Date == new DateTime(2026, 9, 28) && second.Date == null
                  && third.Date == new DateTime(2026, 10, 2),
                "an appended date keeps only the day");

            var child = TodoListOps.AddChild(first, "sub");
            var secondChild = TodoListOps.AddChild(first, "sub two");
            Check(first.Children.Count == 2 && first.Children[0] == child && first.Children[1] == secondChild,
                "sub-todos append under their parent");
            Check(!string.IsNullOrEmpty(child.Id) && child.Id != secondChild.Id,
                "every appended sub-todo gets its own id");
            Check(!child.Done, "an appended sub-todo starts open");
            Check(data.Items[1].Children.Count == 0, "adding a sub-todo touches only its parent");

            first.Children = null;
            var reopened = TodoListOps.AddChild(first, "after repair");
            Check(first.Children != null && first.Children.Count == 1 && first.Children[0] == reopened,
                "adding a sub-todo to a null child list repairs the list");

            var empty = new TodoData();
            empty.Items = null;
            var firstOfEmpty = TodoListOps.AddItem(empty, "first", null);
            Check(empty.Items != null && empty.Items.Count == 1 && empty.Items[0] == firstOfEmpty,
                "adding an entry to a null item list repairs the list");
        }

        private static void ListRemoves()
        {
            var data = new TodoData();
            var a = TodoListOps.AddItem(data, "a", null);
            TodoListOps.AddChild(a, "a1");
            TodoListOps.AddChild(a, "a2");
            var b = TodoListOps.AddItem(data, "b", null);
            var carriedChild = a.Children[0];

            Check(TodoListOps.RemoveItem(data, a.Id), "removing a known entry reports success");
            Check(data.Items.Count == 1 && data.Items[0] == b,
                "removing an entry takes it out of the list");
            Check(!data.Items.SelectMany(i => i.Children).Contains(carriedChild),
                "removing a parent takes its sub-todos with it");
            Check(!TodoListOps.RemoveItem(data, "missing") && data.Items.Count == 1,
                "removing an unknown entry changes nothing");
            Check(TodoListOps.RemoveItem(data, b.Id) && data.Items.Count == 0,
                "the last entry can be removed");

            var parent = Item("p", "parent", null);
            TodoListOps.AddChild(parent, "x");
            var kept = TodoListOps.AddChild(parent, "y");
            Check(TodoListOps.RemoveChild(parent, parent.Children[0].Id),
                "removing a known sub-todo reports success");
            Check(parent.Children.Count == 1 && parent.Children[0] == kept, "the sibling sub-todo stays");
            Check(!TodoListOps.RemoveChild(parent, "missing") && parent.Children.Count == 1,
                "removing an unknown sub-todo changes nothing");
        }

        private static void DayFilter()
        {
            var items = new List<TodoItem>();
            var carried = Item("carried", "carried", new DateTime(2026, 9, 25));
            carried.Children.Add(Child("sub", "sub", true));
            var doneFirst = Item("done-1", "done one", new DateTime(2026, 9, 27), true,
                new DateTime(2026, 9, 28));
            var openToday = Item("open-1", "open today", new DateTime(2026, 9, 28));
            var doneSecond = Item("done-2", "done two", new DateTime(2026, 9, 28), true,
                new DateTime(2026, 9, 28));
            var future = Item("future", "future", new DateTime(2026, 9, 29));
            items.AddRange(new[] { carried, doneFirst, openToday, doneSecond, future });

            var todayList = TodoListOps.ForDay(items, TodayDate, TodayDate);
            Check(todayList.Select(i => i.Id).SequenceEqual(new[] { "carried", "open-1", "done-1", "done-2" }),
                "the day list keeps the list order but moves completed entries last");
            Check(!todayList.Any(i => i.Id == "sub"), "sub-todos are not day rows");
            Check(todayList.Count == 4, "the day count counts top-level entries only");
            Check(!todayList.Contains(future), "a future entry is not in today's list");

            var tomorrow = TodoListOps.ForDay(items, new DateTime(2026, 9, 29), TodayDate);
            Check(tomorrow.Count == 1 && tomorrow[0] == future, "a future entry shows on its own day");

            var originalDay = TodoListOps.ForDay(items, new DateTime(2026, 9, 25), TodayDate);
            Check(originalDay.Count == 0, "a carried entry is not listed on its original day");

            var empty = TodoListOps.ForDay(items, new DateTime(2026, 9, 26), TodayDate);
            Check(empty.Count == 0, "a day with no entries has an empty list");

            Check(carried.Date == new DateTime(2026, 9, 25) && !carried.Done && carried.CompletedDate == null
                  && doneFirst.Done && doneFirst.CompletedDate == new DateTime(2026, 9, 28)
                  && openToday.Date == new DateTime(2026, 9, 28) && !openToday.Done,
                "building the day list does not mutate the entries");
        }

        private static void DisplayOrder()
        {
            var done1 = Item("d1", "done one", null, true, new DateTime(2026, 9, 28));
            var open1 = Item("o1", "open one", new DateTime(2026, 9, 28));
            var done2 = Item("d2", "done two", null, true, new DateTime(2026, 9, 28));
            var open2 = Item("o2", "open two", new DateTime(2026, 9, 28));
            var input = new List<TodoItem> { done1, open1, done2, open2 };

            var ordered = TodoListOps.OrderForDisplay(input);
            Check(ordered.Select(i => i.Id).SequenceEqual(new[] { "o1", "o2", "d1", "d2" }),
                "completed entries sort to the bottom without disturbing either group's order");
            Check(input.Select(i => i.Id).SequenceEqual(new[] { "d1", "o1", "d2", "o2" }),
                "sorting does not disturb the source list");
        }

        private static void RemovalSnapshots()
        {
            var day = new DateTime(2026, 10, 12);
            var one = Item("p1", "one", day);
            var two = Item("p2", "two", day);
            var three = Item("p3", "three", day);
            var items = new List<TodoItem> { one, two, three };

            var removal = TodoListOps.RemoveParent(items, two);
            Check(items.Count == 2 && !items.Contains(two),
                "removing a parent takes it out of the list");
            Check(!removal.IsEmpty && removal.Parents.Count == 1
                  && removal.Parents[0].Item == two && removal.Parents[0].Index == 1,
                "the removal records the entry and its old index");

            TodoListOps.RestoreRemoval(removal, items);
            Check(items.Count == 3 && items[0] == one && items[1] == two && items[2] == three,
                "restoring the removal puts the entry back at its old index");

            var missing = Item("p4", "missing", day);
            var untouched = TodoListOps.RemoveParent(items, missing);
            Check(untouched.IsEmpty && items.Count == 3,
                "removing an entry that is not in the list changes nothing");

            var noList = TodoListOps.RemoveParent(null, two);
            Check(noList.IsEmpty, "a null list yields an empty removal");
        }

        private static void ChildRemovalSnapshots()
        {
            var day = new DateTime(2026, 10, 12);
            var parent = Item("p1", "parent", day);
            var first = Child("c1", "first");
            var second = Child("c2", "second");
            var third = Child("c3", "third");
            parent.Children.AddRange(new[] { first, second, third });
            var items = new List<TodoItem> { parent };

            var removal = TodoListOps.RemoveChildItem(parent, second);
            Check(parent.Children.Count == 2 && !parent.Children.Contains(second),
                "removing a sub-todo takes it out of its parent");
            Check(!removal.IsEmpty && removal.Children.Count == 1
                  && removal.Children[0].Parent == parent && removal.Children[0].Child == second
                  && removal.Children[0].Index == 1,
                "the child removal records parent, child and index");
            Check(removal.Parents.Count == 0, "a child removal leaves the parent list alone");

            TodoListOps.RestoreRemoval(removal, items);
            Check(parent.Children.Count == 3 && parent.Children[0] == first
                  && parent.Children[1] == second && parent.Children[2] == third,
                "restoring the child removal puts the sub-todo back in place");

            var ghost = TodoListOps.RemoveChildItem(parent, Child("c4", "ghost"));
            Check(ghost.IsEmpty && parent.Children.Count == 3,
                "removing a sub-todo that is not there changes nothing");
        }

        private static void DayClear()
        {
            var today = new DateTime(2026, 10, 12);
            var day = new DateTime(2026, 10, 12);
            var items = DayClearFixture(day);
            var before = items.Select(State).ToList();

            var doneToday = items.First(i => i.Id == "d1");
            var doneToday2 = items.First(i => i.Id == "d3");
            var openParent = items.First(i => i.Id == "o1");
            var doneKid = openParent.Children.First(c => c.Id == "o1b");
            var doneKid2 = openParent.Children.First(c => c.Id == "o1c");

            Check(TodoListOps.HasCompletedOn(items, day, today),
                "the day reports completed rows when the clear has work");

            var removal = TodoListOps.RemoveCompletedOn(items, day, today);

            var recordedDone = removal.Parents.FirstOrDefault(p => p.Item == doneToday);
            var recordedDone2 = removal.Parents.FirstOrDefault(p => p.Item == doneToday2);
            Check(removal.Parents.Count == 2 && recordedDone != null && recordedDone2 != null
                  && recordedDone.Index == 0 && recordedDone2.Index == 3,
                "the clear takes both entries completed on the day, each at its old index");
            Check(!items.Contains(doneToday) && !items.Any(i => i.Id == "d1"),
                "the completed entry leaves the list");
            Check(doneToday.Children.Count == 1 && !doneToday.Children[0].Done,
                "the completed entry's open sub-todo rides along untouched");
            Check(items.Any(i => i.Id == "d2"),
                "the entry completed yesterday stays");
            var recordedKid = removal.Children.FirstOrDefault(c => c.Child == doneKid);
            var recordedKid2 = removal.Children.FirstOrDefault(c => c.Child == doneKid2);
            Check(removal.Children.Count == 2 && recordedKid != null && recordedKid2 != null
                  && recordedKid.Parent == openParent && recordedKid2.Parent == openParent
                  && recordedKid.Index == 1 && recordedKid2.Index == 2,
                "the clear records both completed sub-todos of the day's open entry");
            Check(!openParent.Children.Contains(doneKid) && !openParent.Children.Contains(doneKid2)
                  && openParent.Children.Count == 1 && openParent.Children[0].Id == "o1a",
                "those completed sub-todos leave and the open sibling stays");
            Check(items.First(i => i.Id == "f1").Children.Count == 1,
                "a completed sub-todo under a non-day entry stays");
            Check(items.Select(i => i.Id).SequenceEqual(new[] { "d2", "o1", "f1", "o2", "u1", "f2" }),
                "the day's open entries and every other entry survive in order");
            Check(!TodoListOps.HasCompletedOn(items, day, today),
                "the day reports nothing left after the clear");

            TodoListOps.RestoreRemoval(removal, items);
            Check(items.Select(State).SequenceEqual(before),
                "restoring the clear rebuilds the original list text for text");

            var openOnly = new List<TodoItem> { Item("x1", "open only", day) };
            Check(!TodoListOps.HasCompletedOn(openOnly, day, today),
                "a day of open entries reports nothing to clear");
            var noWork = TodoListOps.RemoveCompletedOn(openOnly, day, today);
            Check(noWork.IsEmpty && openOnly.Count == 1,
                "clearing a day of open entries changes nothing");

            var emptyDay = new DateTime(2026, 10, 20);
            Check(!TodoListOps.HasCompletedOn(items, emptyDay, today),
                "a day without rows reports nothing to clear");
            var emptyRemoval = TodoListOps.RemoveCompletedOn(items, emptyDay, today);
            Check(emptyRemoval.IsEmpty && items.Select(State).SequenceEqual(before),
                "clearing a day without rows yields an empty removal and changes nothing");
        }

        private static List<TodoItem> DayClearFixture(DateTime day)
        {
            // (1) completed on the day: the whole entry goes, an open sub-todo rides along
            var doneToday = Item("d1", "done today", day);
            TodoRollover.Toggle(doneToday, true, day);
            TodoListOps.AddChild(doneToday, "open kid");

            // (1b) a second entry completed on the day, childless: the descending
            // removal has two top-level indices (0 and 3) to walk
            var doneToday2 = Item("d3", "done today two", day);
            TodoRollover.Toggle(doneToday2, true, day);

            // (2) completed yesterday: stays
            var doneYesterday = Item("d2", "done yesterday", day.AddDays(-1));
            TodoRollover.Toggle(doneYesterday, true, day.AddDays(-1));

            // (3) open on the day with one open and two completed sub-todos
            var openParent = Item("o1", "open today", day);
            openParent.Children.Add(Child("o1a", "open kid"));
            openParent.Children.Add(Child("o1b", "done kid", true));
            openParent.Children.Add(Child("o1c", "done kid two", true));

            // (4) off the day with a completed sub-todo: the whole entry stays
            var futureParent = Item("f1", "future parent", day.AddDays(1));
            futureParent.Children.Add(Child("f1a", "done kid", true));

            // (5) the day's untouched rows: open entries, the undated one, the future
            var openToday = Item("o2", "open today too", day);
            var undated = Item("u1", "undated", null);
            var future = Item("f2", "future", day.AddDays(2));

            return new List<TodoItem>
            {
                doneToday, doneYesterday, openParent, doneToday2, futureParent, openToday, undated, future
            };
        }

        private static string State(TodoItem item)
        {
            return item.Id + "|" + item.Text + "|" + item.Done + "|"
                + (item.CompletedDate.HasValue
                    ? item.CompletedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : "-")
                + "|" + string.Join(",", item.Children.Select(c => c.Id + ":" + c.Text + ":" + c.Done));
        }

        private static int CountOn(List<TodoDayCell> cells, DateTime date)
        {
            var matches = cells.Where(c => c.Date == date).ToList();
            return matches.Count == 0 ? -1 : matches[0].TopLevelCount;
        }

        private static List<TodoItem> MonthFixture()
        {
            var carried = Item("a", "carried", new DateTime(2026, 9, 25));
            carried.Children.Add(Child("a1", "sub one"));
            carried.Children.Add(Child("a2", "sub two"));
            var completed = Item("b", "done earlier", new DateTime(2026, 9, 20), true,
                new DateTime(2026, 9, 26));
            var future = Item("c", "future", new DateTime(2026, 10, 2));
            var today = Item("d", "today", new DateTime(2026, 9, 28));
            var undated = Item("e", "undated", null);
            var yesterday = Item("f", "done yesterday", new DateTime(2026, 9, 27), true,
                new DateTime(2026, 9, 27));
            yesterday.Children.Add(Child("f1", "sub"));
            var withSubs = Item("g", "with subs", new DateTime(2026, 9, 29));
            withSubs.Children.Add(Child("g1", "one"));
            withSubs.Children.Add(Child("g2", "two"));
            withSubs.Children.Add(Child("g3", "three"));

            return new List<TodoItem> { carried, completed, future, today, undated, yesterday, withSubs };
        }

        private static TodoItem Item(string id, string text, DateTime? date,
            bool done = false, DateTime? completed = null)
        {
            return new TodoItem
            {
                Id = id,
                Text = text,
                Date = date,
                Done = done,
                CompletedDate = completed,
                Children = new List<SubTodoItem>()
            };
        }

        private static SubTodoItem Child(string id, string text, bool done = false)
        {
            return new SubTodoItem { Id = id, Text = text, Done = done };
        }
    }
}
