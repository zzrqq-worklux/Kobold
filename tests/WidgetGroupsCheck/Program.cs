using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kobold.Controls;
using Kobold.Core;
using Newtonsoft.Json.Linq;

namespace Kobold.WidgetGroupsCheck
{
    /// <summary>
    /// Behaviour checks for the widget-group data model: the group block and
    /// the per-widget membership fields round-trip through the config, a
    /// broken block is repaired without losing widget data, RemoveWidget keeps
    /// groups consistent, and the startup policy only reopens open groups.
    /// Runs against throwaway temp directories. Exit code 0 = all green; 1 =
    /// failures.
    /// Run: dotnet run --project tests/WidgetGroupsCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            _root = Path.Combine(Path.GetTempPath(), "kobold-widgetgroups-" + Guid.NewGuid().ToString("N"));
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
                Console.WriteLine("ALL WIDGET-GROUPS CHECKS PASSED");
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
            GroupRoundTrip();
            OldConfigHasNoGroups();
            OrphanMembershipIsCleared();
            OneMemberGroupIsDissolved();
            ActiveFallbackIsFirstByOrder();
            OrderIsNormalized();
            InvalidGroupsAreDropped();
            RemoveWidgetKeepsTheGroup();
            RemoveWidgetDissolvesTheLastPair();
            RemoveWidgetLeavesOtherGroupsAlone();
            StartupPolicyRestoresExpandedGroups();
            EmptyGroupsAreNeverWritten();
            GroupHostPolicyPins();
            GroupedMembersAreNotStandalonePanels();
            GroupDragPolicyPins();
        }

        /// <summary>
        /// A fully configured group block survives Save -> Load unchanged:
        /// state, geometry, membership, order and the active member.
        /// </summary>
        private static void GroupRoundTrip()
        {
            string dir = NewCase("round-trip");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{
  ""Language"": ""en"",
  ""Groups"": [
    { ""Id"": ""g1"", ""ActiveWidgetId"": ""w2"",
      ""PanelX"": 120.5, ""PanelY"": 80.25, ""PanelDpiScale"": 1.5,
      ""PanelWidth"": 640.0, ""PanelHeight"": 480.0,
      ""IsExpanded"": true, ""IsLocked"": true, ""IsPanelPinned"": true },
    { ""Id"": ""g2"", ""ActiveWidgetId"": ""w3"",
      ""PanelX"": 10.0, ""PanelY"": 20.0, ""PanelDpiScale"": 1.0,
      ""PanelWidth"": 300.0, ""PanelHeight"": 200.0,
      ""IsExpanded"": false, ""IsLocked"": false, ""IsPanelPinned"": false }
  ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""w1"", ""Name"": ""A"", ""GroupId"": ""g1"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w2"", ""Name"": ""B"", ""GroupId"": ""g1"", ""GroupOrder"": 1, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w3"", ""Name"": ""C"", ""GroupId"": ""g2"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w4"", ""Name"": ""D"", ""GroupId"": ""g2"", ""GroupOrder"": 1, ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            var g1 = Find(cfg.Groups, "g1");
            var g2 = Find(cfg.Groups, "g2");

            Check(cfg.Groups != null && cfg.Groups.Count == 2, "round-trip: two groups load");
            Check(g1 != null && g1.ActiveWidgetId == "w2" && g1.IsExpanded && g1.IsLocked
                  && g1.IsPanelPinned, "round-trip: the group state fields load");
            Check(g1 != null && Nearly(g1.PanelX, 120.5) && Nearly(g1.PanelY, 80.25)
                  && Nearly(g1.PanelDpiScale, 1.5) && Nearly(g1.PanelWidth, 640)
                  && Nearly(g1.PanelHeight, 480), "round-trip: the nullable geometry loads");
            Check(g2 != null && g2.ActiveWidgetId == "w3" && !g2.IsExpanded && !g2.IsLocked
                  && !g2.IsPanelPinned, "round-trip: a collapsed, unlocked group loads");
            Check(Membership(cfg, "w1") == "g1:0" && Membership(cfg, "w2") == "g1:1"
                  && Membership(cfg, "w3") == "g2:0" && Membership(cfg, "w4") == "g2:1",
                "round-trip: the membership fields load");

            cfg.Save();
            var root = JObject.Parse(File.ReadAllText(main));
            Check(root["Groups"] is JArray && ((JArray)root["Groups"]).Count == 2,
                "round-trip: the saved config writes the Groups block");
            var savedW1 = FindSavedWidget(root, "w1");
            Check(savedW1 != null && (string)savedW1["GroupId"] == "g1"
                  && (int?)savedW1["GroupOrder"] == 0,
                "round-trip: the saved widget carries GroupId and GroupOrder");

            var reloaded = AppConfig.Load(main, backup);
            var rg1 = Find(reloaded.Groups, "g1");
            Check(reloaded.Groups != null && reloaded.Groups.Count == 2,
                "round-trip: the group block survives a save and reload");
            Check(rg1 != null && rg1.ActiveWidgetId == "w2" && rg1.IsExpanded && rg1.IsLocked
                  && rg1.IsPanelPinned && Nearly(rg1.PanelX, 120.5)
                  && Nearly(rg1.PanelWidth, 640) && Nearly(rg1.PanelHeight, 480),
                "round-trip: the group state survives a save and reload");
            Check(Membership(reloaded, "w1") == "g1:0" && Membership(reloaded, "w2") == "g1:1"
                  && Membership(reloaded, "w3") == "g2:0" && Membership(reloaded, "w4") == "g2:1",
                "round-trip: the membership survives a save and reload");
        }

        /// <summary>
        /// A config written before groups existed loads with no groups and
        /// saves without a Groups key; everything else is untouched.
        /// </summary>
        private static void OldConfigHasNoGroups()
        {
            string dir = NewCase("old-config");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{ ""Language"": ""ja"", ""Widgets"": [ " +
                @"{ ""Kind"": ""folder"", ""Id"": ""w1"", ""Name"": ""Old"", ""Items"": [] } ] }");

            var cfg = AppConfig.Load(main, backup);
            Check(cfg.Groups == null || cfg.Groups.Count == 0,
                "old: a config without Groups loads with no groups");

            cfg.Save();
            var root = JObject.Parse(File.ReadAllText(main));
            Check(root["Groups"] == null, "old: saving writes no Groups block");
            Check((string)root["Language"] == "ja", "old: other fields survive the save");

            var reloaded = AppConfig.Load(main, backup);
            Check(reloaded.Widgets.Count == 1 && reloaded.Widgets[0].Id == "w1"
                  && reloaded.Widgets[0].Name == "Old", "old: the widget list survives the save");
            Check(reloaded.Groups == null || reloaded.Groups.Count == 0,
                "old: the saved config still loads with no groups");
        }

        /// <summary>A membership pointing at no group is cleared, data kept.</summary>
        private static void OrphanMembershipIsCleared()
        {
            string dir = NewCase("orphan");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{
  ""Groups"": [ { ""Id"": ""g1"", ""ActiveWidgetId"": ""a1"" } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""a1"", ""GroupId"": ""g1"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""a2"", ""GroupId"": ""g1"", ""GroupOrder"": 1, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""orphan"", ""Name"": ""Kept"",
      ""GroupId"": ""missing"", ""GroupOrder"": 7, ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            var orphan = Widget(cfg, "orphan");
            Check(cfg.Groups != null && cfg.Groups.Count == 1,
                "orphan: the valid group survives");
            Check(orphan != null && orphan.GroupId == null && orphan.GroupOrder == null,
                "orphan: both membership fields are cleared");
            Check(orphan != null && orphan.Name == "Kept", "orphan: the widget data is kept");
            Check(Membership(cfg, "a1") == "g1:0" && Membership(cfg, "a2") == "g1:1",
                "orphan: the valid group keeps its members");
        }

        /// <summary>A group that would show a single tab is dissolved.</summary>
        private static void OneMemberGroupIsDissolved()
        {
            string dir = NewCase("dissolve");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{
  ""Groups"": [ { ""Id"": ""solo"", ""ActiveWidgetId"": ""s1"" } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""s1"", ""Name"": ""Only"",
      ""GroupId"": ""solo"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""free"", ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            var s1 = Widget(cfg, "s1");
            Check(cfg.Groups == null || cfg.Groups.Count == 0,
                "dissolve: the 1-member group is dropped");
            Check(s1 != null && s1.GroupId == null && s1.GroupOrder == null,
                "dissolve: the last member loses both membership fields");
            Check(cfg.Widgets.Count == 2 && s1 != null && s1.Name == "Only",
                "dissolve: no widget data is lost");
        }

        /// <summary>An unknown/absent active id falls back to the first member by order.</summary>
        private static void ActiveFallbackIsFirstByOrder()
        {
            string dir = NewCase("active-fallback");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{
  ""Groups"": [
    { ""Id"": ""g1"", ""ActiveWidgetId"": ""ghost"" },
    { ""Id"": ""g2"" }
  ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""w1"", ""GroupId"": ""g1"", ""GroupOrder"": 5, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w2"", ""GroupId"": ""g1"", ""GroupOrder"": -1, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w3"", ""GroupId"": ""g2"", ""GroupOrder"": 4, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w4"", ""GroupId"": ""g2"", ""GroupOrder"": 1, ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            var g1 = Find(cfg.Groups, "g1");
            var g2 = Find(cfg.Groups, "g2");
            Check(g1 != null && g1.ActiveWidgetId == "w2",
                "active: an unknown active id falls back to the first by normalized order");
            Check(g2 != null && g2.ActiveWidgetId == "w4",
                "active: an absent active id falls back to the first by normalized order");
        }

        /// <summary>Duplicate, missing and negative orders become 0..n-1.</summary>
        private static void OrderIsNormalized()
        {
            string dir = NewCase("order");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{
  ""Groups"": [ { ""Id"": ""g1"", ""ActiveWidgetId"": ""a"" } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""a"", ""GroupId"": ""g1"", ""GroupOrder"": 2, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""b"", ""GroupId"": ""g1"", ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""c"", ""GroupId"": ""g1"", ""GroupOrder"": 1, ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            Check(Membership(cfg, "c") == "g1:0" && Membership(cfg, "a") == "g1:1"
                  && Membership(cfg, "b") == "g1:2",
                "order: a missing order sorts last, duplicates/negatives normalize to 0..n-1");

            string dir2 = NewCase("order-ties");
            string main2 = Path.Combine(dir2, "config.json");
            string backup2 = main2 + ".backup";

            File.WriteAllText(main2, @"{
  ""Groups"": [ { ""Id"": ""g1"", ""ActiveWidgetId"": ""d"" } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""d"", ""GroupId"": ""g1"", ""GroupOrder"": 5, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""e"", ""GroupId"": ""g1"", ""GroupOrder"": 5, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""f"", ""GroupId"": ""g1"", ""GroupOrder"": 5, ""Items"": [] }
  ]
}");

            var tied = AppConfig.Load(main2, backup2);
            Check(Membership(tied, "d") == "g1:0" && Membership(tied, "e") == "g1:1"
                  && Membership(tied, "f") == "g1:2",
                "order: equal orders keep the config order");
        }

        /// <summary>
        /// Duplicate ids keep the first group; a blank/null id is no group at
        /// all. Nothing throws and no widget data is lost.
        /// </summary>
        private static void InvalidGroupsAreDropped()
        {
            string dir = NewCase("invalid-groups");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{
  ""Groups"": [
    { ""Id"": ""g1"", ""ActiveWidgetId"": ""w1"" },
    { ""Id"": ""g1"", ""ActiveWidgetId"": ""w2"" },
    { ""Id"": null },
    { ""Id"": ""   "" }
  ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""w1"", ""Name"": ""One"", ""GroupId"": ""g1"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w2"", ""Name"": ""Two"", ""GroupId"": ""g1"", ""GroupOrder"": 1, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w3"", ""Name"": ""Three"", ""GroupId"": """", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w4"", ""Name"": ""Four"", ""GroupId"": ""   "", ""GroupOrder"": 1, ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            Check(cfg.Groups != null && cfg.Groups.Count == 1 && cfg.Groups[0].Id == "g1",
                "invalid: only the first valid group survives");
            Check(cfg.Groups != null && cfg.Groups[0].ActiveWidgetId == "w1",
                "invalid: the first group's state wins");
            Check(cfg.Widgets.Count == 4, "invalid: every widget survives");
            Check(Widget(cfg, "w3") != null && Widget(cfg, "w3").GroupId == null
                  && Widget(cfg, "w3").GroupOrder == null,
                "invalid: a widget of the null-id group becomes an orphan");
            Check(Widget(cfg, "w4") != null && Widget(cfg, "w4").GroupId == null
                  && Widget(cfg, "w4").GroupOrder == null,
                "invalid: a widget of the blank-id group becomes an orphan");
            Check(Widget(cfg, "w2") != null && Widget(cfg, "w2").Name == "Two"
                  && Membership(cfg, "w2") == "g1:1",
                "invalid: the duplicate group's member keeps the surviving group");
        }

        /// <summary>Removing one of three keeps the group and fixes the active id.</summary>
        private static void RemoveWidgetKeepsTheGroup()
        {
            string dir = NewCase("remove-keep");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            WriteThreeMemberGroup(main, "w2");

            var cfg = AppConfig.Load(main, backup);
            cfg.RemoveWidget("w2");
            cfg.Save(); // flush the debounce so the temp directory can be deleted

            var group = cfg.Groups != null && cfg.Groups.Count == 1 ? cfg.Groups[0] : null;
            Check(cfg.Widgets.Count == 2 && Widget(cfg, "w2") == null,
                "remove: the widget is removed");
            Check(group != null && group.Id == "g1", "remove: the pair keeps the group");
            Check(group != null && group.ActiveWidgetId == "w1",
                "remove: removing the active member picks the first survivor");
            Check(Membership(cfg, "w1") == "g1:0" && Membership(cfg, "w3") == "g1:1",
                "remove: the surviving members keep a normalized order");

            var reloaded = AppConfig.Load(main, backup);
            Check(reloaded.Groups != null && reloaded.Groups.Count == 1
                  && reloaded.Groups[0].ActiveWidgetId == "w1",
                "remove: the cleaned group survives a reload");
        }

        /// <summary>Removing down to one member dissolves the group.</summary>
        private static void RemoveWidgetDissolvesTheLastPair()
        {
            string dir = NewCase("remove-dissolve");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            WriteThreeMemberGroup(main, "w1");

            var cfg = AppConfig.Load(main, backup);
            cfg.RemoveWidget("w2");
            cfg.RemoveWidget("w3");
            cfg.Save();

            Check(cfg.Widgets.Count == 1 && Widget(cfg, "w1") != null,
                "remove: the last member is kept");
            Check(cfg.Groups == null || cfg.Groups.Count == 0,
                "remove: removing down to one dissolves the group");
            Check(Membership(cfg, "w1") == "<null>:<null>",
                "remove: the last member loses both membership fields");

            cfg.Save();
            Check(!File.ReadAllText(main).Contains("\"Groups\""),
                "remove: the dissolved group leaves no Groups block");
        }

        /// <summary>Removing an ungrouped widget never touches a group.</summary>
        private static void RemoveWidgetLeavesOtherGroupsAlone()
        {
            string dir = NewCase("remove-other");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            File.WriteAllText(main, @"{
  ""Groups"": [ { ""Id"": ""g1"", ""ActiveWidgetId"": ""w2"" } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""w1"", ""GroupId"": ""g1"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w2"", ""GroupId"": ""g1"", ""GroupOrder"": 1, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""free"", ""Items"": [] }
  ]
}");

            var cfg = AppConfig.Load(main, backup);
            cfg.RemoveWidget("free");
            cfg.Save();

            var group = cfg.Groups != null && cfg.Groups.Count == 1 ? cfg.Groups[0] : null;
            Check(group != null && group.ActiveWidgetId == "w2",
                "remove: removing an ungrouped widget leaves the group alone");
            Check(cfg.Widgets.Count == 2 && Membership(cfg, "w1") == "g1:0"
                  && Membership(cfg, "w2") == "g1:1",
                "remove: the group members are untouched");
        }

        /// <summary>
        /// The startup policy restores expanded groups with at least two
        /// members, ordered by their first member in the widget list.
        /// </summary>
        private static void StartupPolicyRestoresExpandedGroups()
        {
            var groups = new List<WidgetGroup>
            {
                new WidgetGroup { Id = "gOpen2", IsExpanded = true },
                new WidgetGroup { Id = "gClosed", IsExpanded = false },
                new WidgetGroup { Id = "gSolo", IsExpanded = true },
                new WidgetGroup { Id = "gLate", IsExpanded = true },
                new WidgetGroup { Id = "gEmpty", IsExpanded = true }
            };
            var widgets = new List<WidgetData>
            {
                new FolderData { Id = "a", GroupId = "gLate", GroupOrder = 0 },
                new FolderData { Id = "b", GroupId = "gOpen2", GroupOrder = 0 },
                new FolderData { Id = "c", GroupId = "gClosed", GroupOrder = 0 },
                new FolderData { Id = "d", GroupId = "gSolo", GroupOrder = 0 },
                new FolderData { Id = "e", GroupId = "gClosed", GroupOrder = 1 },
                new FolderData { Id = "f", GroupId = "gOpen2", GroupOrder = 1 },
                new FolderData { Id = "g", GroupId = "gLate", GroupOrder = 1 },
                new FolderData { Id = "h" }
            };

            var restore = GroupStartupPolicy.ToRestoreGroups(groups, widgets);
            Check(restore.Count == 2 && restore[0].Id == "gLate" && restore[1].Id == "gOpen2",
                "policy: only open groups with two members, in first-member config order");

            Check(GroupStartupPolicy.ToRestoreGroups(null, null).Count == 0,
                "policy: null inputs give an empty list");
            Check(GroupStartupPolicy.ToRestoreGroups(groups, null).Count == 0,
                "policy: null widgets give an empty list");
            Check(GroupStartupPolicy.ToRestoreGroups(null, widgets).Count == 0,
                "policy: null groups give an empty list");
            Check(GroupStartupPolicy.ToRestoreGroups(new List<WidgetGroup>(),
                    new List<WidgetData>()).Count == 0,
                "policy: empty inputs give an empty list");
        }

        /// <summary>An empty or null group list is never written.</summary>
        private static void EmptyGroupsAreNeverWritten()
        {
            string dir = NewCase("empty-groups");
            string main = Path.Combine(dir, "config.json");
            string backup = main + ".backup";

            var cfg = AppConfig.Load(main, backup);

            cfg.Groups = new List<WidgetGroup>();
            cfg.Save();
            Check(!File.ReadAllText(main).Contains("\"Groups\""),
                "save: an empty group list writes no Groups block");

            cfg.Groups = null;
            cfg.Save();
            Check(!File.ReadAllText(main).Contains("\"Groups\""),
                "save: a null group list writes no Groups block");

            cfg.Groups = new List<WidgetGroup> { new WidgetGroup { Id = "g1" } };
            cfg.Save();
            Check(File.ReadAllText(main).Contains("\"Groups\""),
                "save: a configured group list is written");
        }

        /// <summary>
        /// T5 host policy: membership lookup, member order, the active member
        /// projection and the island's open-tile projection. Every helper is
        /// pure and null-safe.
        /// </summary>
        private static void GroupHostPolicyPins()
        {
            var groups = new List<WidgetGroup>
            {
                new WidgetGroup { Id = "g1", ActiveWidgetId = "a2" },
                new WidgetGroup { Id = "g2", ActiveWidgetId = "ghost" }
            };
            var widgets = new List<WidgetData>
            {
                new FolderData { Id = "solo" },
                new FolderData { Id = "a1", GroupId = "g1", GroupOrder = 1 },
                new FolderData { Id = "a2", GroupId = "g1", GroupOrder = 0 },
                new FolderData { Id = "b1", GroupId = "g2", GroupOrder = 1 },
                new FolderData { Id = "b2", GroupId = "g2", GroupOrder = 0 }
            };

            var members = GroupHostPolicy.MembersOf(groups[0], widgets);
            Check(members.Count == 2 && members[0].Id == "a2" && members[1].Id == "a1",
                "host: MembersOf orders by GroupOrder, then config order");

            Check(GroupHostPolicy.IndexOfMember(groups[0], widgets, "a1") == 1
                  && GroupHostPolicy.IndexOfMember(groups[0], widgets, "solo") == -1
                  && GroupHostPolicy.IndexOfMember(groups[0], widgets, null) == -1,
                "host: IndexOfMember finds a member and rejects outsiders");

            Check(ReferenceEquals(GroupHostPolicy.FindGroupForWidget(groups, widgets, "a1"), groups[0])
                  && ReferenceEquals(GroupHostPolicy.FindGroupForWidget(groups, widgets, "a2"), groups[0])
                  && ReferenceEquals(GroupHostPolicy.FindGroupForWidget(groups, widgets, "b2"), groups[1]),
                "host: both members of a group resolve to the same group");
            Check(GroupHostPolicy.FindGroupForWidget(groups, widgets, "solo") == null
                  && GroupHostPolicy.FindGroupForWidget(groups, widgets, "missing") == null,
                "host: an ungrouped/unknown widget resolves to no group");

            Check(GroupHostPolicy.ActiveMemberId(groups[0], widgets) == "a2",
                "host: a valid active id is kept");
            Check(GroupHostPolicy.ActiveMemberId(groups[1], widgets) == "b2",
                "host: an unknown active id falls back to the first member");
            Check(GroupHostPolicy.ActiveMemberId(null, widgets) == null
                  && GroupHostPolicy.ActiveMemberId(groups[0], null) == null,
                "host: a null group/widget list has no active member");

            var open = new HashSet<string>(StringComparer.Ordinal);
            Func<string, bool> isOpen = id => open.Contains(id);

            var tiles = GroupHostPolicy.OpenTileIds(groups, widgets, isOpen);
            Check(tiles.Count == 0, "host: closed groups and closed widgets mark no tile");

            open.Add("g1");
            tiles = GroupHostPolicy.OpenTileIds(groups, widgets, isOpen);
            Check(tiles.Count == 1 && tiles[0] == "a2",
                "host: an open group marks only its active member");

            open.Add("solo");
            tiles = GroupHostPolicy.OpenTileIds(groups, widgets, isOpen);
            Check(tiles.Count == 2 && tiles.Contains("a2") && tiles.Contains("solo"),
                "host: an open ungrouped widget keeps its own id");

            open.Remove("g1");
            tiles = GroupHostPolicy.OpenTileIds(groups, widgets, isOpen);
            Check(tiles.Count == 1 && tiles[0] == "solo",
                "host: closing the group drops the active member ring");

            Check(GroupHostPolicy.MembersOf(null, widgets).Count == 0
                  && GroupHostPolicy.MembersOf(groups[0], null).Count == 0
                  && GroupHostPolicy.OpenTileIds(null, null, null).Count == 0,
                "host: null inputs give empty results");
        }

        /// <summary>A grouped member's own IsExpanded never restores a standalone panel.</summary>
        private static void GroupedMembersAreNotStandalonePanels()
        {
            var widgets = new List<WidgetData>
            {
                new FolderData { Id = "grouped-a", GroupId = "g1", GroupOrder = 0, IsExpanded = true },
                new FolderData { Id = "grouped-b", GroupId = "g1", GroupOrder = 1, IsExpanded = true },
                new FolderData { Id = "free", IsExpanded = true }
            };

            var restore = StartupPanels.ToRestore(widgets);
            Check(restore.Count == 1 && restore[0].Id == "free",
                "startup: grouped members are not restored as standalone panels");
        }

        /// <summary>
        /// T6 pure drag/merge policy: the 12 DIP drag-out threshold, the
        /// one-member dissolve rule, the append index and the merge guards
        /// (same/null, group source, locks, Folder). Every input is a plain
        /// datum or a fake window, so no WPF runs here. The screen hit test
        /// needs <see cref="System.Windows.Point"/> and lives in XamlLoadCheck,
        /// whose project references WindowsBase.
        /// </summary>
        private static void GroupDragPolicyPins()
        {
            Check(!GroupDragPolicy.IsDragOut(0, 0),
                "drag: no movement is not a drag-out");
            Check(!GroupDragPolicy.IsDragOut(11.99, 0) && !GroupDragPolicy.IsDragOut(0, -11.99),
                "drag: below the 12 DIP threshold is not a drag-out");
            Check(GroupDragPolicy.IsDragOut(12, 0) && GroupDragPolicy.IsDragOut(0, 12)
                  && GroupDragPolicy.IsDragOut(-12, 0) && GroupDragPolicy.IsDragOut(0, -12),
                "drag: at/above the threshold is a drag-out in every direction");
            Check(GroupDragPolicy.IsDragOut(40, -25),
                "drag: a long diagonal movement is a drag-out");
            Check(!GroupDragPolicy.IsDragOut(20, 20, 50) && GroupDragPolicy.IsDragOut(50, 0, 50),
                "drag: an explicit threshold is inclusive and overridable");

            Check(GroupDragPolicy.ShouldDissolve(0) && GroupDragPolicy.ShouldDissolve(1),
                "dissolve: zero or one member dissolves the group");
            Check(!GroupDragPolicy.ShouldDissolve(2) && !GroupDragPolicy.ShouldDissolve(3),
                "dissolve: two or more members keep the group");

            Check(GroupDragPolicy.InsertIndex(0) == 0 && GroupDragPolicy.InsertIndex(2) == 2
                  && GroupDragPolicy.InsertIndex(3) == 3,
                "insert: a merged source lands after the last target tab");
            Check(GroupDragPolicy.InsertIndex(-4) == 0,
                "insert: a negative member count clamps to the first slot");

            var todoData = new TodoData { Id = "cm-todo" };
            var noteData = new NoteData { Id = "cm-note" };
            var searchData = new SearchData { Id = "cm-search" };
            var folderData = new FolderData { Id = "cm-folder" };
            var todo = new FakeWindow(todoData);
            var note = new FakeWindow(noteData);
            var search = new FakeWindow(searchData);
            var folder = new FakeWindow(folderData);

            Check(!GroupDragPolicy.CanMerge(null, note) && !GroupDragPolicy.CanMerge(todo, null),
                "merge: a null source/target is refused");
            Check(!GroupDragPolicy.CanMerge(todo, todo),
                "merge: a widget cannot merge into itself");

            var groupData = new TodoData { Id = "cm-group-member" };
            var groupSource = new FakeGroupWindow(groupData, locked: false);
            Check(!GroupDragPolicy.CanMerge(groupSource, note),
                "merge: a group source is refused (no group to group)");
            Check(!GroupDragPolicy.CanMerge(groupSource, new FakeWindow(new NoteData { Id = "cm-x" })),
                "merge: a group source stays refused for every target");

            todoData.IsLocked = true;
            Check(!GroupDragPolicy.CanMerge(todo, note),
                "merge: a locked source is refused");
            todoData.IsLocked = false;
            noteData.IsLocked = true;
            Check(!GroupDragPolicy.CanMerge(todo, note),
                "merge: a locked target is refused");
            noteData.IsLocked = false;

            var lockedGroup = new FakeGroupWindow(new NoteData { Id = "cm-locked-group" }, locked: true);
            Check(!GroupDragPolicy.CanMerge(todo, lockedGroup),
                "merge: a locked target group is refused");
            var openGroup = new FakeGroupWindow(new NoteData { Id = "cm-open-group" }, locked: false);
            Check(GroupDragPolicy.CanMerge(todo, openGroup),
                "merge: a standalone todo can merge into an unlocked group");
            Check(GroupDragPolicy.CanMerge(todo, note),
                "merge: a standalone todo can merge into a standalone note");
            Check(GroupDragPolicy.CanMerge(search, note),
                "merge: search is a mergeable kind");

            Check(!GroupDragPolicy.CanMerge(folder, note),
                "merge: a Folder source is refused (deferred)");
            Check(!GroupDragPolicy.CanMerge(todo, folder),
                "merge: a Folder target is refused (deferred)");
            Check(!GroupDragPolicy.CanMerge(folder, new FakeWindow(new FolderData { Id = "cm-folder-2" })),
                "merge: Folder to Folder is refused");
        }

        /// <summary>
        /// A minimal IWidgetWindow for the pure policy pins. It never opens a
        /// panel, so it needs no WPF tree.
        /// </summary>
        private class FakeWindow : IWidgetWindow
        {
            public FakeWindow(WidgetData data)
            {
                Data = data;
            }

            public string WidgetId => Data == null ? null : Data.Id;
            public WidgetData Data { get; }
            public bool IsPanelOpen => false;
            public bool IsModalOpen => false;

            public (double width, double height) GetPanelSize() => (300, 200);
            public void ShowPanel(double defaultLeft, double defaultTop, bool activate = true) { }
            public void HidePanel() { }
            public void ShowWidgetMenu(Action onClosed) { }
            public void UpdateUI() { }
            public void RefreshTheme() { }

            // The pins only read the datum/flags; nothing subscribes here.
            public event Action<IWidgetWindow> OnDeleted { add { } remove { } }
            public event Action OnDataChanged { add { } remove { } }
            public event Action<bool> PanelVisibilityChanged { add { } remove { } }
        }

        /// <summary>A group host for the policy pins: the group lock is its only group fact.</summary>
        private sealed class FakeGroupWindow : FakeWindow, IGroupDragHost
        {
            public FakeGroupWindow(WidgetData activeMember, bool locked) : base(activeMember)
            {
                IsGroupLocked = locked;
            }

            public bool IsGroupLocked { get; }
        }

        private static void WriteThreeMemberGroup(string main, string activeWidgetId)        {
            File.WriteAllText(main, @"{
  ""Groups"": [ { ""Id"": ""g1"", ""ActiveWidgetId"": """ + activeWidgetId + @""", ""IsExpanded"": true } ],
  ""Widgets"": [
    { ""Kind"": ""folder"", ""Id"": ""w1"", ""GroupId"": ""g1"", ""GroupOrder"": 0, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w2"", ""GroupId"": ""g1"", ""GroupOrder"": 1, ""Items"": [] },
    { ""Kind"": ""folder"", ""Id"": ""w3"", ""GroupId"": ""g1"", ""GroupOrder"": 2, ""Items"": [] }
  ]
}");
        }

        private static WidgetGroup Find(List<WidgetGroup> groups, string id)
        {
            if (groups == null) return null;
            foreach (var group in groups)
            {
                if (group != null && group.Id == id) return group;
            }
            return null;
        }

        private static WidgetData Widget(AppConfig cfg, string id)
        {
            foreach (var widget in cfg.Widgets)
            {
                if (widget != null && widget.Id == id) return widget;
            }
            return null;
        }

        private static string Membership(AppConfig cfg, string id)
        {
            var widget = Widget(cfg, id);
            if (widget == null) return "<missing>";
            string group = widget.GroupId ?? "<null>";
            string order = widget.GroupOrder.HasValue ? widget.GroupOrder.Value.ToString() : "<null>";
            return group + ":" + order;
        }

        private static JObject FindSavedWidget(JObject root, string id)
        {
            var widgets = root["Widgets"] as JArray;
            if (widgets == null) return null;
            foreach (var token in widgets)
            {
                var obj = token as JObject;
                if (obj != null && (string)obj["Id"] == id) return obj;
            }
            return null;
        }

        private static bool Nearly(double? value, double expected)
        {
            return value.HasValue && Math.Abs(value.Value - expected) < 1e-9;
        }
    }
}
