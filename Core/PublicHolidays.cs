using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kobold.Core
{
    /// <summary>
    /// Region holiday marks for the todo day cells. China comes from the
    /// hand-written official table (<see cref="CnHolidayData"/>, no network);
    /// Japan/US/UK are computed by rule and need no per-year data. Pure logic:
    /// no DateTime.Now, every date normalized with .Date. The caller
    /// normalizes the region with <see cref="NormalizeRegion"/> and asks for a
    /// day's label with <see cref="GetLabel"/>; a null result means "no
    /// region mark" and the view falls back to the base calendar line.
    /// </summary>
    public static class PublicHolidays
    {
        public const string RegionNone = "none";
        public const string RegionCn = "cn";
        public const string RegionJp = "jp";
        public const string RegionUs = "us";
        public const string RegionUk = "uk";

        /// <summary>The make-up workday mark shared by the CN table and the view.</summary>
        public const string WorkdayMark = "班";

        private static Dictionary<DateTime, string> _cnLookup;

        /// <summary>null/empty/unknown -> "cn"; known ids are returned canonical.</summary>
        public static string NormalizeRegion(string value)
        {
            if (value == null) return RegionCn;
            switch (value.Trim().ToLowerInvariant())
            {
                case RegionNone: return RegionNone;
                case RegionJp: return RegionJp;
                case RegionUs: return RegionUs;
                case RegionUk: return RegionUk;
                default: return RegionCn;
            }
        }

        /// <summary>The region's holiday label for the day, or null.</summary>
        public static string GetLabel(string region, DateTime date)
        {
            DateTime day = date.Date;
            switch (NormalizeRegion(region))
            {
                case RegionNone: return null;
                case RegionJp: return JapanLabel(day);
                case RegionUs: return UnitedStatesLabel(day);
                case RegionUk: return UnitedKingdomLabel(day);
                default: return ChinaLabel(day);
            }
        }

        #region China

        private static string ChinaLabel(DateTime date)
        {
            string label;
            return CnLookup().TryGetValue(date, out label) ? label : null;
        }

        private static Dictionary<DateTime, string> CnLookup()
        {
            if (_cnLookup != null) return _cnLookup;

            var lookup = new Dictionary<DateTime, string>();
            foreach (var row in CnHolidayData.Rows)
            {
                string[] parts = (row ?? string.Empty).Split('|');
                if (parts.Length != 3) continue; // production ignores bad rows (the checks pin all rows)

                DateTime date;
                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out date)) continue;

                string name = parts[1];
                if (string.IsNullOrEmpty(name)) continue;

                if (parts[2] == "off") lookup[date] = name;
                else if (parts[2] == "work") lookup[date] = WorkdayMark;
            }
            _cnLookup = lookup;
            return lookup;
        }

        #endregion

        #region Japan (祝日法)

        /// <summary>
        /// 元日/成人の日/建国記念の日/天皇誕生日/春分の日/昭和の日/憲法記念日/
        /// みどりの日/こどもの日/海の日/山の日/敬老の日/秋分の日/スポーツの日/
        /// 文化の日/勤労感謝の日, or null. The equinox days use the standard
        /// 国立天文台 approximation (valid 1948-2150):
        /// floor(20.8431 + 0.242194*(y-1980) - floor((y-1980)/4)) for 春分,
        /// 23.2488 for 秋分 (2026 -> 3/20 and 9/23).
        /// </summary>
        private static string JapanLabel(DateTime date)
        {
            string primary = PrimaryJapan(date);
            if (primary != null) return primary;
            if (IsSubstituteJapan(date)) return "振替休日";

            // 国民の休日: a non-holiday day sandwiched between two holidays.
            if (date.DayOfWeek != DayOfWeek.Sunday &&
                date > DateTime.MinValue && date < DateTime.MaxValue &&
                HasHolidayJapan(date.AddDays(-1)) && HasHolidayJapan(date.AddDays(1)))
            {
                return "国民の休日";
            }
            return null;
        }

        private static string PrimaryJapan(DateTime date)
        {
            int year = date.Year;
            switch (date.Month)
            {
                case 1:
                    if (date.Day == 1) return "元日";
                    if (date == NthWeekday(year, 1, DayOfWeek.Monday, 2)) return "成人の日";
                    break;
                case 2:
                    if (date.Day == 11) return "建国記念の日";
                    if (date.Day == 23) return "天皇誕生日";
                    break;
                case 3:
                    if (date == VernalEquinoxJapan(year)) return "春分の日";
                    break;
                case 4:
                    if (date.Day == 29) return "昭和の日";
                    break;
                case 5:
                    if (date.Day == 3) return "憲法記念日";
                    if (date.Day == 4) return "みどりの日";
                    if (date.Day == 5) return "こどもの日";
                    break;
                case 7:
                    if (date == NthWeekday(year, 7, DayOfWeek.Monday, 3)) return "海の日";
                    break;
                case 8:
                    if (date.Day == 11) return "山の日";
                    break;
                case 9:
                    if (date == NthWeekday(year, 9, DayOfWeek.Monday, 3)) return "敬老の日";
                    if (date == AutumnalEquinoxJapan(year)) return "秋分の日";
                    break;
                case 10:
                    if (date == NthWeekday(year, 10, DayOfWeek.Monday, 2)) return "スポーツの日";
                    break;
                case 11:
                    if (date.Day == 3) return "文化の日";
                    if (date.Day == 23) return "勤労感謝の日";
                    break;
            }
            return null;
        }

        /// <summary>
        /// 振替休日: the first non-holiday day after a run of holidays that
        /// started on a Sunday (since 2007 the run can push the substitute
        /// past a Monday holiday, e.g. 2026-05-06). The walk stops at the
        /// DateTime.MinValue edge instead of throwing there.
        /// </summary>
        private static bool IsSubstituteJapan(DateTime date)
        {
            if (date <= DateTime.MinValue) return false;
            DateTime cursor = date.AddDays(-1);
            while (PrimaryJapan(cursor) != null)
            {
                if (cursor.DayOfWeek == DayOfWeek.Sunday) return true;
                if (cursor == DateTime.MinValue) break;
                cursor = cursor.AddDays(-1);
            }
            return false;
        }

        private static bool HasHolidayJapan(DateTime date)
        {
            return PrimaryJapan(date) != null || IsSubstituteJapan(date);
        }

        private static DateTime? VernalEquinoxJapan(int year)
        {
            if (year < 1948 || year > 2150) return null;
            int day = (int)Math.Floor(20.8431 + 0.242194 * (year - 1980) - Math.Floor((year - 1980) / 4.0));
            return new DateTime(year, 3, day);
        }

        private static DateTime? AutumnalEquinoxJapan(int year)
        {
            if (year < 1948 || year > 2150) return null;
            int day = (int)Math.Floor(23.2488 + 0.242194 * (year - 1980) - Math.Floor((year - 1980) / 4.0));
            return new DateTime(year, 9, day);
        }

        #endregion

        #region United States (federal, observed dates)

        /// <summary>
        /// Federal holidays labelled on the observed date (Sat -> preceding
        /// Friday, Sun -> following Monday; when actual == observed, once).
        /// 2026 pins: 1/1, 1/19, 2/16, 5/25, 6/19, 7/3, 9/7, 10/12, 11/11,
        /// 11/26, 12/25.
        /// </summary>
        private static string UnitedStatesLabel(DateTime date)
        {
            int year = date.Year;
            if (date == NthWeekday(year, 1, DayOfWeek.Monday, 3)) return "金恩日";
            if (date == NthWeekday(year, 2, DayOfWeek.Monday, 3)) return "总统日";
            if (date == LastWeekday(year, 5, DayOfWeek.Monday)) return "阵亡日";
            if (date == NthWeekday(year, 9, DayOfWeek.Monday, 1)) return "劳工节";
            if (date == NthWeekday(year, 10, DayOfWeek.Monday, 2)) return "哥伦布日";
            if (date == NthWeekday(year, 11, DayOfWeek.Thursday, 4)) return "感恩节";

            // Fixed dates; +-1 year covers 1/1 observed on the previous 12/31.
            int first = Math.Max(1, year - 1);
            int last = Math.Min(9999, year + 1);
            for (int y = first; y <= last; y++)
            {
                if (date == ObservedUs(y, 1, 1)) return "元旦";
                if (date == ObservedUs(y, 6, 19)) return "六月节";
                if (date == ObservedUs(y, 7, 4)) return "独立日";
                if (date == ObservedUs(y, 11, 11)) return "退伍日";
                if (date == ObservedUs(y, 12, 25)) return "圣诞";
            }
            return null;
        }

        private static DateTime ObservedUs(int year, int month, int day)
        {
            var actual = new DateTime(year, month, day);
            if (actual.DayOfWeek == DayOfWeek.Saturday) return actual.AddDays(-1);
            if (actual.DayOfWeek == DayOfWeek.Sunday) return actual.AddDays(1);
            return actual;
        }

        #endregion

        #region United Kingdom (England & Wales)

        /// <summary>
        /// 元旦/耶稣受难日/复活节翌日/五月假/春假/夏假/圣诞/节礼日. A fixed
        /// bank holiday on Sat/Sun moves to the following Monday, keeping the
        /// original name (2026: 节礼日 12/26 Sat -> 12/28). Adjacent
        /// substitutions of Christmas and Boxing Day are computed
        /// independently (the rare same-Monday collision resolves to
        /// Christmas); Easter uses the anonymous Gregorian computus, shared
        /// with no other caller.
        /// </summary>
        private static string UnitedKingdomLabel(DateTime date)
        {
            int year = date.Year;
            DateTime easter = EasterSunday(year);
            if (date == easter.AddDays(-2)) return "耶稣受难日";
            if (date == easter.AddDays(1)) return "复活节翌日";
            if (date == NthWeekday(year, 5, DayOfWeek.Monday, 1)) return "五月假";
            if (date == LastWeekday(year, 5, DayOfWeek.Monday)) return "春假";
            if (date == LastWeekday(year, 8, DayOfWeek.Monday)) return "夏假";

            int first = Math.Max(1, year - 1);
            int last = Math.Min(9999, year + 1);
            for (int y = first; y <= last; y++)
            {
                if (date == ObservedUk(y, 1, 1)) return "元旦";
                if (date == ObservedUk(y, 12, 25)) return "圣诞";
                if (date == ObservedUk(y, 12, 26)) return "节礼日";
            }
            return null;
        }

        private static DateTime ObservedUk(int year, int month, int day)
        {
            var actual = new DateTime(year, month, day);
            if (actual.DayOfWeek == DayOfWeek.Saturday) return actual.AddDays(2);
            if (actual.DayOfWeek == DayOfWeek.Sunday) return actual.AddDays(1);
            return actual;
        }

        /// <summary>Anonymous Gregorian algorithm for Easter Sunday.</summary>
        private static DateTime EasterSunday(int year)
        {
            int a = year % 19;
            int b = year / 100;
            int c = year % 100;
            int d = b / 4;
            int e = b % 4;
            int f = (b + 8) / 25;
            int g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30;
            int i = c / 4;
            int k = c % 4;
            int l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31;
            int day = (h + l - 7 * m + 114) % 31 + 1;
            return new DateTime(year, month, day);
        }

        #endregion

        #region Calendar helpers

        private static DateTime NthWeekday(int year, int month, DayOfWeek weekday, int n)
        {
            var first = new DateTime(year, month, 1);
            int delta = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(delta + 7 * (n - 1));
        }

        private static DateTime LastWeekday(int year, int month, DayOfWeek weekday)
        {
            var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            int delta = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
            return last.AddDays(-delta);
        }

        #endregion
    }
}
