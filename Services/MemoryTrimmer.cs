using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kobold.Services
{
    /// <summary>
    /// Hands the idle working set back to the OS. WPF keeps its render caches
    /// resident for the life of the process - hiding or closing a window returns
    /// almost nothing - so the only way to lower the idle footprint is to page
    /// the working set out; the pages fault back in on demand.
    /// </summary>
    public static class MemoryTrimmer
    {
        /// <summary>Collects managed garbage, then empties the working set. Never throws.</summary>
        public static bool Trim()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                return EmptyWorkingSet(GetCurrentProcess());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] Working-set trim failed: " + ex.Message);
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr process);
    }
}
