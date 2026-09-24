using System;
using System.Collections.Generic;
using System.IO;
using Kobold.Core;

namespace Kobold.AtomicFileCheck
{
    /// <summary>
    /// Behaviour checks for AtomicFile: a write either fully replaces the target
    /// or leaves the previous file untouched - never a half-written file and
    /// never a leftover temp file. Runs against throwaway temp directories.
    /// Exit code 0 = all green; 1 = failures. Run: dotnet run --project tests/AtomicFileCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "kobold-atomicfile-test-" + Guid.NewGuid().ToString("N"));
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
                Console.WriteLine("ALL ATOMIC-FILE CHECKS PASSED");
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

        private static void RunAll()
        {
            // 1. Success: contents are replaced, no temp file is left behind.
            string dir1 = Path.Combine(_root, "case1");
            Directory.CreateDirectory(dir1);
            string target1 = Path.Combine(dir1, "config.json");
            File.WriteAllText(target1, "old");
            AtomicFile.WriteAllText(target1, "new");
            Check(File.ReadAllText(target1) == "new", "write replaces contents");
            Check(!File.Exists(target1 + ".tmp"), "no temp file left after success");

            // 2. First write: missing directory and missing target are created.
            string deep = Path.Combine(_root, "case2", "sub", "config.json");
            AtomicFile.WriteAllText(deep, "fresh");
            Check(File.Exists(deep) && File.ReadAllText(deep) == "fresh", "creates directory and file");

            // 3. Locked target: the write fails, the old file survives untouched.
            string lockedPath = Path.Combine(_root, "case3", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(lockedPath));
            File.WriteAllText(lockedPath, "precious");
            using (var hold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool threw = false;
                try { AtomicFile.WriteAllText(lockedPath, "lost"); }
                catch (IOException) { threw = true; }
                Check(threw, "write fails when target is locked");
            }
            Check(File.ReadAllText(lockedPath) == "precious", "old contents survive a failed write");
            Check(!File.Exists(lockedPath + ".tmp"), "no temp file left after failure");

            // 4. Temp path occupied by a directory: fail without touching the target
            //    and without deleting what is not ours.
            string blocked = Path.Combine(_root, "case4", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(blocked));
            File.WriteAllText(blocked, "keep");
            Directory.CreateDirectory(blocked + ".tmp");
            bool threw2 = false;
            try { AtomicFile.WriteAllText(blocked, "nope"); }
            catch (Exception) { threw2 = true; }
            Check(threw2 && File.ReadAllText(blocked) == "keep", "temp collision fails without touching target");
            Check(Directory.Exists(blocked + ".tmp"), "pre-existing temp path is not deleted");

            // 5. Non-ASCII contents survive the UTF-8 (no BOM) round trip.
            string unicode = Path.Combine(_root, "case5", "config.json");
            AtomicFile.WriteAllText(unicode, "设置");
            Check(File.ReadAllText(unicode) == "设置", "non-ASCII contents round-trip");
        }
    }
}
