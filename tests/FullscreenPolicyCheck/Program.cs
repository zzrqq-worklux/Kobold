using System;
using System.Collections.Generic;
using System.Windows;
using Kobold.Core;

namespace Kobold.FullscreenPolicyCheck
{
    /// <summary>
    /// Behaviour checks for FullscreenPolicy - the pure rules deciding when
    /// the island steps out of the way of a fullscreen foreground window.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FullscreenPolicyCheck
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
                Console.WriteLine("ALL FULLSCREEN-POLICY CHECKS PASSED");
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
            var monitor = new Rect(0, 0, 1920, 1080);

            Check(FullscreenPolicy.CoversMonitor(monitor, monitor), "exact cover");
            Check(FullscreenPolicy.CoversMonitor(new Rect(-1, -1, 1922, 1082), monitor), "slightly larger cover");
            Check(FullscreenPolicy.CoversMonitor(new Rect(2, 2, 1916, 1076), monitor), "2px tolerance cover");
            Check(!FullscreenPolicy.CoversMonitor(new Rect(0, 0, 1920, 1032), monitor),
                "taskbar-height window is not fullscreen");
            Check(!FullscreenPolicy.CoversMonitor(new Rect(100, 100, 800, 600), monitor),
                "a normal window is not fullscreen");

            Check(FullscreenPolicy.IsShellWindow("Progman") && FullscreenPolicy.IsShellWindow("WorkerW") &&
                  FullscreenPolicy.IsShellWindow("Shell_TrayWnd"), "shell host windows are recognised");
            Check(!FullscreenPolicy.IsShellWindow("Chrome_WidgetWin_1"), "a browser window is not a shell window");

            Check(!FullscreenPolicy.ShouldAvoidTopmost(true, true, false, "Chrome_WidgetWin_1", monitor, monitor),
                "our own fullscreen window does not push the island down");
            Check(!FullscreenPolicy.ShouldAvoidTopmost(false, true, false, "Progman", monitor, monitor),
                "the shell desktop does not count as fullscreen");
            Check(!FullscreenPolicy.ShouldAvoidTopmost(false, false, false, "Game", monitor, monitor),
                "invisible windows do not count");
            Check(!FullscreenPolicy.ShouldAvoidTopmost(false, true, true, "Game", monitor, monitor),
                "minimised windows do not count");
            Check(!FullscreenPolicy.ShouldAvoidTopmost(false, true, false, "Game", new Rect(0, 0, 1280, 720), monitor),
                "a 720p window on a 1080p monitor does not count");
            Check(FullscreenPolicy.ShouldAvoidTopmost(false, true, false, "Game", monitor, monitor),
                "an external fullscreen window counts");
        }
    }
}
