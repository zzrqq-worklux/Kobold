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
            TodoSplitRoundTrips();
            TodoRepairNormalizes();
            WidgetKindRegistryChecks();
            RegisteredTypeDrivesTheLoad();
            FreshConfigGetsOneFolder();
            CorruptWidgetListsAreUnreadable();
            PanelSizeMigrationChecks();
        }

        private static void PanelSizeMigrationChecks()
        {
            LegacyFolderSizeMigrates();
            ExistingSizePairIsKept();
            MixedWidgetsEachMigrate();
            PartialSizeFillsTheMissingField();
            UnknownKindGetsNoSizes();
            DefaultConfigGetsSizes();
            LoadDoesNotForceASave();
            FactoryDefaults();
        }

        private static void LegacyFolderSizeMigrates()
        {
            string dir = NewCase("size-legacy");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""Folders"": [ { ""Id"": ""f1"", ""GridColumns"": 3, ""Items"": [ " +
                @"{ ""Name"": ""a"", ""Path"": ""C:\\a"" }, " +
                @"{ ""Name"": ""b"", ""Path"": ""C:\\b"" }, " +
                @"{ ""Name"": ""c"", ""Path"": ""C:\\c"" }, " +
                @"{ ""Name"": ""d"", ""Path"": ""C:\\d"" }, " +
                @"{ ""Name"": ""e"", ""Path"": ""C:\\e"" } ] } ] }");

            var cfg = AppConfig.Load(main, backup);
            var f1 = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as FolderData : null;
            Check(f1 != null && f1.PanelWidth == 270 && f1.PanelHeight == 252,
                "A: a legacy folder config migrates to (270, 252)");

            cfg.Save();
            var reloaded = AppConfig.Load(main, backup);
            var rf1 = reloaded.Widgets.Count == 1 ? reloaded.Widgets[0] as FolderData : null;
            Check(rf1 != null && rf1.PanelWidth == 270 && rf1.PanelHeight == 252,
                "A: the migrated sizes survive a save and reload");
        }

        private static void ExistingSizePairIsKept()
        {
            string dir = NewCase("size-existing");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""Widgets"": [ { ""Kind"": ""folder"", ""Id"": ""f1"", " +
                @"""GridColumns"": 3, ""PanelWidth"": 999, ""PanelHeight"": 888, ""Items"": [] } ] }");

            var cfg = AppConfig.Load(main, backup);
            var f1 = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as FolderData : null;
            Check(f1 != null && f1.PanelWidth == 999 && f1.PanelHeight == 888,
                "B: an existing size pair is not overwritten");

            cfg.Save();
            var reloaded = AppConfig.Load(main, backup);
            var rf1 = reloaded.Widgets.Count == 1 ? reloaded.Widgets[0] as FolderData : null;
            Check(rf1 != null && rf1.PanelWidth == 999 && rf1.PanelHeight == 888,
                "B: the kept size pair survives a save and reload");
        }

        private static void MixedWidgetsEachMigrate()
        {
            string dir = NewCase("size-mixed");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""Widgets"": [ " +
                @"{ ""Kind"": ""folder"", ""Id"": ""f1"", ""GridColumns"": 3, ""Items"": [] }, " +
                @"{ ""Kind"": ""todo"", ""Id"": ""t1"", ""SelectedDate"": ""2026-10-10"", ""Items"": [ " +
                @"{ ""Id"": ""i1"", ""Text"": ""done"", ""Date"": ""2026-10-10"", ""Done"": true, " +
                @"""CompletedDate"": ""2026-10-10"", " +
                @"""Children"": [ { ""Id"": ""c1"", ""Text"": ""sub"" } ] } ] } ] }");

            var cfg = AppConfig.Load(main, backup);
            var f1 = cfg.Widgets.Count == 2 ? cfg.Widgets[0] as FolderData : null;
            var t1 = cfg.Widgets.Count == 2 ? cfg.Widgets[1] as TodoData : null;
            Check(f1 != null && f1.PanelWidth == 270 && f1.PanelHeight == 154,
                "C: the empty 3-column folder gets (270, 154)");
            Check(t1 != null && t1.PanelWidth == 342 && t1.PanelHeight == 373,
                "C: the one-item October todo gets (342, 373)");
        }

        private static void PartialSizeFillsTheMissingField()
        {
            string dir = NewCase("size-partial");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""Widgets"": [ { ""Kind"": ""folder"", ""Id"": ""f1"", " +
                @"""GridColumns"": 3, ""PanelWidth"": 500, ""Items"": [] } ] }");

            var cfg = AppConfig.Load(main, backup);
            var f1 = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as FolderData : null;
            Check(f1 != null && f1.PanelWidth == 500, "D: an existing width is preserved");
            Check(f1 != null && f1.PanelHeight == 154, "D: only the missing height is filled");
        }

        private static void UnknownKindGetsNoSizes()
        {
            string dir = NewCase("size-unknown");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main,
                @"{ ""Widgets"": [ { ""Kind"": ""weather"", ""Id"": ""wx"", ""City"": ""Berlin"" } ] }");

            var cfg = AppConfig.Load(main, backup);
            var unknown = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as UnknownData : null;
            Check(unknown != null && unknown.PanelWidth == null && unknown.PanelHeight == null,
                "E: an unknown kind is given no size fields");

            cfg.Save();
            string savedText = File.ReadAllText(main);
            Check(!savedText.Contains("\"PanelWidth\""),
                "E: the unknown payload is written back without size fields");

            var reloaded = AppConfig.Load(main, backup);
            var raw = reloaded.Widgets.Count == 1 ? reloaded.Widgets[0] as UnknownData : null;
            Check(raw != null && (string)raw.Raw["City"] == "Berlin"
                  && raw.PanelWidth == null && raw.PanelHeight == null,
                "E: the unknown payload round-trips raw and sizeless");
        }

        private static void DefaultConfigGetsSizes()
        {
            string dir = NewCase("size-default");
            string main = Path.Combine(dir, "config.json");

            var cfg = AppConfig.Load(main, main + ".backup");
            var f1 = cfg.Widgets.Count == 1 ? cfg.Widgets[0] as FolderData : null;
            Check(f1 != null && f1.PanelWidth == 270 && f1.PanelHeight == 154,
                "F: the default folder gets (270, 154)");

            var root = JObject.Parse(File.ReadAllText(main));
            var widgets = root["Widgets"] as JArray;
            double? savedW = widgets != null && widgets.Count == 1
                ? widgets[0].Value<double?>("PanelWidth") : null;
            double? savedH = widgets != null && widgets.Count == 1
                ? widgets[0].Value<double?>("PanelHeight") : null;
            Check(savedW == 270 && savedH == 154,
                "F: the defaulted write persists the migrated sizes");
        }

        private static void LoadDoesNotForceASave()
        {
            string dir = NewCase("size-no-save");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""Folders"": [ { ""Id"": ""f1"", ""GridColumns"": 3, " +
                @"""Items"": [ { ""Name"": ""x"", ""Path"": ""C:\\x"" } ] } ] }");
            byte[] before = File.ReadAllBytes(main);

            var cfg = AppConfig.Load(main, backup);
            byte[] after = File.ReadAllBytes(main);
            Check(before.SequenceEqual(after),
                "G: loading an old config does not force a write");

            cfg.Save();
            var reloaded = AppConfig.Load(main, backup);
            var f1 = reloaded.Widgets.Count == 1 ? reloaded.Widgets[0] as FolderData : null;
            Check(f1 != null && f1.PanelWidth == 270 && f1.PanelHeight == 154,
                "G: after an explicit save the filled sizes survive a reload");
        }

        private static void FactoryDefaults()
        {
            var folder = new FolderData();
            PanelSizeMigration.FillMissingSizes(folder, 1.1, "en", new DateTime(2026, 10, 10));
            Check(folder.PanelWidth == 270 && folder.PanelHeight == 154,
                "H: a fresh folder defaults to (270, 154)");

            var todo = new TodoData { SelectedDate = new DateTime(2026, 10, 10) };
            PanelSizeMigration.FillMissingSizes(todo, 1.1, "en", new DateTime(2026, 10, 10));
            Check(todo.PanelWidth == 342 && todo.PanelHeight == 313,
                "H: a fresh todo defaults to (342, 313)");
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
            Check(savedText.Contains("\"Widgets\"") && savedText.Contains("\"Folders\": null"),
                "saving writes Widgets and an explicit Folders null");
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

        private static void TodoSplitRoundTrips()
        {
            var withSplit = new TodoData { CalendarCellHeight = 30 };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(withSplit);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<TodoData>(json);
            Check(back != null && back.CalendarCellHeight == 30,
                "the set calendar split survives a config round-trip");

            var without = new TodoData();
            string bareJson = Newtonsoft.Json.JsonConvert.SerializeObject(without);
            var bareBack = Newtonsoft.Json.JsonConvert.DeserializeObject<TodoData>(bareJson);
            Check(bareBack != null && bareBack.CalendarCellHeight == null,
                "an unset calendar split round-trips as null");
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

            Check(typeof(WidgetKind).GetField("SupportsGridColumns") == null
                  && typeof(WidgetKind).GetField("SupportsItemScale") == null
                  && typeof(WidgetKind).GetField("SupportsMaxRows") == null,
                "the retired layout flags are gone from WidgetKind");

            var defaultFolder = folder?.CreateDefault() as FolderData;
            Check(defaultFolder != null && defaultFolder.Name == Localization.Get("UI_DefaultFolderName")
                  && defaultFolder.Color == UiTokens.DefaultFolderColor,
                "the folder factory builds the default folder");
            Check(defaultFolder != null && !string.IsNullOrEmpty(defaultFolder.Id),
                "the folder factory gives the widget an id");

            var defaultTodo = todo?.CreateDefault() as TodoData;
            Check(defaultTodo != null && defaultTodo.Items != null && defaultTodo.Items.Count == 0
                  && defaultTodo.Kind == "todo", "the todo factory builds an empty todo");

            Check(folder != null && folder.DataType == typeof(FolderData),
                "the folder kind carries its data type");
            Check(todo != null && todo.DataType == typeof(TodoData),
                "the todo kind carries its data type");
        }

        private static void RegisteredTypeDrivesTheLoad()
        {
            string dir = NewCase("registered-type");
            string main = Path.Combine(dir, "config.json");

            File.WriteAllText(main, @"{ ""Widgets"": [ { ""Kind"": ""todo"", ""Id"": ""t1"", ""Name"": ""T"" } ] }");
            var cfg = AppConfig.Load(main, main + ".backup");

            Check(cfg.Widgets.Count == 1 && cfg.Widgets[0] is TodoData && cfg.Widgets[0].Kind == "todo",
                "the registered type drives the load of a todo");
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
            Check(savedText.Contains("\"Widgets\"") && savedText.Contains("\"Folders\": null"),
                "a fresh save writes Widgets and an explicit Folders null");
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
                Check(savedText.Contains("\"Widgets\"") && savedText.Contains("\"Folders\": null"),
                    label + ": the save writes the explicit Folders null");
                string backupText = File.ReadAllText(backup);
                Check(backupText.Contains("\"Folders\": [") && backupText.Contains("\"fromBackup\""),
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
