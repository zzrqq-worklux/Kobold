using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Kobold.Core;

namespace Kobold.LunarCalendarCheck
{
    /// <summary>
    /// Behaviour checks for the todo day-cell calendar line: the BCL-based
    /// lunar month/day names, the 24 solar terms, the traditional festivals,
    /// the hand-written CN holiday table (2025/2026), the region
    /// normalization and the rule-based JP/US/UK holiday labels. No network,
    /// no DateTime.Now: every date is a fixture. Exit code 0 = all green;
    /// 1 = failures.
    /// Run: dotnet run --project tests/LunarCalendarCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            RunAll();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL LUNAR CALENDAR CHECKS PASSED");
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
            LunarDayNames();
            Festivals();
            SolarTerms();
            LunarRange();
            LeapMonths();
            CnTable();
            CnLabels();
            RegionNormalization();
            JapanRules();
            UnitedStatesRules();
            UnitedKingdomRules();
            EdgeRobustness();
        }

        private static DateTime D(string date)
        {
            return DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static void CheckLabel(string region, string date, string expected, string what)
        {
            string actual = PublicHolidays.GetLabel(region, D(date));
            Check(actual == expected,
                what + " (" + date + " [" + (region ?? "null") + "] got " + (actual ?? "null") +
                ", wanted " + (expected ?? "null") + ")");
        }

        private static void LunarDayNames()
        {
            Check(LunarCalendar.DayName(D("2024-02-10")) == "正月初一", "2024-02-10 is 正月初一");
            Check(LunarCalendar.DayName(D("2025-01-29")) == "正月初一", "2025-01-29 is 正月初一");
            Check(LunarCalendar.DayName(D("2026-02-17")) == "正月初一", "2026-02-17 is 正月初一");
            Check(LunarCalendar.DayName(D("2026-09-25")) == "八月十五", "2026-09-25 is 八月十五");
            Check(LunarCalendar.DayName(D("2026-09-24")) == "八月十四", "2026-09-24 is 八月十四 (十一-十九 series)");
            Check(LunarCalendar.DayName(D("2026-02-16")) == "腊月廿九", "2026-02-16 is 腊月廿九 (廿 series)");
            Check(LunarCalendar.DayName(D("2026-02-26")) == "正月初十", "2026-02-26 is 正月初十");
            Check(LunarCalendar.DayName(D("2026-03-08")) == "正月二十", "2026-03-08 is 正月二十");
            Check(LunarCalendar.DayName(D("2026-03-18")) == "正月三十", "2026-03-18 is 正月三十");
        }

        private static void Festivals()
        {
            Check(LunarCalendar.FestivalName(D("2026-02-17")) == "春节", "2026-02-17 is 春节");
            Check(LunarCalendar.FestivalName(D("2026-03-03")) == "元宵", "2026-03-03 is 元宵");
            Check(LunarCalendar.FestivalName(D("2026-06-19")) == "端午", "2026-06-19 is 端午");
            Check(LunarCalendar.FestivalName(D("2026-09-25")) == "中秋", "2026-09-25 is 中秋");
            Check(LunarCalendar.FestivalName(D("2026-02-16")) == "除夕", "2026-02-16 is 除夕");
            Check(LunarCalendar.FestivalName(D("2026-01-01")) == "元旦", "2026-01-01 is 元旦");
            Check(LunarCalendar.FestivalName(D("2026-05-01")) == "劳动节", "2026-05-01 is 劳动节");
            Check(LunarCalendar.FestivalName(D("2026-10-01")) == "国庆节", "2026-10-01 is 国庆节");
            Check(LunarCalendar.FestivalName(D("2026-04-05")) == null,
                "清明 is a solar term, not a festival");
            Check(LunarCalendar.FestivalName(D("2026-04-04")) == null,
                "a day before 清明 carries no festival");
        }

        private static void SolarTerms()
        {
            Check(LunarCalendar.SolarTermName(D("2026-02-04")) == "立春", "2026-02-04 is 立春");
            Check(LunarCalendar.SolarTermName(D("2026-09-07")) == "白露", "2026-09-07 is 白露");
            Check(LunarCalendar.SolarTermName(D("2026-09-23")) == "秋分", "2026-09-23 is 秋分");
            Check(LunarCalendar.SolarTermName(D("2026-10-08")) == "寒露", "2026-10-08 is 寒露");
            Check(LunarCalendar.SolarTermName(D("2026-10-23")) == "霜降", "2026-10-23 is 霜降");
            Check(LunarCalendar.SolarTermName(D("2026-11-07")) == "立冬", "2026-11-07 is 立冬");
            Check(LunarCalendar.SolarTermName(D("2026-04-05")) == "清明", "2026-04-05 is 清明");
            Check(LunarCalendar.SolarTermName(D("2026-10-01")) == null, "a non-term day returns null");
        }

        private static void LunarRange()
        {
            foreach (var date in new[] { D("1901-01-01"), D("2101-01-01") })
            {
                string dayName = null, term = null, festival = null;
                bool threw = false;
                try
                {
                    dayName = LunarCalendar.DayName(date);
                    term = LunarCalendar.SolarTermName(date);
                    festival = LunarCalendar.FestivalName(date);
                }
                catch (Exception)
                {
                    threw = true;
                }
                Check(!threw && dayName == null && term == null && festival == null,
                    date.ToString("yyyy-MM-dd") + " is out of the 1901-2100 window: null, no throw");
            }

            bool lastThrew = false;
            string last = null;
            try { last = LunarCalendar.DayName(D("2100-12-31")); }
            catch (Exception) { lastThrew = true; }
            Check(!lastThrew && last != null, "2100-12-31 still converts normally");
        }

        private static void LeapMonths()
        {
            var leaps2025 = DistinctLeapMonthNames(2025);
            Check(leaps2025.Count == 1 && leaps2025.Contains("闰六月"),
                "2025 converts exactly one leap month (闰六月), found: " + string.Join(",", leaps2025));

            var leaps2026 = DistinctLeapMonthNames(2026);
            Check(leaps2026.Count == 0,
                "2026 converts no leap month, found: " + string.Join(",", leaps2026));
        }

        private static HashSet<string> DistinctLeapMonthNames(int year)
        {
            var found = new HashSet<string>();
            for (var day = new DateTime(year, 1, 1); day.Year == year; day = day.AddDays(1))
            {
                string name = LunarCalendar.DayName(day);
                if (name != null && name.StartsWith("闰", StringComparison.Ordinal))
                {
                    found.Add(name.Substring(0, 3));
                }
            }
            return found;
        }

        private static void CnTable()
        {
            var type = typeof(PublicHolidays).Assembly.GetType("Kobold.Core.CnHolidayData");
            var field = type == null
                ? null
                : type.GetField("Rows", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            var rows = field == null ? null : (string[])field.GetValue(null);
            Check(rows != null, "CnHolidayData.Rows is found");
            if (rows == null) return;

            Check(rows.Length == 72, "the CN table holds exactly 72 rows (got " + rows.Length + ")");

            var problems = new List<string>();
            DateTime previous = DateTime.MinValue;
            for (int i = 0; i < rows.Length; i++)
            {
                string[] parts = (rows[i] ?? string.Empty).Split('|');
                if (parts.Length != 3)
                {
                    problems.Add("row " + i + " is not yyyy-MM-dd|name|off|work: '" + rows[i] + "'");
                    continue;
                }
                DateTime date;
                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out date))
                {
                    problems.Add("row " + i + " does not parse: '" + rows[i] + "'");
                    continue;
                }
                if (string.IsNullOrEmpty(parts[1]) || (parts[2] != "off" && parts[2] != "work"))
                {
                    problems.Add("row " + i + " has an empty name or a bad type: '" + rows[i] + "'");
                    continue;
                }
                if (date <= previous)
                {
                    problems.Add("row " + i + " is not strictly sorted by date: '" + rows[i] + "'");
                }
                previous = date;
            }
            Check(problems.Count == 0,
                "every CN row parses as yyyy-MM-dd|name|off|work, sorted and unique" +
                (problems.Count == 0 ? "" : " - " + string.Join("; ", problems)));
        }

        private static void CnLabels()
        {
            Check(PublicHolidays.WorkdayMark == "班", "the make-up workday mark is 班");
            CheckLabel("cn", "2025-01-26", "班", "2025-01-26 is a make-up workday");
            CheckLabel("cn", "2025-10-01", "国庆中秋", "the 2025 combined span shows 国庆中秋");
            CheckLabel("cn", "2025-01-01", "元旦", "2025-01-01 is 元旦");
            CheckLabel("cn", "2026-10-01", "国庆节", "2026-10-01 is 国庆节");
            CheckLabel("cn", "2026-10-10", "班", "2026-10-10 is a make-up workday");
            CheckLabel("cn", "2026-02-17", "春节", "2026-02-17 is 春节");
            CheckLabel("cn", "2026-02-28", "班", "2026-02-28 is a make-up workday");
            CheckLabel("cn", "2026-10-08", null, "2026-10-08 is a normal day");
            CheckLabel("cn", "2024-01-01", null, "a year without table data falls back (2024-01-01)");
        }

        private static void RegionNormalization()
        {
            Check(PublicHolidays.NormalizeRegion(null) == "cn", "a null region normalizes to cn");
            Check(PublicHolidays.NormalizeRegion("") == "cn", "an empty region normalizes to cn");
            Check(PublicHolidays.NormalizeRegion("cn") == "cn", "cn stays cn");
            Check(PublicHolidays.NormalizeRegion("JP") == "jp", "an upper-case region normalizes to jp");
            Check(PublicHolidays.NormalizeRegion(" nope ") == "cn", "an unknown region normalizes to cn");
            Check(PublicHolidays.NormalizeRegion("none") == "none", "none stays none");
            Check(PublicHolidays.NormalizeRegion("uk") == "uk", "uk stays uk");

            CheckLabel("none", "2026-10-01", null, "the none region never labels a day");
            CheckLabel(null, "2026-10-01", "国庆节", "a null region behaves as cn");
        }

        private static void JapanRules()
        {
            var pins = new (string date, string name)[]
            {
                ("2026-01-01", "元日"),
                ("2026-01-12", "成人の日"),
                ("2026-02-11", "建国記念の日"),
                ("2026-02-23", "天皇誕生日"),
                ("2026-03-20", "春分の日"),
                ("2026-04-29", "昭和の日"),
                ("2026-05-03", "憲法記念日"),
                ("2026-05-04", "みどりの日"),
                ("2026-05-05", "こどもの日"),
                ("2026-07-20", "海の日"),
                ("2026-08-11", "山の日"),
                ("2026-09-21", "敬老の日"),
                ("2026-09-23", "秋分の日"),
                ("2026-10-12", "スポーツの日"),
                ("2026-11-03", "文化の日"),
                ("2026-11-23", "勤労感謝の日"),
                ("2026-05-06", "振替休日"),
                ("2026-09-22", "国民の休日"),
            };
            foreach (var pin in pins)
            {
                CheckLabel("jp", pin.date, pin.name, "jp " + pin.date + " is " + pin.name);
            }
        }

        private static void UnitedStatesRules()
        {
            var pins = new (string date, string name)[]
            {
                ("2026-01-01", "元旦"),
                ("2026-01-19", "金恩日"),
                ("2026-02-16", "总统日"),
                ("2026-05-25", "阵亡日"),
                ("2026-06-19", "六月节"),
                ("2026-07-03", "独立日"),
                ("2026-09-07", "劳工节"),
                ("2026-10-12", "哥伦布日"),
                ("2026-11-11", "退伍日"),
                ("2026-11-26", "感恩节"),
                ("2026-12-25", "圣诞"),
            };
            foreach (var pin in pins)
            {
                CheckLabel("us", pin.date, pin.name, "us " + pin.date + " is " + pin.name);
            }
            CheckLabel("us", "2026-07-04", null,
                "us 2026-07-04 (Saturday) moved to the observed Friday");
        }

        private static void UnitedKingdomRules()
        {
            var pins = new (string date, string name)[]
            {
                ("2026-01-01", "元旦"),
                ("2026-04-03", "耶稣受难日"),
                ("2026-04-06", "复活节翌日"),
                ("2026-05-04", "五月假"),
                ("2026-05-25", "春假"),
                ("2026-08-31", "夏假"),
                ("2026-12-25", "圣诞"),
                ("2026-12-28", "节礼日"),
            };
            foreach (var pin in pins)
            {
                CheckLabel("uk", pin.date, pin.name, "uk " + pin.date + " is " + pin.name);
            }
            CheckLabel("uk", "2026-12-26", null,
                "uk 2026-12-26 (Saturday) moved to the substitute Monday");
            CheckLabel("uk", "2026-04-05", null, "uk Easter Sunday itself is not a bank holiday");

            CheckLabel("uk", "2022-12-26", "节礼日",
                "uk 2022-12-26 (Monday) keeps Boxing Day");
            CheckLabel("uk", "2022-12-27", "圣诞",
                "uk 2022-12-27 is the Christmas substitute");
            CheckLabel("uk", "2022-12-28", null,
                "uk 2022-12-28 needs no substitute");
            CheckLabel("uk", "2027-12-27", "圣诞",
                "uk 2027-12-27 is the Christmas substitute");
            CheckLabel("uk", "2027-12-28", "节礼日",
                "uk 2027-12-28 is the Boxing Day substitute");
            CheckLabel("uk", "2027-12-26", null,
                "uk 2027-12-26 (Sunday) moved to the substitute Tuesday");
        }

        private static void EdgeRobustness()
        {
            foreach (var region in new[] { "cn", "jp", "us", "uk", "none" })
            {
                bool threw = false;
                try
                {
                    PublicHolidays.GetLabel(region, DateTime.MinValue);
                    PublicHolidays.GetLabel(region, DateTime.MaxValue);
                }
                catch (Exception)
                {
                    threw = true;
                }
                Check(!threw, "region " + region + " labels the DateTime edges without throwing");
            }
        }
    }
}
