using System;
using System.Collections.Generic;
using Kobold.Core;

namespace Kobold.WidgetItemsCheck
{
    /// <summary>
    /// Checks for WidgetItems (clone + merge-into-target semantics used when an
    /// item is dragged from one widget to another). Pure logic, no UI.
    /// Exit code 0 = all green; 1 = failures. Run: dotnet run --project tests/WidgetItemsCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        private static int Main()
        {
            RunAll();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL WIDGET-ITEMS CHECKS PASSED");
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

        private static WidgetItem Item(string path, bool isReference = true)
        {
            return new WidgetItem(path, isReference);
        }

        private static void RunAll()
        {
            CloneCopiesAllFields();
            CloneIsIndependent();
            MergeAppendsNewItems();
            MergePreservesItemData();
            MergeSkipsCaseInsensitiveDuplicates();
            MergeDeduplicatesWithinIncomingBatch();
            MergeSkipsNullAndEmptyPathEntries();
            MergeIsSafeOnNullAndEmptyInput();
        }

        private static void CloneCopiesAllFields()
        {
            var source = new WidgetItem
            {
                Name = "note.md",
                Path = @"C:\docs\note.md",
                IsReference = false,
                OriginalPath = @"D:\originals\note.md"
            };

            var copy = WidgetItems.Clone(source);

            Check(copy != null && !ReferenceEquals(copy, source), "Clone returns a new instance");
            Check(copy.Name == source.Name && copy.Path == source.Path &&
                  copy.IsReference == source.IsReference && copy.OriginalPath == source.OriginalPath,
                "Clone preserves Name/Path/IsReference/OriginalPath");
            Check(WidgetItems.Clone(null) == null, "Clone(null) returns null");
        }

        private static void CloneIsIndependent()
        {
            var source = Item(@"C:\docs\a.txt");
            var copy = WidgetItems.Clone(source);

            copy.Name = "changed";
            copy.Path = @"C:\other\b.txt";
            copy.IsReference = false;

            Check(source.Name == "a.txt" && source.Path == @"C:\docs\a.txt" && source.IsReference,
                "Mutating a clone does not affect the source");
        }

        private static void MergeAppendsNewItems()
        {
            var target = new List<WidgetItem> { Item(@"C:\w\a.txt") };

            int added = WidgetItems.MergeInto(target, new[] { Item(@"C:\w\b.txt"), Item(@"C:\w\c.txt") });

            Check(added == 2 && target.Count == 3, "MergeInto appends new items and returns the count");
            Check(target[0].Path == @"C:\w\a.txt" && target[1].Path == @"C:\w\b.txt" && target[2].Path == @"C:\w\c.txt",
                "MergeInto keeps existing order and appends incoming order");
        }

        private static void MergePreservesItemData()
        {
            var target = new List<WidgetItem>();
            var stored = new WidgetItem
            {
                Name = "Display Name",
                Path = @"C:\Kobold\Storage\project",
                IsReference = false,
                OriginalPath = @"C:\orig\project"
            };

            WidgetItems.MergeInto(target, new[] { stored });

            Check(target.Count == 1 &&
                  target[0].Name == "Display Name" &&
                  target[0].Path == @"C:\Kobold\Storage\project" &&
                  !target[0].IsReference &&
                  target[0].OriginalPath == @"C:\orig\project",
                "MergeInto preserves Name/IsReference/OriginalPath");
        }

        private static void MergeSkipsCaseInsensitiveDuplicates()
        {
            var target = new List<WidgetItem> { Item(@"C:\Work\Notes.MD") };

            int added = WidgetItems.MergeInto(target, new[] { Item(@"c:\work\notes.md") });

            Check(added == 0 && target.Count == 1, "MergeInto skips case-insensitive path duplicates");
            Check(target[0].Path == @"C:\Work\Notes.MD", "MergeInto keeps the existing item on duplicate");
        }

        private static void MergeDeduplicatesWithinIncomingBatch()
        {
            var target = new List<WidgetItem>();

            int added = WidgetItems.MergeInto(target, new[] { Item(@"C:\w\x.txt"), Item(@"C:\W\X.TXT") });

            Check(added == 1 && target.Count == 1, "MergeInto deduplicates paths within one batch");
        }

        private static void MergeSkipsNullAndEmptyPathEntries()
        {
            var target = new List<WidgetItem>();

            int added = WidgetItems.MergeInto(target, new WidgetItem[]
            {
                null,
                new WidgetItem("", false),
                Item(@"C:\w\ok.txt")
            });

            Check(added == 1 && target.Count == 1 && target[0].Path == @"C:\w\ok.txt",
                "MergeInto skips null and empty-path entries");
        }

        private static void MergeIsSafeOnNullAndEmptyInput()
        {
            Check(WidgetItems.MergeInto(null, new[] { Item(@"C:\w\a.txt") }) == 0,
                "MergeInto(null target) is a safe no-op");
            Check(WidgetItems.MergeInto(new List<WidgetItem>(), null) == 0,
                "MergeInto(null incoming) returns 0");
            Check(WidgetItems.MergeInto(new List<WidgetItem>(), new WidgetItem[0]) == 0,
                "MergeInto(empty incoming) returns 0");
        }
    }
}
