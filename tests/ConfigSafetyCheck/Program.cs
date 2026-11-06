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
            Check(cfg2.Widgets.Count == 1, "both unreadable => default config with one folder");
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
            Check(cfg2b.Widgets.Count == 1 &&
                  Directory.GetFiles(dir2, "config.json.failed-*").Length == 1,
                "the rewritten main is used on the next start (no new evidence copies)");

            // --- Fresh install (no files at all): default config and a main file.

            string dir3 = NewCase("case3");
            string main3 = Path.Combine(dir3, "config.json");
            string backup3 = main3 + ".backup";
            var cfg3 = AppConfig.Load(main3, backup3);
            Check(cfg3.Widgets.Count == 1 && File.Exists(main3),
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

            // --- Storage pin desired state: default true, explicit false survives.

            Check(new AppConfig().PinStorageToQuickAccess,
                "a new config desires the storage pin (default true)");
            Check(cfg4.PinStorageToQuickAccess,
                "an old config without the storage-pin field defaults to the desired pin");

            string dir6 = NewCase("case6");
            string main6 = Path.Combine(dir6, "config.json");
            File.WriteAllText(main6, "{\"PinStorageToQuickAccess\":false}");
            var cfg6 = AppConfig.Load(main6, main6 + ".backup");
            Check(!cfg6.PinStorageToQuickAccess,
                "an explicit false for the storage pin survives the load");

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

            // --- Flush-then-save ordering: the note widget's close-time flush
            // splices its pending edit and then asks for a synchronous save
            // (WidgetManager.SaveConfigNow / SaveConfigIfLoaded). That save must
            // still land after BeginClosing, where the debounced path is gone -
            // this is the logoff ordering the note interaction upgrade needs.

            string dir10 = NewCase("case10");
            string main10 = Path.Combine(dir10, "config.json");
            var cfg10 = AppConfig.Load(main10, main10 + ".backup");
            cfg10.Language = "ja";
            cfg10.BeginClosing();
            cfg10.Language = "en"; // the edit flushed by the widget's Closing
            cfg10.Save();          // NoteWidget.Closing's SaveConfigIfLoaded
            Check(File.ReadAllText(main10).Contains("\"en\""),
                "a synchronous flush-then-save still lands after BeginClosing");

            cfg10.Language = "fr";
            cfg10.SaveDebounced(50);
            System.Threading.Thread.Sleep(300);
            Check(!File.ReadAllText(main10).Contains("\"fr\""),
                "the closing state still refuses debounced writes after that flush");

            // --- Saved configs carry an explicit "Folders": null: it sends
            // ≤1.3.0 down their evidence + default path, and our own reader
            // accepts it when the Widgets array is present.

            string dir9 = NewCase("case9");
            string main9 = Path.Combine(dir9, "config.json");
            string backup9 = main9 + ".backup";
            var cfg9 = AppConfig.Load(main9, backup9);
            Check(File.ReadAllText(main9).Contains("\"Folders\": null"),
                "a saved config carries the explicit Folders null");

            File.Delete(backup9);
            var cfg9b = AppConfig.Load(main9, backup9);
            Check(cfg9b.Widgets.Count == 1 && cfg9b.Widgets[0] is FolderData,
                "the saved config reloads from the main file alone");
            Check(Directory.GetFiles(dir9, "*.failed-*").Length == 0,
                "our own Folders null raises no evidence copies");

            // --- Note font scale survives an atomic save: the note's size step
            // lands in a complete main file and reads back after a reload.

            string dir11 = NewCase("case11");
            string main11 = Path.Combine(dir11, "config.json");
            var cfg11 = AppConfig.Load(main11, main11 + ".backup"); // valid: has bound paths
            cfg11.Widgets.Add(new NoteData { Id = "n1", FontScale = 1.2 });
            cfg11.Save();

            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(main11)) != null,
                "note font scale: the saved config is complete JSON");
            Check(!File.Exists(main11 + ".tmp") && !File.Exists(main11 + ".backup.tmp"),
                "note font scale: the atomic save leaves no temp file behind");

            var reloaded11 = AppConfig.Load(main11, main11 + ".backup");
            NoteData note11 = null;
            foreach (var widget in reloaded11.Widgets)
            {
                if (widget is NoteData found) { note11 = found; break; }
            }
            Check(note11 != null && Math.Abs(note11.FontScale - 1.2) < 1e-9,
                "note font scale: the saved scale reads back after a reload");

            RunWidgetColorMigrationChecks();
            RunSchemaVersionChecks();
            RunGroupConfigChecks();
        }

        /// <summary>
        /// Schema-version pins: a legacy config without the field loads as 0 and
        /// upgrades on save; a config from a newer build loads for display but
        /// refuses to save, leaving both files byte-identical; two saves rotate
        /// main = newest, backup = previous.
        /// </summary>
        private static void RunSchemaVersionChecks()
        {
            // --- Old JSON without SchemaVersion loads as 0, then saves as current.
            string dir = NewCase("schema-legacy");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";
            File.WriteAllText(main, "{\"Language\":\"ja\"}");

            var legacy = AppConfig.Load(main, backup);
            Check(legacy.SchemaVersion == 0,
                "schema: an old config without the field loads as version 0");

            legacy.Save();
            var legacyReload = AppConfig.Load(main, backup);
            Check(legacyReload.SchemaVersion == AppConfig.CurrentSchemaVersion,
                "schema: a saved legacy config reloads with the current version");
            Check(File.ReadAllText(main).Contains("\"SchemaVersion\": " + AppConfig.CurrentSchemaVersion),
                "schema: the saved main file records the current version");

            // --- A config from a newer build loads for display, but Save must
            //     refuse to touch either file.
            string dir2 = NewCase("schema-newer");
            string main2 = Path.Combine(dir2, "config.json");
            string backup2 = main2 + ".backup";
            int newerVersion = AppConfig.CurrentSchemaVersion + 1;
            File.WriteAllText(main2,
                "{\"SchemaVersion\":" + newerVersion + ",\"Language\":\"ja\"}");
            File.WriteAllText(backup2, "{\"Language\":\"en\",\"SchemaVersion\":1}");
            byte[] mainBytes = File.ReadAllBytes(main2);
            byte[] backupBytes = File.ReadAllBytes(backup2);

            var newer = AppConfig.Load(main2, backup2);
            Check(newer.Language == "ja" && newer.SchemaVersion == newerVersion,
                "schema: a newer config still loads its known fields for display");

            string reported = null;
            int failedCount = 0;
            Action<string> handler = msg =>
            {
                reported = msg;
                failedCount++;
            };
            AppConfig.SaveFailed += handler;
            try
            {
                // A pending debounced save must be cancelled by the refusal:
                // otherwise it fires later and reports the same refusal again.
                newer.SaveDebounced(300);
                newer.Language = "fr";
                newer.Save();

                Check(reported != null && reported.Contains("newer"),
                    "schema: saving a newer config fires SaveFailed with a clear message");
                Check(failedCount == 1,
                    "schema: the refused save reports once and stops pending saves");
                System.Threading.Thread.Sleep(900);
                Check(failedCount == 1,
                    "schema: a pending debounced save cannot fire after the refusal");
            }
            finally
            {
                AppConfig.SaveFailed -= handler;
            }

            Check(SameBytes(main2, mainBytes) && SameBytes(backup2, backupBytes),
                "schema: a refused save leaves main and backup byte-identical");

            // --- Two successful saves rotate the backup under a sandbox config.
            string dir3 = NewCase("schema-rotation");
            string main3 = Path.Combine(dir3, "config.json");
            string backup3 = main3 + ".backup";
            var rotation = AppConfig.Load(main3, backup3);
            rotation.Language = "ja";
            rotation.Save();
            rotation.Language = "en";
            rotation.Save();

            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(main3)).Language == "en",
                "schema rotation: the main file holds the newest save");
            Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(backup3)).Language == "ja",
                "schema rotation: the backup holds the previous save");
            Check(!File.Exists(main3 + ".tmp") && !File.Exists(backup3 + ".tmp"),
                "schema rotation: no temp file is left behind");
        }

        private static bool SameBytes(string path, byte[] expected)
        {
            if (!File.Exists(path)) return false;
            byte[] actual = File.ReadAllBytes(path);
            if (actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Group persistence: an old config stays readable and gains no Groups
        /// block, a broken membership block is repaired without losing widget
        /// data, and a corrupt Groups entry never sends the config down the
        /// unreadable/default path.
        /// </summary>
        private static void RunGroupConfigChecks()
        {
            // --- An old config without Groups loads and saves without one.
            string dir = NewCase("groups-old");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";
            File.WriteAllText(main,
                @"{ ""Language"": ""ja"", ""Widgets"": [ { ""Kind"": ""folder"", ""Id"": ""w1"", ""Items"": [] } ] }");

            var cfg = AppConfig.Load(main, backup);
            Check(cfg.Groups == null || cfg.Groups.Count == 0,
                "groups: an old config loads with no groups");
            cfg.Save();
            var saved = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(main));
            Check(saved["Groups"] == null, "groups: saving an old config writes no Groups block");
            Check(saved["Widgets"] is Newtonsoft.Json.Linq.JArray,
                "groups: the old config still saves its widgets");
            var reloaded = AppConfig.Load(main, backup);
            Check(reloaded.Widgets.Count == 1 && reloaded.Widgets[0].Id == "w1"
                  && (reloaded.Groups == null || reloaded.Groups.Count == 0),
                "groups: the saved old config stays loadable");

            // --- A 1-member group, an orphan and duplicate orders repair losslessly.
            string dir2 = NewCase("groups-repair");
            string main2 = Path.Combine(dir2, "config.json");
            string backup2 = main2 + ".backup";
            File.WriteAllText(main2, @"{
  ""Groups"": [
    { ""Id"": ""solo"", ""ActiveWidgetId"": ""s1"" },
    { ""Id"": ""pair"", ""ActiveWidgetId"": ""p1"" }
  ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""s1"", ""Name"": ""Solo"", ""GroupId"": ""solo"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""p1"", ""Name"": ""PairA"", ""GroupId"": ""pair"", ""GroupOrder"": 4, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""p2"", ""Name"": ""PairB"", ""GroupId"": ""pair"", ""GroupOrder"": 4, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""o1"", ""Name"": ""Orphan"", ""GroupId"": ""gone"", ""GroupOrder"": 2, ""Items"": [] }
  ]
}");

            var repaired = AppConfig.Load(main2, backup2);
            Check(repaired.Widgets.Count == 4, "groups: repairing keeps every widget");
            Check(repaired.Groups != null && repaired.Groups.Count == 1
                  && repaired.Groups[0].Id == "pair",
                "groups: the 1-member group is dropped and the pair survives");
            Check(FindWidget(repaired, "s1") != null && FindWidget(repaired, "s1").Name == "Solo"
                  && FindWidget(repaired, "s1").GroupId == null
                  && FindWidget(repaired, "s1").GroupOrder == null,
                "groups: the dissolved member keeps its data and loses its membership");
            Check(FindWidget(repaired, "o1") != null && FindWidget(repaired, "o1").Name == "Orphan"
                  && FindWidget(repaired, "o1").GroupId == null
                  && FindWidget(repaired, "o1").GroupOrder == null,
                "groups: the orphan keeps its data and loses its membership");
            Check(FindWidget(repaired, "p1") != null && FindWidget(repaired, "p1").GroupOrder == 0
                  && FindWidget(repaired, "p2") != null && FindWidget(repaired, "p2").GroupOrder == 1,
                "groups: duplicate orders normalize to 0..n-1");
            Check(Directory.GetFiles(dir2, "*.failed-*").Length == 0,
                "groups: repairing is not treated as an unreadable config");

            // --- A corrupt Groups entry does not make the config unreadable.
            string dir3 = NewCase("groups-corrupt-entry");
            string main3 = Path.Combine(dir3, "config.json");
            string backup3 = main3 + ".backup";
            File.WriteAllText(main3, @"{
  ""Groups"": [ null, { ""Id"": null }, { ""Id"": ""  "" } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""k1"", ""Name"": ""KeepA"", ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""k2"", ""Name"": ""KeepB"", ""Items"": [] }
  ]
}");

            var corrupt = AppConfig.Load(main3, backup3);
            Check(corrupt.Widgets.Count == 2 && corrupt.Widgets[0].Id == "k1"
                  && corrupt.Widgets[1].Id == "k2",
                "groups: a corrupt Groups entry keeps the widgets (no default fallback)");
            Check(corrupt.Widgets[0].Name == "KeepA" && corrupt.Widgets[1].Name == "KeepB",
                "groups: the corrupt Groups entry loses no widget data");
            Check(Directory.GetFiles(dir3, "*.failed-*").Length == 0,
                "groups: a corrupt Groups entry raises no evidence copy");
            Check(corrupt.Groups == null || corrupt.Groups.Count == 0,
                "groups: the corrupt Groups entries are dropped");

            // --- A wrong-typed Groups block is not a config failure: the
            // widgets must survive and no evidence copy may appear.
            string dir4 = NewCase("groups-wrong-type");
            string main4 = Path.Combine(dir4, "config.json");
            string backup4 = main4 + ".backup";
            File.WriteAllText(main4, @"{
  ""Groups"": ""oops"",
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""wt1"", ""Name"": ""WrongA"", ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""wt2"", ""Name"": ""WrongB"", ""Items"": [] }
  ]
}");

            var wrongType = AppConfig.Load(main4, backup4);
            Check(wrongType.Widgets.Count == 2
                  && FindWidget(wrongType, "wt1") != null && FindWidget(wrongType, "wt1").Name == "WrongA"
                  && FindWidget(wrongType, "wt2") != null && FindWidget(wrongType, "wt2").Name == "WrongB",
                "groups: a wrong-typed Groups block keeps both widgets");
            Check(Directory.GetFiles(dir4, "*.failed-*").Length == 0,
                "groups: a wrong-typed Groups block raises no evidence copy");
            Check(wrongType.Groups == null || wrongType.Groups.Count == 0,
                "groups: a wrong-typed Groups block loads no groups");

            // --- Junk entries (null, a number, a null id, a bad field type)
            // are skipped one by one: the widgets survive with no default
            // fallback and no evidence copy.
            string dir5 = NewCase("groups-junk-entries");
            string main5 = Path.Combine(dir5, "config.json");
            string backup5 = main5 + ".backup";
            File.WriteAllText(main5, @"{
  ""Groups"": [ null, 5, { ""Id"": null }, { ""Id"": ""g1"", ""PanelWidth"": ""wide"" } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""j1"", ""Name"": ""JunkA"", ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""j2"", ""Name"": ""JunkB"", ""Items"": [] }
  ]
}");

            var junk = AppConfig.Load(main5, backup5);
            Check(junk.Widgets.Count == 2
                  && FindWidget(junk, "j1") != null && FindWidget(junk, "j1").Name == "JunkA"
                  && FindWidget(junk, "j2") != null && FindWidget(junk, "j2").Name == "JunkB",
                "groups: junk entries keep the widgets (no default fallback)");
            Check(Directory.GetFiles(dir5, "*.failed-*").Length == 0,
                "groups: junk entries raise no evidence copy");
            Check(junk.Groups == null || junk.Groups.Count == 0,
                "groups: junk entries are all skipped");

            // --- One malformed entry next to a valid pair: the pair survives
            // and the malformed group's members fall back to plain widgets
            // with their data intact.
            string dir6 = NewCase("groups-mixed-entries");
            string main6 = Path.Combine(dir6, "config.json");
            string backup6 = main6 + ".backup";
            File.WriteAllText(main6, @"{
  ""Groups"": [
    { ""Id"": ""bad"", ""PanelWidth"": ""wide"" },
    { ""Id"": ""ok"", ""ActiveWidgetId"": ""a1"" }
  ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""a1"", ""Name"": ""PairA"", ""GroupId"": ""ok"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""a2"", ""Name"": ""PairB"", ""GroupId"": ""ok"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""b1"", ""Name"": ""BadA"", ""GroupId"": ""bad"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""b2"", ""Name"": ""BadB"", ""GroupId"": ""bad"", ""GroupOrder"": 1, ""Items"": [] }
  ]
}");

            var mixed = AppConfig.Load(main6, backup6);
            Check(mixed.Widgets.Count == 4, "groups: a malformed entry loses no widget");
            Check(mixed.Groups != null && mixed.Groups.Count == 1 && mixed.Groups[0].Id == "ok",
                "groups: the valid pair survives next to a malformed entry");
            Check(FindWidget(mixed, "a1") != null && FindWidget(mixed, "a1").GroupId == "ok"
                  && FindWidget(mixed, "a2") != null && FindWidget(mixed, "a2").GroupId == "ok"
                  && FindWidget(mixed, "a1").GroupOrder == 0 && FindWidget(mixed, "a2").GroupOrder == 1,
                "groups: the valid pair keeps its normalized tab order");
            Check(FindWidget(mixed, "b1") != null && FindWidget(mixed, "b1").Name == "BadA"
                  && FindWidget(mixed, "b1").GroupId == null && FindWidget(mixed, "b1").GroupOrder == null
                  && FindWidget(mixed, "b2") != null && FindWidget(mixed, "b2").Name == "BadB"
                  && FindWidget(mixed, "b2").GroupId == null && FindWidget(mixed, "b2").GroupOrder == null,
                "groups: the malformed group's members become plain widgets");
            Check(Directory.GetFiles(dir6, "*.failed-*").Length == 0,
                "groups: a malformed entry next to a valid pair raises no evidence copy");

            // --- A save round-trips a valid Groups array through the converter.
            string dir7 = NewCase("groups-roundtrip");
            string main7 = Path.Combine(dir7, "config.json");
            string backup7 = main7 + ".backup";
            var roundTrip = AppConfig.Load(main7, backup7);
            roundTrip.Widgets.Add(new NoteData { Id = "rt-n1", Name = "RT1", GroupId = "rt-g", GroupOrder = 0 });
            roundTrip.Widgets.Add(new NoteData { Id = "rt-n2", Name = "RT2", GroupId = "rt-g", GroupOrder = 1 });
            roundTrip.Groups = new List<WidgetGroup>
            {
                new WidgetGroup
                {
                    Id = "rt-g",
                    ActiveWidgetId = "rt-n2",
                    PanelWidth = 480,
                    PanelHeight = 360,
                    IsLocked = true
                }
            };
            roundTrip.Save();

            var savedRoot = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(main7));
            var savedGroups = savedRoot["Groups"] as Newtonsoft.Json.Linq.JArray;
            Check(savedGroups != null && savedGroups.Count == 1,
                "groups: a save writes a valid Groups array (converter round-trip)");
            Check(savedGroups != null && savedGroups.Count == 1
                  && (string)savedGroups[0]["Id"] == "rt-g"
                  && (double?)savedGroups[0]["PanelWidth"] == 480
                  && (bool?)savedGroups[0]["IsLocked"] == true,
                "groups: the saved array keeps the group fields");

            var roundTripReload = AppConfig.Load(main7, backup7);
            Check(roundTripReload.Groups != null && roundTripReload.Groups.Count == 1
                  && roundTripReload.Groups[0].Id == "rt-g"
                  && roundTripReload.Groups[0].ActiveWidgetId == "rt-n2"
                  && roundTripReload.Groups[0].PanelWidth == 480
                  && roundTripReload.Groups[0].PanelHeight == 360
                  && roundTripReload.Groups[0].IsLocked,
                "groups: the saved array reads back through the converter");
        }

        private static WidgetData FindWidget(AppConfig cfg, string id)
        {
            foreach (var widget in cfg.Widgets)
            {
                if (widget != null && widget.Id == id) return widget;
            }
            return null;
        }

        /// <summary>
        /// Widget colours written by the pre-Claude-Warm picker still carry the
        /// nine Tailwind hexes. A config load maps them index-for-index onto
        /// the current palette; custom picks and already-current colours are
        /// left alone. The mapping is idempotent.
        /// </summary>
        private static void RunWidgetColorMigrationChecks()
        {
            string[] legacy =
            {
                "#EF4444", "#F97316", "#F59E0B", "#84CC16", "#22C55E",
                "#06B6D4", "#3B82F6", "#8B5CF6", "#94A3B8"
            };
            for (int i = 0; i < legacy.Length; i++)
            {
                Check(string.Equals(WidgetColorMigration.Upgrade(legacy[i]), UiTokens.FolderPalette[i],
                        StringComparison.OrdinalIgnoreCase),
                    "legacy widget colour " + legacy[i] + " maps to " + UiTokens.FolderPalette[i]);
            }
            Check(WidgetColorMigration.Upgrade("#123456") == "#123456",
                "a custom widget colour is untouched");
            Check(WidgetColorMigration.Upgrade(UiTokens.FolderPalette[0]) == UiTokens.FolderPalette[0],
                "an already-current widget colour is untouched");

            string dir = NewCase("widget-colors");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";
            File.WriteAllText(main, @"{
  ""Language"": ""en"",
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""f"", ""Name"": ""F"", ""Color"": ""#EF4444"", ""Items"": [] },
    { ""Kind"": ""note"", ""Id"": ""n"", ""Name"": ""N"", ""Color"": ""#3B82F6"", ""Notes"": [] },
    { ""Kind"": ""todo"", ""Id"": ""t"", ""Name"": ""T"", ""Color"": ""#123456"", ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""f2"", ""Name"": ""F2"", ""Color"": ""#CC785C"", ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            Check(cfg.Widgets.Count == 4, "the color-migration config loads four widgets");
            Check(cfg.Widgets[0].Color == UiTokens.FolderPalette[0],
                "a legacy folder colour is migrated on load");
            Check(cfg.Widgets[1].Color == UiTokens.FolderPalette[6],
                "a legacy note colour is migrated on load");
            Check(cfg.Widgets[2].Color == "#123456",
                "a custom colour survives config load");
            Check(cfg.Widgets[3].Color == UiTokens.FolderPalette[0],
                "a current palette colour survives config load");

            var again = AppConfig.Load(main, backup);
            Check(again.Widgets[1].Color == UiTokens.FolderPalette[6],
                "the colour migration is idempotent");
        }
    }
}
