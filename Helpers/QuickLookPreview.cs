using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Kobold.Helpers
{
    /// <summary>
    /// QuickLook integration: Space on a selected item asks QuickLook to preview it.
    /// QuickLook handles single-instancing itself - starting QuickLook.exe with a
    /// path hands the request to the running instance over its pipe (and starts it
    /// when it is not running), so this only has to find the executable.
    /// </summary>
    public static class QuickLookPreview
    {
        private static string _cachedPath;

        /// <summary>
        /// Absolute path to QuickLook.exe, or null when it is not installed.
        /// </summary>
        public static string Find()
        {
            if (_cachedPath != null && File.Exists(_cachedPath)) return _cachedPath;

            _cachedPath = FromAppPaths() ?? FromInstallDirs() ?? FromPath();
            return _cachedPath;
        }

        /// <summary>
        /// Asks QuickLook to show (or toggle away) the preview for a path. Returns
        /// false when QuickLook is not installed or could not be started, so the
        /// caller can leave the key to whatever else wants it.
        /// </summary>
        public static bool Request(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string exe = Find();
            if (exe == null) return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = false
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] QuickLook failed: {ex.Message}");
                return false;
            }
        }

        private static string FromAppPaths()
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var key = root.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\QuickLook.exe"))
                    {
                        var value = key?.GetValue(null) as string;
                        value = value?.Trim().Trim('"'); // some installers quote the default value
                        if (!string.IsNullOrEmpty(value) && File.Exists(value)) return value;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FromInstallDirs()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Programs\QuickLook\QuickLook.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             @"QuickLook\QuickLook.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             @"QuickLook\QuickLook.exe")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string FromPath()
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(path)) return null;

                foreach (var dir in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string candidate = Path.Combine(dir.Trim().Trim('"'), "QuickLook.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }
    }
}
