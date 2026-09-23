using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Kobold.Core;

namespace Kobold.FolderWatchCheck
{
    /// <summary>
    /// Checks for FolderWatcher (debounced change events, start/stop semantics).
    /// Touches the real filesystem - Windows only. Exit code 0 = all green.
    /// Run: dotnet run --project tests/FolderWatchCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;
        private static int _events;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            _root = Path.Combine(Path.GetTempPath(), "kobold-folderwatch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            try
            {
                MissingDirectoryCannotBeWatched();
                DisposedWatcherRefusesStart();
                ChangesRaiseOneDebouncedEvent();
                BurstsCollapseIntoOneEvent();
                HiddenFlagChangesAreReported();
                StoppedWatcherGoesQuiet();
                RestartFollowsTheNewFolder();
            }
            finally
            {
                TryDelete(_root);
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL FOLDER-WATCH CHECKS PASSED");
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

        private static string NewDir(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static int EventCount()
        {
            return Volatile.Read(ref _events);
        }

        private static void ResetEvents()
        {
            Interlocked.Exchange(ref _events, 0);
        }

        /// <summary>Waits for a condition instead of guessing a fixed delay.</summary>
        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }

        private static void MissingDirectoryCannotBeWatched()
        {
            using (var watcher = new FolderWatcher(100))
            {
                Check(!watcher.Start(Path.Combine(_root, "does-not-exist")),
                    "missing directory: Start returns false");
                Check(!watcher.IsWatching, "missing directory: IsWatching is false");
                Check(!watcher.Start(null) && !watcher.Start("   "),
                    "blank path: Start returns false");
            }
        }

        private static void DisposedWatcherRefusesStart()
        {
            var watcher = new FolderWatcher(100);
            watcher.Dispose();

            Check(!watcher.Start(NewDir("case-disposed")), "dispose: Start after Dispose returns false");
        }

        private static void ChangesRaiseOneDebouncedEvent()
        {
            string dir = NewDir("case-create");
            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);

                Check(watcher.Start(dir), "create: Start returns true for a real folder");
                Check(watcher.IsWatching, "create: IsWatching is true");
                Check(watcher.Path == dir, "create: Path reports the watched folder");

                ResetEvents();
                File.WriteAllText(Path.Combine(dir, "new.txt"), "");

                Check(WaitFor(() => EventCount() >= 1, 3000), "create: a new file raises Changed");
                Thread.Sleep(300);
                Check(EventCount() == 1, "create: the burst stays one event (got " + EventCount() + ")");
            }
        }

        private static void BurstsCollapseIntoOneEvent()
        {
            string dir = NewDir("case-burst");
            using (var watcher = new FolderWatcher(150))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(dir);

                ResetEvents();
                for (int i = 0; i < 5; i++)
                {
                    File.WriteAllText(Path.Combine(dir, "burst" + i + ".txt"), "");
                }

                Check(WaitFor(() => EventCount() >= 1, 3000), "burst: five files raise a Changed");
                Thread.Sleep(400);
                Check(EventCount() == 1,
                    "burst: five quick files collapse into one event (got " + EventCount() + ")");
            }
        }

        private static void HiddenFlagChangesAreReported()
        {
            string dir = NewDir("case-attributes");
            string file = Path.Combine(dir, "toggle.txt");
            File.WriteAllText(file, "");

            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(dir);

                ResetEvents();
                File.SetAttributes(file, FileAttributes.Hidden); // the listing filters this file

                Check(WaitFor(() => EventCount() >= 1, 3000),
                    "attributes: flipping the hidden flag raises Changed");
            }
        }

        private static void StoppedWatcherGoesQuiet()
        {
            string dir = NewDir("case-stop");
            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(dir);

                ResetEvents();
                File.WriteAllText(Path.Combine(dir, "before.txt"), "");
                Check(WaitFor(() => EventCount() >= 1, 3000), "stop: watching works before Stop");

                watcher.Stop();
                Check(!watcher.IsWatching, "stop: IsWatching is false after Stop");

                ResetEvents();
                File.WriteAllText(Path.Combine(dir, "after.txt"), "");
                Thread.Sleep(700);
                Check(EventCount() == 0, "stop: no events after Stop (got " + EventCount() + ")");
            }
        }

        private static void RestartFollowsTheNewFolder()
        {
            string first = NewDir("case-restart-a");
            string second = NewDir("case-restart-b");

            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(first);

                ResetEvents();
                File.WriteAllText(Path.Combine(first, "a.txt"), "");
                Check(WaitFor(() => EventCount() >= 1, 3000), "restart: the first folder is followed");

                Check(watcher.Start(second), "restart: Start on the second folder returns true");
                Check(watcher.Path == second, "restart: Path follows the new folder");

                ResetEvents();
                File.WriteAllText(Path.Combine(first, "ignored.txt"), "");
                Thread.Sleep(500);
                Check(EventCount() == 0, "restart: the old folder is no longer followed");

                File.WriteAllText(Path.Combine(second, "b.txt"), "");
                Check(WaitFor(() => EventCount() >= 1, 3000), "restart: the new folder raises events");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
