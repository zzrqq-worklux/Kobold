using System;
using System.Diagnostics;
using System.IO;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Application-internal restart orchestration. A replace/restore that
    /// changes structure writes the new document and then starts this same
    /// executable in helper mode (<c>--restart-helper &lt;pid&gt; &lt;exe&gt;</c>)
    /// before asking the app to exit. The helper waits for the old process to
    /// go away and starts a normal instance, so the single-instance mutex is
    /// never contested and the CLI can see the response arrive before shutdown.
    /// </summary>
    public static class RestartHelper
    {
        private const string HelperArgument = "--restart-helper";
        private const int ParentWaitMilliseconds = 15000;

        /// <summary>
        /// Starts a helper process. Detached and windowless: the helper belongs
        /// to the exiting app, not to a shell. Returns false when the process
        /// could not be started at all.
        /// </summary>
        public static bool Launch(int parentPid, string exePath)
        {
            if (parentPid <= 0 || string.IsNullOrWhiteSpace(exePath)) return false;

            try
            {
                Process process = Process.Start(new ProcessStartInfo(exePath)
                {
                    Arguments = HelperArgument + " " + parentPid + " \"" + exePath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = WorkingDirectoryFor(exePath)
                });
                return process != null;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("restart helper launch failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Helper-mode entry point: parse the arguments and, when they are the
        /// helper's, wait for the parent and start the new instance. Returns
        /// whether the process was in helper mode.
        /// </summary>
        public static bool TryRun(string[] args)
        {
            int parentPid;
            string exePath;
            if (!TryParse(args, out parentPid, out exePath)) return false;

            Run(parentPid, exePath);
            return true;
        }

        /// <summary>
        /// Accepts exactly <c>--restart-helper &lt;pid&gt; &lt;exe&gt;</c>.
        /// Anything else is a normal start.
        /// </summary>
        public static bool TryParse(string[] args, out int parentPid, out string exePath)
        {
            parentPid = 0;
            exePath = null;

            if (args == null || args.Length != 3) return false;
            if (!string.Equals(args[0], HelperArgument, StringComparison.Ordinal)) return false;

            int pid;
            if (!int.TryParse(args[1], out pid) || pid <= 0) return false;
            if (string.IsNullOrWhiteSpace(args[2])) return false;

            parentPid = pid;
            exePath = args[2];
            return true;
        }

        /// <summary>
        /// Waits (at most 15 s) for the exiting parent, then starts a normal
        /// app instance. Failure is logged and reported, never thrown: the
        /// helper has no UI that could explain it.
        /// </summary>
        public static bool Run(int parentPid, string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;

            try
            {
                using (Process parent = Process.GetProcessById(parentPid))
                {
                    if (!parent.WaitForExit(ParentWaitMilliseconds))
                        AppDiagnostics.Write("restart helper: the parent did not exit within 15 s");
                }
            }
            catch (Exception)
            {
                // The parent is already gone (or the pid is not ours): the
                // restart can go ahead.
            }

            try
            {
                Process process = Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = WorkingDirectoryFor(exePath)
                });
                return process != null;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("restart helper failed: " + ex.Message);
                return false;
            }
        }

        private static string WorkingDirectoryFor(string exePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(exePath);
                return string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : directory;
            }
            catch (Exception)
            {
                return Environment.CurrentDirectory;
            }
        }
    }
}
