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

        private static FolderData Widget(string id, bool open)
        {
            return new FolderData { Id = id, Name = id, IsExpanded = open };
        }

        private static void OnlyOpenPanelsComeBack()
        {
            var folders = new List<FolderData>
            {
                Widget("closed-a", false),
                Widget("open-a", true),
                Widget("closed-b", false),
                Widget("open-b", true)
            };

            var restore = StartupPanels.ToRestore(folders);

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

            folders[1].IsExpanded = false;
            Check(StartupPanels.ToRestore(folders).Count == 1,
                "restore: closing a panel drops it from the next launch");
        }

        private static void EmptyAndMissingInputsAreSafe()
        {
            Check(StartupPanels.ToRestore(null).Count == 0, "restore: a null config opens nothing");
            Check(StartupPanels.ToRestore(new List<FolderData>()).Count == 0,
                "restore: an empty config opens nothing");
            Check(StartupPanels.ToRestore(new List<FolderData> { null, Widget("open", true), null }).Count == 1,
                "restore: null entries are skipped");
            Check(StartupPanels.ToRestore(new List<FolderData>
                {
                    new FolderData { Id = "default", Name = "default" }
                }).Count == 0,
                "restore: a fresh widget (never opened) stays closed");
        }
    }
}
