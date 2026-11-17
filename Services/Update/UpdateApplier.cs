using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Kobold.Core;

namespace Kobold.Services.Update
{
    /// <summary>
    /// Starts the detached PowerShell helper that replaces the installed files
    /// after the running app exits. The helper is a separate process because
    /// Windows cannot overwrite a running exe; this class only builds the argv
    /// and starts it, so the caller can shut down immediately afterwards.
    /// </summary>
    public static class UpdateApplier
    {
        private const string ScriptFileName = "apply-update.ps1";

        /// <summary>
        /// Launches <c>powershell.exe -NoProfile -ExecutionPolicy Bypass
        /// -WindowStyle Hidden -File apply-update.ps1 ...</c> without waiting for
        /// it. Returns false when the script is missing or the process could not
        /// be started; the caller then falls back to opening the download page.
        /// </summary>
        public static bool Apply(string stageDir, string installDir, int processId, string logPath)
        {
            if (string.IsNullOrWhiteSpace(stageDir)) return false;
            if (string.IsNullOrWhiteSpace(installDir)) return false;
            if (!Directory.Exists(stageDir)) return false;
            if (!Directory.Exists(installDir)) return false;

            string script = ResolveScriptPath(installDir);
            if (script == null) return false;

            string arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "
                + Quote(script)
                + " -ProcessId " + processId.ToString(CultureInfo.InvariantCulture)
                + " -StageDir " + Quote(stageDir)
                + " -InstallDir " + Quote(installDir);
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                arguments += " -LogPath " + Quote(logPath);
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    WorkingDirectory = installDir,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using (Process.Start(startInfo))
                {
                }
                return true;
            }
            catch (Exception ex)
            {
                // The caller falls back to "open the download page"; a failed
                // launch must never surface as an unhandled exception.
                Debug.WriteLine("[Kobold] could not start the update helper: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The package ships the script under <c>tools\</c>; the installer also
        /// copies it flat beside Kobold.exe. Accept both, preferring the tools
        /// copy so an updated install and a fresh install behave the same.
        /// </summary>
        private static string ResolveScriptPath(string installDir)
        {
            string shipped = UpdateInstall.ToolsScriptPath(installDir, ScriptFileName);
            if (shipped != null) return shipped;

            string flat = Path.Combine(installDir, ScriptFileName);
            return File.Exists(flat) ? flat : null;
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }
    }
}
