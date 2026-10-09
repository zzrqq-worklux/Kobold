using System;
using System.Globalization;

namespace Kobold.Core
{
    /// <summary>
    /// The day cell's calendar line (zh UI): the lunar month/day name, the 24
    /// solar terms and the traditional festivals. Pure logic - no DateTime.Now,
    /// every input is normalized with .Date, and the BCL
    /// <see cref="ChineseLunisolarCalendar"/> carries the lunar conversion (no
    /// data table, no dependency). Dates outside the documented 1901-2100
    /// window return null from every method instead of throwing.
    ///
    /// Solar terms use Meeus' low-accuracy apparent solar longitude
    /// ("Astronomical Algorithms", ch. 25: mean longitude
    /// 280.46646 + 36000.76983 T + 0.0003032 T^2, equation of the centre
    /// 1.914602 sin M + 0.019993 sin 2M + 0.000289 sin 3M, nutation
    /// -0.00569 - 0.00478 sin Omega), solved by Newton iteration for every
    /// 15-degree multiple and evaluated at China Standard Time (UTC+8).
    /// Accuracy is ~0.01 degree (minutes), so the civil day is stable across
    /// 1901-2100; pinned 2026 terms match the published calendar within
    /// minutes (立春 02-04, 清明 04-05, 白露 09-07, ...).
    /// </summary>
    public static class LunarCalendar
    {
        // The BCL calendar itself supports 1901-02-19 .. 2101-01-28; the
        // public contract here is the documented 1901-2100 window, so both
        // edges (including 2101-01-01) return null.
        private static readonly DateTime MinDate = new DateTime(1901, 2, 19);
        private static readonly DateTime MaxDate = new DateTime(2100, 12, 31);

        private static readonly ChineseLunisolarCalendar Lunar = new ChineseLunisolarCalendar();

        private static readonly string[] MonthNames =
        {
            "正月", "二月", "三月", "四月", "五月", "六月",
            "七月", "八月", "九月", "十月", "冬月", "腊月"
        };

        private static readonly string[] Digits = { "", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

        // Index order follows the Gregorian year: 小寒 (lambda 285) first.
        private static readonly string[] SolarTermNames =
        {
            "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨",
            "立夏", "小满", "芒种", "夏至", "小暑", "大暑", "立秋", "处暑",
            "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至"
        };

        private const double J2000 = 2451545.0;
        private const double DegToRad = Math.PI / 180.0;
        private static readonly DateTime Epoch = new DateTime(2000, 1, 1, 12, 0, 0);

        // One-entry cache: a Refresh asks for all ~42 visible days, almost all
        // in the same year, so the 24 Newton solves run once per year.
        private static int _cachedTermYear = int.MinValue;
        private static DateTime[] _cachedTermDates;

        /// <summary>The lunar month/day name, e.g. 八月二十, leap months 闰六月初一.</summary>
        public static string DayName(DateTime date)
        {
            DateTime day = date.Date;
            if (day < MinDate || day > MaxDate) return null;

            int year = Lunar.GetYear(day);
            int leapMonth = Lunar.GetLeapMonth(year);
            int monthIndex = Lunar.GetMonth(day);
            bool isLeap = leapMonth != 0 && monthIndex == leapMonth;
            int month = leapMonth != 0 && monthIndex >= leapMonth ? monthIndex - 1 : monthIndex;
            int dayOfMonth = Lunar.GetDayOfMonth(day);

            string name = MonthNames[month - 1] + DayOfMonthName(dayOfMonth);
            return isLeap ? "闰" + name : name;
        }

        /// <summary>One of the 24 standard solar term names, or null on other days.</summary>
        public static string SolarTermName(DateTime date)
        {
            DateTime day = date.Date;
            if (day < MinDate || day > MaxDate) return null;

            DateTime[] dates = TermDates(day.Year);
            for (int i = 0; i < dates.Length; i++)
            {
                if (dates[i] == day) return SolarTermNames[i];
            }
            return null;
        }

        /// <summary>
        /// The exact festival set: 春节/元宵/端午/七夕/中秋/重阳 (lunar), 除夕 (the
        /// day before 正月初一) and the fixed-solar 元旦/劳动节/国庆节, else null.
        /// 清明 is intentionally absent - it is a solar term.
        /// </summary>
        public static string FestivalName(DateTime date)
        {
            DateTime day = date.Date;
            if (day < MinDate || day > MaxDate) return null;

            if (day.Month == 1 && day.Day == 1) return "元旦";
            if (day.Month == 5 && day.Day == 1) return "劳动节";
            if (day.Month == 10 && day.Day == 1) return "国庆节";

            int month, dayOfMonth;
            bool isLeap;
            if (TryLunarParts(day, out month, out dayOfMonth, out isLeap) && !isLeap)
            {
                if (month == 1 && dayOfMonth == 1) return "春节";
                if (month == 1 && dayOfMonth == 15) return "元宵";
                if (month == 5 && dayOfMonth == 5) return "端午";
                if (month == 7 && dayOfMonth == 7) return "七夕";
                if (month == 8 && dayOfMonth == 15) return "中秋";
                if (month == 9 && dayOfMonth == 9) return "重阳";
            }

            // 除夕: the day before the lunar new year (the last day of the
            // 12th lunar month, leap or not).
            int nextMonth, nextDay;
            bool nextLeap;
            if (TryLunarParts(day.AddDays(1), out nextMonth, out nextDay, out nextLeap) &&
                !nextLeap && nextMonth == 1 && nextDay == 1)
            {
                return "除夕";
            }

            return null;
        }

        private static bool TryLunarParts(DateTime date, out int month, out int day, out bool isLeap)
        {
            month = 0;
            day = 0;
            isLeap = false;
            if (date < MinDate || date > MaxDate) return false;

            int year = Lunar.GetYear(date);
            int leapMonth = Lunar.GetLeapMonth(year);
            int monthIndex = Lunar.GetMonth(date);
            isLeap = leapMonth != 0 && monthIndex == leapMonth;
            month = leapMonth != 0 && monthIndex >= leapMonth ? monthIndex - 1 : monthIndex;
            day = Lunar.GetDayOfMonth(date);
            return true;
        }

        private static string DayOfMonthName(int day)
        {
            if (day == 10) return "初十";
            if (day < 10) return "初" + Digits[day];
            if (day < 20) return "十" + Digits[day - 10];
            if (day == 20) return "二十";
            if (day < 30) return "廿" + Digits[day - 20];
            return "三十";
        }

        private static DateTime[] TermDates(int year)
        {
            if (year == _cachedTermYear) return _cachedTermDates;

            var dates = new DateTime[SolarTermNames.Length];
            for (int i = 0; i < dates.Length; i++) dates[i] = SolarTermDate(year, i);
            _cachedTermYear = year;
            _cachedTermDates = dates;
            return dates;
        }

        /// <summary>
        /// The China Standard Time civil day the sun reaches 285 + 15*index
        /// degrees of apparent ecliptic longitude. The Newton step uses the
        /// sun's mean daily motion (0.9856474 deg/day); eight iterations
        /// converge well below a minute.
        /// </summary>
        private static DateTime SolarTermDate(int year, int termIndex)
        {
            double target = (285.0 + 15.0 * termIndex) % 360.0;
            double julianDay = J2000 + (new DateTime(year, 1, 6) - Epoch).TotalDays + termIndex * 15.2184;

            for (int i = 0; i < 8; i++)
            {
                double delta = target - ApparentSolarLongitude(julianDay);
                while (delta > 180.0) delta -= 360.0;
                while (delta < -180.0) delta += 360.0;
                julianDay += delta / 0.9856474;
            }

            // Julian Day -> UT civil date, then UTC+8 (China Standard Time).
            return Epoch.AddDays(julianDay - J2000).AddHours(8).Date;
        }

        private static double ApparentSolarLongitude(double julianDay)
        {
            double t = (julianDay - J2000) / 36525.0;
            double meanLongitude = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
            double meanAnomaly = (357.52911 + 35999.05029 * t - 0.0001537 * t * t) * DegToRad;
            double center = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(meanAnomaly)
                          + (0.019993 - 0.000101 * t) * Math.Sin(2.0 * meanAnomaly)
                          + 0.000289 * Math.Sin(3.0 * meanAnomaly);
            double omega = (125.04 - 1934.136 * t) * DegToRad;
            return meanLongitude + center - 0.00569 - 0.00478 * Math.Sin(omega);
        }
    }
}
