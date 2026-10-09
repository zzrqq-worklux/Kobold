namespace Kobold.Core
{
    /// <summary>
    /// The official mainland-China holiday arrangement (放假/调休), hand-written
    /// from the State Council notices and maintained by hand here:
    ///   2025: https://www.gov.cn/zhengce/zhengceku/202411/content_6986383.htm
    ///   2026: https://www.gov.cn/zhengce/zhengceku/202511/content_7047091.htm
    /// One row per date: "yyyy-MM-dd|display name|off|work", sorted by date, no
    /// duplicates. The announced years are transcribed verbatim; a work row is
    /// a make-up workday (调休上班). When a new year is announced it is a
    /// one-time manual addition - years without data simply fall back to the
    /// base calendar line, and nothing here is ever fetched or generated at
    /// runtime. Cross-checked 2026-10-08 against the MIT-licensed mirror
    /// NateScarlet/holiday-cn (2025.json / 2026.json).
    /// Display-name note: the official 2025 combined span is 国庆节、中秋节;
    /// the day cell shows the 4-character 国庆中秋 that fits its width.
    /// </summary>
    internal static class CnHolidayData
    {
        internal static readonly string[] Rows =
        {
            "2025-01-01|元旦|off",
            "2025-01-26|春节|work",
            "2025-01-28|春节|off",
            "2025-01-29|春节|off",
            "2025-01-30|春节|off",
            "2025-01-31|春节|off",
            "2025-02-01|春节|off",
            "2025-02-02|春节|off",
            "2025-02-03|春节|off",
            "2025-02-04|春节|off",
            "2025-02-08|春节|work",
            "2025-04-04|清明节|off",
            "2025-04-05|清明节|off",
            "2025-04-06|清明节|off",
            "2025-04-27|劳动节|work",
            "2025-05-01|劳动节|off",
            "2025-05-02|劳动节|off",
            "2025-05-03|劳动节|off",
            "2025-05-04|劳动节|off",
            "2025-05-05|劳动节|off",
            "2025-05-31|端午节|off",
            "2025-06-01|端午节|off",
            "2025-06-02|端午节|off",
            "2025-09-28|国庆中秋|work",
            "2025-10-01|国庆中秋|off",
            "2025-10-02|国庆中秋|off",
            "2025-10-03|国庆中秋|off",
            "2025-10-04|国庆中秋|off",
            "2025-10-05|国庆中秋|off",
            "2025-10-06|国庆中秋|off",
            "2025-10-07|国庆中秋|off",
            "2025-10-08|国庆中秋|off",
            "2025-10-11|国庆中秋|work",
            "2026-01-01|元旦|off",
            "2026-01-02|元旦|off",
            "2026-01-03|元旦|off",
            "2026-01-04|元旦|work",
            "2026-02-14|春节|work",
            "2026-02-15|春节|off",
            "2026-02-16|春节|off",
            "2026-02-17|春节|off",
            "2026-02-18|春节|off",
            "2026-02-19|春节|off",
            "2026-02-20|春节|off",
            "2026-02-21|春节|off",
            "2026-02-22|春节|off",
            "2026-02-23|春节|off",
            "2026-02-28|春节|work",
            "2026-04-04|清明节|off",
            "2026-04-05|清明节|off",
            "2026-04-06|清明节|off",
            "2026-05-01|劳动节|off",
            "2026-05-02|劳动节|off",
            "2026-05-03|劳动节|off",
            "2026-05-04|劳动节|off",
            "2026-05-05|劳动节|off",
            "2026-05-09|劳动节|work",
            "2026-06-19|端午节|off",
            "2026-06-20|端午节|off",
            "2026-06-21|端午节|off",
            "2026-09-20|国庆节|work",
            "2026-09-25|中秋节|off",
            "2026-09-26|中秋节|off",
            "2026-09-27|中秋节|off",
            "2026-10-01|国庆节|off",
            "2026-10-02|国庆节|off",
            "2026-10-03|国庆节|off",
            "2026-10-04|国庆节|off",
            "2026-10-05|国庆节|off",
            "2026-10-06|国庆节|off",
            "2026-10-07|国庆节|off",
            "2026-10-10|国庆节|work",
        };
    }
}
