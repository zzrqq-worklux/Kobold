using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kobold.Core;
using Newtonsoft.Json.Linq;

namespace Kobold.WidgetKindsCheck
{
    /// <summary>
    /// Behaviour checks for the widget data model: the legacy Folders config
    /// migrates losslessly into Widgets, the Kind discriminator round-trips
    /// folders and todos, and unknown kinds keep their raw payload. Runs
    /// against throwaway temp directories. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/WidgetKindsCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            _root = Path.Combine(Path.GetTempPath(), "kobold-widgetkinds-" + Guid.NewGuid().ToString("N"));
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
                Console.WriteLine("ALL WIDGET-KINDS CHECKS PASSED");
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
            LegacyFoldersMigrate();
            MissingKindLoadsAsFolder();
            UnknownKindIsKeptVerbatim();
            WidgetsWinOverFolders();
            TodoRoundTrips();
            TodoHolidayRegionRoundTrips();
            TodoRepairNormalizes();
            WidgetKindRegistryChecks();
            FreshConfigGetsOneFolder();
            CorruptWidgetListsAreUnreadable();
        }

        private static void LegacyFoldersMigrate()
        {
            string dir = NewCase("legacy");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            string legacy = @"{ ""Folders"": [ " +
                @"{ ""Id"": ""f1"", ""Name"": ""A"", ""Color"": ""#EF4444"", ""GridColumns"": 3, " +
                @"""Items"": [ { ""Name"": ""x"", ""Path"": ""C:\\x"" } ] }, " +
                @"{ ""Id"": ""f2"", ""Name"": ""B"", ""Color"": ""#22C55E"", ""GridColumns"": 4 } ] }";
            File.WriteAllText(main, legacy);

            var cfg = AppConfig.Load(main, backup);
            Check(cfg.Widgets.Count == 2 && cfg.Widgets[0] is FolderData && cfg.Widgets[1] is FolderData,
                "legacy Folders migrate into Widgets");

            var f1 = cfg.Widgets[0] as FolderData;
            var f2 = cfg.Widgets[1] as FolderData;
            Check(f1 != null && f1.Id == "f1" && f2 != null && f2.Id == "f2",
                "migration keeps the ids and the order");
            Check(f1 != null && f1.Name == "A" && f1.Color == "#EF4444" && f1.GridColumns == 3
                  && f1.Items.Count == 1 && f1.Items[0].Name == "x" && f1.Items[0].Path == @"C:\x",
                "migration keeps the fields and items");
            Check(cfg.Widgets[0].Kind == "folder", "migrated folders carry Kind= folder");

            cfg.Save();
            string savedText = File.ReadAllText(main);
            Check(savedText.Contains("\"Widgets\"") && !savedText.Contains("\"Folders\""),
                "saving writes Widgets and no Folders");
            Check(savedText.Contains("\"Kind\": \"folder\""), "saved folders carry their Kind");

            var reloaded = AppConfig.Load(main, backup);
            var rf1 = reloaded.Widgets.Count == 2 ? reloaded.Widgets[0] as FolderData : null;
            Check(rf1 != null && rf1.Id == "f1" && rf1.Items.Count == 1 && rf1.Items[0].Name == "x",
                "the migrated config reloads unchanged");
        }

        private static void MissingKindLoadsAsFolder()
        {
            string dir = NewCase("no-kind");
            string main = Path.Combine(dir, "config.json");

            File.WriteAllText(main, @"{ ""Widgets"": [ { ""Id"": ""w1"", ""Name"": ""N"", ""Color"": ""#123456"" } ] }");
            var cfg = AppConfig.Load(main, main + ".backup");

            Check(cfg.Widgets.Count == 1 && cfg.Widgets[0] is FolderData,
                "a missing Kind loads as a folder");
            Check(cfg.Widgets[0].Id == "w1" && cfg.Widgets[0].Name == "N",
                "a missing Kind keeps the fields");
        }

        private static void UnknownKindIsKeptVerbatim()
        {
            string dir = NewCase("unknown-kind");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main,
                @"{ ""Widgets"": [ { ""Kind"": ""weather"", ""Id"": ""wx"", ""City"": ""Berlin"", ""Days"": [ 1, 2 ] } ] }");
            var cfg = AppConfig.Load(main, backup);

            Check(cfg.Widgets.Count == 1 && cfg.Widgets[0] is UnknownData,
                "an unknown Kind loads as UnknownData");

            var raw = (cfg.Widgets[0] as UnknownData)?.Raw;
            Check(raw != null && (string)raw["City"] == "Berlin" && ((JArray)raw["Days"]).Count == 2,
                "an unknown Kind keeps its raw payload");

            cfg.Save();
            string savedText = File.ReadAllText(main);
            Check(savedText.Contains("\"weather\"") && savedText.Contains("\"Berlin\""),
                "an unknown Kind is written back as it came in");

            var reloaded = AppConfig.Load(main, backup);
            Check(reloaded.Widgets.Count == 1 && reloaded.Widgets[0] is UnknownData,
                "an unknown Kind survives a round trip");
        }

        private static void WidgetsWinOverFolders()
        {
            string dir = NewCase("both");
            string main = Path.Combine(dir, "config.json");

            File.WriteAllText(main,
                @"{ ""Folders"": [ { ""Id"": ""legacy"" } ], " +
                @"""Widgets"": [ { ""Kind"": ""folder"", ""Id"": ""w1"" } ] }");
            var cfg = AppConfig.Load(main, main + ".backup");

            Check(cfg.Widgets.Count == 1 && cfg.Widgets[0].Id == "w1",
                "Widgets wins when both lists are present");

            cfg.Save();
            Check(!File.ReadAllText(main).Contains("\"legacy\""),
                "the ignored Folders list is dropped on save");
        }

        private static void TodoRoundTrips()
        {
            string dir = NewCase("todo");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main,
                @"{ ""Widgets"": [ { ""Kind"": ""todo"", ""Id"": ""t1"", ""Name"": ""Tasks"", " +
                @"""Items"": [ { ""Id"": ""i1"", ""Text"": ""parent"", " +
                @"""Date"": ""2026-09-25T13:45:00"", ""Done"": true, " +
                @"""CompletedDate"": ""2026-09-26T08:30:00"", " +
                @"""Children"": [ { ""Id"": ""c1"", ""Text"": ""child"", ""Done"": false } ] } ], " +
                @"""SelectedDate"": ""2026-09-27T00:00:00"" } ] }");
            var cfg = AppConfig.Load(main, backup);

            var todo = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as TodoData : null;
            Check(todo != null, "todo Kind loads as TodoData");
            Check(todo != null && todo.Items.Count == 1 && todo.Items[0].Text == "parent"
                  && todo.Items[0].Done, "todo items round-trip");
            Check(todo != null && todo.Items[0].Date == new DateTime(2026, 9, 25)
                  && todo.Items[0].CompletedDate == new DateTime(2026, 9, 26),
                "todo dates are normalized to whole days");
            Check(todo != null && todo.SelectedDate == new DateTime(2026, 9, 27),
                "SelectedDate round-trips");
            Check(todo != null && todo.Items[0].Children.Count == 1
                  && todo.Items[0].Children[0].Id == "c1" && todo.Items[0].Children[0].Text == "child"
                  && !todo.Items[0].Children[0].Done, "sub-todos round-trip (Id/Text/Done)");

            string[] childFields = typeof(SubTodoItem).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
            Check(string.Join(",", childFields) == "Done,Id,Text",
                "SubTodoItem cannot hold a third level (no Children field)");

            cfg.Save();
            string savedText = File.ReadAllText(main);
            Check(savedText.Contains("\"Kind\": \"todo\""), "a saved todo carries Kind= todo");

            var reloaded = AppConfig.Load(main, backup);
            var rt = reloaded.Widgets.Count == 1 ? reloaded.Widgets[0] as TodoData : null;
            Check(rt != null && rt.Items.Count == 1 && rt.Items[0].Children.Count == 1
                  && rt.Items[0].Date == new DateTime(2026, 9, 25),
                "a todo widget survives a full round trip");
        }

        private static void TodoHolidayRegionRoundTrips()
        {
            string dir = NewCase("todo-holiday-region");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main,
                @"{ ""Widgets"": [ { ""Kind"": ""todo"", ""Id"": ""t1"", ""HolidayRegion"": ""jp"" } ] }");
            var cfg = AppConfig.Load(main, backup);

            var todo = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as TodoData : null;
            Check(todo != null && todo.HolidayRegion == "jp",
                "HolidayRegion round-trips through the config");

            cfg.Save();
            var reloaded = AppConfig.Load(main, backup);
            var rt = reloaded.Widgets.Count == 1 ? reloaded.Widgets[0] as TodoData : null;
            Check(rt != null && rt.HolidayRegion == "jp", "HolidayRegion survives a save/reload");

            string missingDir = NewCase("todo-holiday-region-missing");
            string missingMain = Path.Combine(missingDir, "config.json");
            File.WriteAllText(missingMain,
                @"{ ""Widgets"": [ { ""Kind"": ""todo"", ""Id"": ""t2"" } ] }");
            var missingCfg = AppConfig.Load(missingMain, missingMain + ".backup");
            var missing = missingCfg.Widgets.Count == 1 ? missingCfg.Widgets[0] as TodoData : null;
            Check(missing != null && missing.HolidayRegion == null &&
                  PublicHolidays.NormalizeRegion(missing.HolidayRegion) == "cn",
                "a missing HolidayRegion normalizes to cn");
            Check(PublicHolidays.NormalizeRegion("unknown") == "cn",
                "an unknown HolidayRegion normalizes to cn");
        }

        private static void TodoRepairNormalizes()
        {
            string dir = NewCase("todo-repair");
            string main = Path.Combine(dir, "config.json");

            File.WriteAllText(main,
                @"{ ""Widgets"": [ { ""Kind"": ""todo"", ""Id"": ""t1"", ""Items"": [ " +
                @"{ ""Text"": null, ""Children"": null }, " +
                @"{ ""Id"": ""i2"", ""Text"": ""kept"", " +
                @"""Date"": ""2026-09-25T13:45:00"", ""CompletedDate"": ""2026-09-26T08:30:00"", " +
                @"""Children"": [ { ""Text"": null }, { ""Id"": ""c2"", ""Text"": ""  spaced  "" } ] } ], " +
                @"""SelectedDate"": ""2026-09-28T13:45:00"" } ] }");
            var cfg = AppConfig.Load(main, main + ".backup");

            var todo = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as TodoData : null;
            Check(todo != null && todo.Items.Count == 2,
                "a todo with null text/children still loads");

            Check(todo != null && todo.SelectedDate == new DateTime(2026, 9, 28),
                "SelectedDate is repaired to a whole day");

            var first = todo != null && todo.Items.Count > 0 ? todo.Items[0] : null;
            Check(first != null && !string.IsNullOrEmpty(first.Id), "a todo item missing its Id gets one");
            Check(first != null && first.Text == string.Empty, "a null todo text becomes empty");
            Check(first != null && first.Children != null && first.Children.Count == 0,
                "null children become an empty list");

            var second = todo != null && todo.Items.Count > 1 ? todo.Items[1] : null;
            Check(second != null && second.Date == new DateTime(2026, 9, 25)
                  && second.CompletedDate == new DateTime(2026, 9, 26),
                "repaired todo dates keep only the day");

            var sub = second != null && second.Children != null && second.Children.Count > 0
                ? second.Children[0] : null;
            Check(sub != null && !string.IsNullOrEmpty(sub.Id), "a sub-todo missing its Id gets one");
            Check(sub != null && sub.Text == string.Empty, "a null sub-todo text becomes empty");

            var kept = second != null && second.Children != null && second.Children.Count > 1
                ? second.Children[1] : null;
            Check(kept != null && kept.Text == "  spaced  ", "repair keeps whitespace around a text");
        }

        private static void WidgetKindRegistryChecks()
        {
            var folder = WidgetKindRegistry.Find("folder");
            var todo = WidgetKindRegistry.Find("todo");

            Check(WidgetKindRegistry.All.Count == 2 && folder != null && todo != null,
                "the registry holds folder and todo");
            Check(folder != null && folder.Id == "folder" && folder.NameKey == "UI_DefaultFolderName"
                  && folder.IconKey == "folder", "the folder kind carries its id, name key and icon key");
            Check(todo != null && todo.Id == "todo" && todo.NameKey == "UI_DefaultTodoName"
                  && todo.IconKey == "todo", "the todo kind carries its id, name key and icon key");
            Check(WidgetKindRegistry.Find("weather") == null, "an unknown kind id is not found");

            Check(folder != null && folder.SupportsGridColumns && folder.SupportsItemScale
                  && folder.SupportsMaxRows, "folder supports grid columns, item scale and max rows");
            Check(todo != null && !todo.SupportsGridColumns && !todo.SupportsItemScale
                  && !todo.SupportsMaxRows, "todo supports no folder layout options");

            var defaultFolder = folder?.CreateDefault() as FolderData;
            Check(defaultFolder != null && defaultFolder.Name == Localization.Get("UI_DefaultFolderName")
                  && defaultFolder.Color == UiTokens.DefaultFolderColor,
                "the folder factory builds the default folder");
            Check(defaultFolder != null && !string.IsNullOrEmpty(defaultFolder.Id),
                "the folder factory gives the widget an id");

            var defaultTodo = todo?.CreateDefault() as TodoData;
            Check(defaultTodo != null && defaultTodo.Items != null && defaultTodo.Items.Count == 0
                  && defaultTodo.Kind == "todo", "the todo factory builds an empty todo");
        }

        private static void FreshConfigGetsOneFolder()
        {
            string dir = NewCase("fresh");
            string main = Path.Combine(dir, "config.json");

            var cfg = AppConfig.Load(main, main + ".backup");
            Check(cfg.Widgets.Count == 1 && cfg.Widgets[0] is FolderData,
                "a fresh config gets one folder widget");
            Check(File.Exists(main), "a fresh config is written");

            string savedText = File.ReadAllText(main);
            Check(savedText.Contains("\"Widgets\"") && !savedText.Contains("\"Folders\""),
                "a fresh save writes Widgets and no Folders");
        }

        private static void CorruptWidgetListsAreUnreadable()
        {
            string[] corruptPayloads =
            {
                @"{ ""Folders"": null }",
                @"{ ""Folders"": [ { ""Items"": null } ] }",
                @"{ ""Folders"": [ null ] }",
                @"{ ""Widgets"": null }",
                @"{ ""Widgets"": [ { ""Kind"": ""folder"", ""Items"": null } ] }",
                @"{ ""Widgets"": [ { ""Kind"": ""todo"", ""Items"": null } ] }",
                @"{ ""Widgets"": [ null ] }"
            };

            for (int i = 0; i < corruptPayloads.Length; i++)
            {
                string label = "corrupt case " + i;

                string dir = NewCase("corrupt-default-" + i);
                string main = Path.Combine(dir, "config.json");
                File.WriteAllText(main, corruptPayloads[i]);

                var cfg = AppConfig.Load(main, main + ".backup");
                Check(cfg.Widgets.Count == 1 && cfg.Widgets[0] is FolderData && HasNoNullItems(cfg),
                    label + ": the corrupt list falls back to the default config");
                Check(Directory.GetFiles(dir, "config.json.failed-*").Length == 1,
                    label + ": the corrupt main is kept as evidence");

                dir = NewCase("corrupt-backup-" + i);
                main = Path.Combine(dir, "config.json");
                string backup = main + ".backup";
                File.WriteAllText(main, corruptPayloads[i]);
                File.WriteAllText(backup,
                    @"{ ""Folders"": [ { ""Id"": ""fromBackup"", ""Name"": ""B"", " +
                    @"""Items"": [ { ""Name"": ""x"", ""Path"": ""C:\\x"" } ] } ] }");

                cfg = AppConfig.Load(main, backup);
                var restored = cfg.Widgets.Count == 1 ? cfg.Widgets[0] : null;
                Check(restored != null && restored.Id == "fromBackup" && HasNoNullItems(cfg),
                    label + ": the good backup is restored");
                Check(Directory.GetFiles(dir, "config.json.failed-*").Length == 1,
                    label + ": the corrupt main still gets evidence when a backup exists");

                cfg.Save();
                string savedText = File.ReadAllText(main);
                Check(savedText.Contains("\"Widgets\"") && !savedText.Contains("\"Folders\""),
                    label + ": Folders never survives into a save");
                Check(File.ReadAllText(backup).Contains("\"Folders\""),
                    label + ": the recovery source is not clobbered by the first save");
                Check(HasNoNullItems(AppConfig.Load(main, backup)),
                    label + ": the saved config reloads without null lists");
            }
        }

        private static bool HasNoNullItems(AppConfig cfg)
        {
            if (cfg.Widgets == null) return false;

            foreach (var widget in cfg.Widgets)
            {
                if (widget == null) return false;
                if (widget is FolderData folder && folder.Items == null) return false;
                if (widget is TodoData todo && todo.Items == null) return false;
            }
            return true;
        }
    }
}
