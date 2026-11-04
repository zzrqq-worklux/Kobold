using System;
using System.Collections.Generic;
using System.Text;
using Kobold.Core;

namespace Kobold.StartupPanelsCheck
{
    /// <summary>
    /// Checks which widget panels a fresh launch reopens: the ones that were open
    /// when the app last closed, in the order the config lists them.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/StartupPanelsCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            OnlyOpenPanelsComeBack();
            EmptyAndMissingInputsAreSafe();
            GroupMembersRestoreThroughTheirGroup();
            OpenGroupsRestoreOnce();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL STARTUP-PANELS CHECKS PASSED");
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

        private static WidgetData Widget(string id, bool open)
        {
            return new FolderData { Id = id, Name = id, IsExpanded = open };
        }

        private static void OnlyOpenPanelsComeBack()
        {
            var widgets = new List<WidgetData>
            {
                Widget("closed-a", false),
                Widget("open-a", true),
                Widget("closed-b", false),
                Widget("open-b", true)
            };

            var restore = StartupPanels.ToRestore(widgets);

            Check(restore.Count == 2, "restore: only the open panels come back (got " + restore.Count + ")");
            if (restore.Count < 2)
            {
                Errors.Add("restore: not enough panels to check the order");
                return;
            }

            Check(restore[0].Id == "open-a" && restore[1].Id == "open-b",
                "restore: the config order is kept");
            Check(restore.TrueForAll(f => f.IsExpanded),
                "restore: nothing closed slips through");

            widgets[1].IsExpanded = false;
            Check(StartupPanels.ToRestore(widgets).Count == 1,
                "restore: closing a panel drops it from the next launch");
        }

        /// <summary>
        /// T5: a member of a group is never restored as its own panel - the
        /// group's own IsExpanded drives that restore instead.
        /// </summary>
        private static void GroupMembersRestoreThroughTheirGroup()
        {
            var widgets = new List<WidgetData>
            {
                new FolderData { Id = "g-a", Name = "A", GroupId = "g1", GroupOrder = 0, IsExpanded = true },
                new FolderData { Id = "g-b", Name = "B", GroupId = "g1", GroupOrder = 1, IsExpanded = false },
                Widget("free-open", true),
                Widget("free-closed", false)
            };

            var restore = StartupPanels.ToRestore(widgets);
            Check(restore.Count == 1 && restore[0].Id == "free-open",
                "groups: grouped members are excluded, the ungrouped open widget restores");
        }

        /// <summary>An open group restores once, in first-member config order.</summary>
        private static void OpenGroupsRestoreOnce()
        {
            var groups = new List<WidgetGroup>
            {
                new WidgetGroup { Id = "g2", IsExpanded = true },
                new WidgetGroup { Id = "g1", IsExpanded = true },
                new WidgetGroup { Id = "g3", IsExpanded = false }
            };
            var widgets = new List<WidgetData>
            {
                new FolderData { Id = "b1", GroupId = "g2", GroupOrder = 0, IsExpanded = true },
                new FolderData { Id = "a1", GroupId = "g1", GroupOrder = 0, IsExpanded = true },
                new FolderData { Id = "a2", GroupId = "g1", GroupOrder = 1, IsExpanded = true },
                new FolderData { Id = "b2", GroupId = "g2", GroupOrder = 1, IsExpanded = true },
                new FolderData { Id = "c1", GroupId = "g3", GroupOrder = 0, IsExpanded = true },
                new FolderData { Id = "c2", GroupId = "g3", GroupOrder = 1, IsExpanded = true },
                Widget("free-open", true)
            };

            var restoredGroups = GroupStartupPolicy.ToRestoreGroups(groups, widgets);
            Check(restoredGroups.Count == 2 && restoredGroups[0].Id == "g2" && restoredGroups[1].Id == "g1",
                "groups: an open group restores once, in first-member config order");

            var standalone = StartupPanels.ToRestore(widgets);
            Check(standalone.Count == 1 && standalone[0].Id == "free-open",
                "groups: no group member doubles as a standalone panel");
        }

        private static void EmptyAndMissingInputsAreSafe()
        {
            Check(StartupPanels.ToRestore(null).Count == 0, "restore: a null config opens nothing");
            Check(StartupPanels.ToRestore(new List<WidgetData>()).Count == 0,
                "restore: an empty config opens nothing");
            Check(StartupPanels.ToRestore(new List<WidgetData> { null, Widget("open", true), null }).Count == 1,
                "restore: null entries are skipped");
            Check(StartupPanels.ToRestore(new List<WidgetData>
                {
                    new FolderData { Id = "default", Name = "default" }
                }).Count == 0,
                "restore: a fresh widget (never opened) stays closed");
        }
    }
}
