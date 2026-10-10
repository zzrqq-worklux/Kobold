using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kobold.Core
{
    /// <summary>
    /// One cell of the todo month grid: its day plus the render markers the
    /// calendar view paints (out-of-month, today, selected, marker count).
    /// </summary>
    public class TodoDayCell
    {
        public DateTime Date { get; set; }
        public bool InMonth { get; set; }
        public bool IsToday { get; set; }
        public bool IsSelected { get; set; }
        public int TopLevelCount { get; set; }
    }

    /// <summary>
    /// The month grid maths behind the todo calendar: full weeks starting on
    /// the caller's FirstDayOfWeek, out-of-month padding, today/selected
    /// markers, and the top-level entry count per day. The count uses
    /// <see cref="TodoRollover.DisplayDate"/>, so carried and completed
    /// entries land on the day they are shown on; nothing is mutated here.
    /// </summary>
    public static class TodoCalendar
    {
        /// <summary>
        /// Moves a year/month pair by whole months, rolling over both year
        /// ends (2026-12 + 1 = 2027-01, 2027-01 - 1 = 2026-12).
        /// </summary>
        public static (int year, int month) ShiftMonth(int year, int month, int delta)
        {
            DateTime shifted = new DateTime(year, month, 1).AddMonths(delta);
            return (shifted.Year, shifted.Month);
        }

        /// <summary>
        /// The month as whole weeks: from the FirstDayOfWeek week containing
        /// the 1st to the week containing the last day (4-6 rows of 7 unique
        /// days). Days outside the month keep InMonth=false and stay in the
        /// grid; TopLevelCount counts top-level entries whose display date is
        /// the cell's day.
        /// </summary>
        public static List<TodoDayCell> BuildMonth(
            int year, int month,
            IList<TodoItem> items,
            DateTime? selectedDate,
            DateTime today,
            DayOfWeek firstDayOfWeek)
        {
            DateTime first = new DateTime(year, month, 1);
            DateTime dayToday = today.Date;
            DateTime? selected = selectedDate?.Date;

            var counts = new Dictionary<DateTime, int>();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null) continue;
                    DateTime day = TodoRollover.DisplayDate(item, dayToday);
                    int count;
                    counts.TryGetValue(day, out count);
                    counts[day] = count + 1;
                }
            }

            int lead = LeadDays(year, month, firstDayOfWeek);
            int cellCount = WeekCount(year, month, firstDayOfWeek) * 7;
            DateTime start = first.AddDays(-lead);

            var cells = new List<TodoDayCell>(cellCount);
            for (int i = 0; i < cellCount; i++)
            {
                DateTime date = start.AddDays(i);
                int count;
                counts.TryGetValue(date, out count);
                cells.Add(new TodoDayCell
                {
                    Date = date,
                    InMonth = date.Year == year && date.Month == month,
                    IsToday = date == dayToday,
                    IsSelected = selected.HasValue && date == selected.Value,
                    TopLevelCount = count
                });
            }
            return cells;
        }

        /// <summary>
        /// The calendar culture for a UI language (month names, first day of
        /// week): zh maps to zh-CN, ja to ja-JP, anything else to en-US.
        /// </summary>
        public static CultureInfo CultureForLanguage(string language)
        {
            switch (language)
            {
                case "zh": return CultureInfo.GetCultureInfo("zh-CN");
                case "ja": return CultureInfo.GetCultureInfo("ja-JP");
                default: return CultureInfo.GetCultureInfo("en-US");
            }
        }

        /// <summary>The month as whole weeks (4-6 rows) with weeks starting on the given day.</summary>
        public static int WeekCount(int year, int month, DayOfWeek firstDayOfWeek)
        {
            return (LeadDays(year, month, firstDayOfWeek) + DateTime.DaysInMonth(year, month) + 6) / 7;
        }

        /// <summary>Days from the week's first day to the 1st of the month.</summary>
        private static int LeadDays(int year, int month, DayOfWeek firstDayOfWeek)
        {
            var first = new DateTime(year, month, 1);
            return ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        }
    }
}
