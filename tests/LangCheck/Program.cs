using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Kobold.Core;

namespace Kobold.LangCheck
{
    /// <summary>
    /// Sanity checks for the Localization data:
    /// 1. Supported languages are exactly en/zh/ja (no legacy tr).
    /// 2. Every language dictionary has exactly the same key set as en.
    /// 3. No value is empty and no value falls back to the raw key.
    /// 4. Keys with "{0}" placeholders survive Localization.Format in every language.
    /// Exit code 0 = all green; 1 = failures. Run via: dotnet run --project tests/LangCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            var stringsField = typeof(Localization).GetField("Strings",
                BindingFlags.NonPublic | BindingFlags.Static);
            var strings = (Dictionary<string, Dictionary<string, string>>)stringsField.GetValue(null);

            // 1. Exactly en/zh/ja
            var expectedLangs = new[] { "en", "zh", "ja" };
            var actualLangs = strings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            if (actualLangs.SequenceEqual(expectedLangs.OrderBy(k => k, StringComparer.Ordinal)))
                Console.WriteLine("PASS languages are exactly en/zh/ja");
            else
                Errors.Add("languages = " + string.Join(",", actualLangs) + " (expected en,zh,ja)");

            // 2-4. Parity per language
            foreach (var lang in expectedLangs)
            {
                if (!strings.ContainsKey(lang)) continue; // already reported above
                var dict = strings[lang];
                var en = strings["en"];

                var missing = en.Keys.Where(k => !dict.ContainsKey(k)).ToList();
                var extra = dict.Keys.Where(k => !en.ContainsKey(k)).ToList();
                var empty = dict.Where(kv => string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key).ToList();

                if (missing.Count == 0 && extra.Count == 0 && empty.Count == 0)
                    Console.WriteLine("PASS " + lang + ": " + dict.Count + " keys match en");
                else
                {
                    if (missing.Count > 0) Errors.Add(lang + " missing keys: " + string.Join(",", missing));
                    if (extra.Count > 0) Errors.Add(lang + " extra keys: " + string.Join(",", extra));
                    if (empty.Count > 0) Errors.Add(lang + " empty values: " + string.Join(",", empty));
                }

                // Get() must never return the raw key (means key lookup failed)
                Localization.SetLanguage(lang);
                foreach (var key in en.Keys)
                {
                    if (Localization.Get(key) == key)
                        Errors.Add(lang + "." + key + " resolved to raw key");
                }

                // Format() must work for all placeholder keys
                foreach (var kv in en.Where(kv => kv.Value.Contains("{0}")))
                {
                    try
                    {
                        Localization.Format(kv.Key, "test");
                    }
                    catch (Exception ex)
                    {
                        Errors.Add(lang + "." + kv.Key + " Format threw: " + ex.Message);
                    }
                }
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL LOCALIZATION CHECKS PASSED");
                return 0;
            }

            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }
    }
}
