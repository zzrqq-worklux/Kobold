using System;
using Kobold.Core;

namespace Kobold.DesktopIconsCheck
{
    /// <summary>
    /// Verifies that the desktop icon list window can be located on this machine.
    /// Explorer parents SHELLDLL_DefView directly under Progman on some systems
    /// and under a WorkerW on others; a locator that only handles the Progman
    /// case fails here. Exit code 0 = located; 1 = not found.
    /// Run: dotnet run --project tests/DesktopIconsCheck
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            IntPtr list = DesktopIcons.FindDesktopIconList();
            if (list == IntPtr.Zero)
            {
                Console.WriteLine("FAIL could not locate the desktop icon list (SysListView32).");
                Console.WriteLine("     Explorer parents SHELLDLL_DefView under Progman and under a WorkerW; both must be handled.");
                return 1;
            }

            Console.WriteLine("PASS located desktop icon list window: 0x" + list.ToInt64().ToString("X"));
            return 0;
        }
    }
}
