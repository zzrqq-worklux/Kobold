using System;
using System.Collections.Generic;
using System.IO;
using Kobold.Core;

namespace Kobold.StorageOpsCheck
{
    /// <summary>
    /// Sanity checks for StorageOps (store-into-storage, restore-to-original,
    /// hidden-attribute toggling). Runs against throwaway temp directories.
    /// Exit code 0 = all green; 1 = failures. Run: dotnet run --project tests/StorageOpsCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "kobold-storageops-test-" + Guid.NewGuid().ToString("N"));
            string desktop = Path.Combine(_root, "Desktop");
            string storage = Path.Combine(_root, "Storage");
            Directory.CreateDirectory(desktop);
            Directory.CreateDirectory(storage);
            try
            {
                RunAll(desktop, storage);
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch { }
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL STORAGE-OPS CHECKS PASSED");
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

        private static void RunAll(string desktop, string storage)
        {
            string srcFile = Path.Combine(desktop, "note.md");
            File.WriteAllText(srcFile, "hello");

            // 1. MoveIntoStorage moves a file and returns the new path
            string stored = StorageOps.MoveIntoStorage(srcFile, storage);
            Check(stored != null && File.Exists(stored) && !File.Exists(srcFile),
                "MoveIntoStorage moves file into storage");
            Check(Path.GetFileName(stored) == "note.md", "MoveIntoStorage keeps file name");

            // 2. Name collision gets a unique suffix
            File.WriteAllText(Path.Combine(desktop, "note.md"), "second");
            string stored2 = StorageOps.MoveIntoStorage(Path.Combine(desktop, "note.md"), storage);
            Check(stored2 != null && stored2 != stored && File.Exists(stored2),
                "MoveIntoStorage resolves name collision");

            // 3. MoveIntoStorage moves directories recursively
            string srcDir = Path.Combine(desktop, "project");
            Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
            File.WriteAllText(Path.Combine(srcDir, "sub", "a.txt"), "x");
            string storedDir = StorageOps.MoveIntoStorage(srcDir, storage);
            Check(storedDir != null && Directory.Exists(storedDir) &&
                  File.Exists(Path.Combine(storedDir, "sub", "a.txt")) && !Directory.Exists(srcDir),
                "MoveIntoStorage moves directory tree");

            // 4. TryRestoreToOriginal moves back to the original path
            bool restored = StorageOps.TryRestoreToOriginal(stored, srcFile);
            Check(restored && File.Exists(srcFile) && !File.Exists(stored),
                "TryRestoreToOriginal restores to original path");

            // 5. Occupied original target => failure, storage copy untouched
            File.WriteAllText(Path.Combine(desktop, "project"), "placeholder"); // occupies name as file
            bool blocked = StorageOps.TryRestoreToOriginal(storedDir, srcDir);
            Check(!blocked && Directory.Exists(storedDir), "TryRestoreToOriginal fails when target occupied");

            // 6. Null original path => failure (caller falls back to desktop)
            bool noOriginal = StorageOps.TryRestoreToOriginal(stored2, null);
            Check(!noOriginal && File.Exists(stored2), "TryRestoreToOriginal fails with null original");

            // 7. Hidden attribute toggling (file)
            string f = Path.Combine(desktop, "visible.md");
            File.WriteAllText(f, "x");
            StorageOps.SetHidden(f, true);
            Check(StorageOps.IsHidden(f), "SetHidden(file, true) sets hidden attribute");
            StorageOps.SetHidden(f, false);
            Check(!StorageOps.IsHidden(f), "SetHidden(file, false) clears hidden attribute");

            // 8. Hidden attribute toggling (directory)
            string d = Path.Combine(desktop, "folder");
            Directory.CreateDirectory(d);
            StorageOps.SetHidden(d, true);
            Check(StorageOps.IsHidden(d), "SetHidden(dir, true) sets hidden attribute");
            StorageOps.SetHidden(d, false);
            Check(!StorageOps.IsHidden(d), "SetHidden(dir, false) clears hidden attribute");

            // 9. SetHidden on missing path is a safe no-op
            Check(!StorageOps.SetHidden(Path.Combine(desktop, "missing"), true) &&
                  !StorageOps.IsHidden(Path.Combine(desktop, "missing")),
                "SetHidden/IsHidden safe on missing path");

            // 10. IsUnder path containment
            Check(StorageOps.IsUnder(Path.Combine(storage, "note.md"), storage) &&
                  !StorageOps.IsUnder(desktop, storage),
                "IsUnder detects containment");
        }
    }
}
