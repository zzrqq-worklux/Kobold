using System;
using System.Collections.Generic;
using System.IO;
using Kobold.Core;
using Newtonsoft.Json;

namespace Kobold.ConfigSafetyCheck
{
    /// <summary>
    /// Behaviour checks for config persistence safety: a startup with an
    /// unreadable config must never silently destroy it, and recovering from the
    /// backup must not let the first save clobber the recovery source. Runs
    /// against throwaway temp directories. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/ConfigSafetyCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "kobold-configsafety-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try
            {
                RunAll();
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch { }
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL CONFIG-SAFETY CHECKS PASSED");
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

        private static string NewCase(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void RunAll()
        {
            // --- Unreadable main + valid backup: recover from backup, keep evidence.

            string dir1 = NewCase("case1");
            string main = Path.Combine(dir1, "config.json");
            string backup = main + ".backup";
            File.WriteAllText(main, "{ not json");
            File.WriteAllText(backup, "{\"Language\":\"zh\"}");

            var cfg = AppConfig.Load(main, backup);
            Check(cfg.Language == "zh", "falls back to backup when main is unreadable");
            Check(File.Exists(main) && File.ReadAllText(main) == "{ not json",
                "unreadable main is left untouched");
            Check(Directory.GetFiles(dir1, "config.json.failed-*").Length == 1,
                "unreadable main gets a timestamped evidence copy");

            // First save uses the recovered config but must not overwrite the backup.
            cfg.Save();
            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(backup)).Language == "zh",
                "backup (the recovery source) is not overwritten by the first save");
            Check(File.ReadAllText(main).Contains("\"zh\""),
                "main holds the recovered config after save");

            // Later saves roll the backup forward (it holds the previous generation).
            cfg.Language = "en";
            cfg.Save();
            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(main)).Language == "en",
                "the new value lands in the main file");
            cfg.Language = "ja";
            cfg.Save();
            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(backup)).Language == "en",
                "backup rolls forward once the recovery source has been confirmed");
            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(main)).Language == "ja",
                "the main file keeps the newest generation");

            // --- Both files unreadable: default config, evidence kept, backup not clobbered.

            string dir2 = NewCase("case2");
            string main2 = Path.Combine(dir2, "config.json");
            string backup2 = main2 + ".backup";
            File.WriteAllText(main2, "{ broken");
            File.WriteAllText(backup2, "also broken");

            var cfg2 = AppConfig.Load(main2, backup2);
            Check(cfg2.Folders.Count == 1, "both unreadable => default config with one folder");
            Check(Directory.GetFiles(dir2, "config.json.failed-*").Length == 1 &&
                  Directory.GetFiles(dir2, "config.json.backup.failed-*").Length == 1,
                "both unreadable files get evidence copies");
            Check(File.ReadAllText(backup2) == "also broken",
                "the default-config write does not clobber the unreadable backup");
            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(main2)) != null,
                "the default config is written to the main file");

            // After the default write the main file is valid again - the next
            // start uses it and produces no further evidence copies.
            var cfg2b = AppConfig.Load(main2, backup2);
            Check(cfg2b.Folders.Count == 1 &&
                  Directory.GetFiles(dir2, "config.json.failed-*").Length == 1,
                "the rewritten main is used on the next start (no new evidence copies)");

            // --- Fresh install (no files at all): default config and a main file.

            string dir3 = NewCase("case3");
            string main3 = Path.Combine(dir3, "config.json");
            string backup3 = main3 + ".backup";
            var cfg3 = AppConfig.Load(main3, backup3);
            Check(cfg3.Folders.Count == 1 && File.Exists(main3),
                "fresh install writes the default config");
            Check(Directory.GetFiles(dir3, "*.failed-*").Length == 0,
                "fresh install leaves no evidence copies");

            // --- Valid main is used as-is, without evidence files.

            string dir4 = NewCase("case4");
            string main4 = Path.Combine(dir4, "config.json");
            File.WriteAllText(main4, "{\"Language\":\"ja\"}");
            var cfg4 = AppConfig.Load(main4, main4 + ".backup");
            Check(cfg4.Language == "ja", "valid main is used as-is");
            Check(Directory.GetFiles(dir4, "*.failed-*").Length == 0,
                "no evidence copies when load succeeds");

            // --- Force-save cap: a debounce that keeps being pushed back still lands.

            string dir5 = NewCase("case5");
            string main5 = Path.Combine(dir5, "config.json");
            var cfg5 = AppConfig.Load(main5, main5 + ".backup");
            cfg5.ForceSaveIntervalMs = 150;
            cfg5.Language = "fr";
            cfg5.SaveDebounced(60000); // the idle debounce is far in the future

            bool forcedWrite = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.ReadAllText(main5).Contains("\"fr\"")) { forcedWrite = true; break; }
                }
                catch (IOException) { }
                System.Threading.Thread.Sleep(50);
            }
            Check(forcedWrite, "force-save cap writes while the idle debounce is pushed back");

            // --- Save failure is reported (and never silently swallowed).

            string dir7 = NewCase("case7");
            string main7 = Path.Combine(dir7, "config.json");
            var cfg7 = AppConfig.Load(main7, main7 + ".backup"); // valid: has bound paths

            // Turn the directory into a file so the next write cannot succeed.
            Directory.Delete(dir7, true);
            File.WriteAllText(dir7, "now a file");

            string reported = null;
            Action<string> handler = msg => reported = msg;
            AppConfig.SaveFailed += handler;
            try { cfg7.Save(); }
            finally { AppConfig.SaveFailed -= handler; }
            Check(reported != null, "SaveFailed is raised when the config cannot be written");

            // --- Session-end closing: one synchronous write, then no new debounced writes.

            string dir8 = NewCase("case8");
            string main8 = Path.Combine(dir8, "config.json");
            var cfg8 = AppConfig.Load(main8, main8 + ".backup");
            cfg8.Language = "ja";
            cfg8.BeginClosing();
            Check(File.ReadAllText(main8).Contains("\"ja\""),
                "BeginClosing writes synchronously before returning");

            cfg8.Language = "en";
            cfg8.SaveDebounced(50);
            System.Threading.Thread.Sleep(300);
            Check(!File.ReadAllText(main8).Contains("\"en\""),
                "no new debounced writes start after BeginClosing");
        }
    }
}
