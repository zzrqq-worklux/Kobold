using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Kobold.Core
{
    /// <summary>
    /// Locates the per-user install an in-app update may replace. The uninstall
    /// entry written by <c>tools\install.ps1</c> is the source of truth for
    /// where the app lives; self-updating only makes sense when the running copy
    /// is that installed copy and its folder can actually be written.
    ///
    /// Every registry-backed entry point has a pure overload, so the checks can
    /// exercise the path rules against temp folders without touching the
    /// machine's real install entry.
    /// </summary>
    public static class UpdateInstall
    {
        private const string UninstallKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Kobold";

        private const string InstallLocationValueName = "InstallLocation";
        private const string ExeName = "Kobold.exe";
        private const string ToolsFolderName = "tools";
        private const string WriteProbePrefix = ".update-probe-";

        /// <summary>
        /// Reads <c>InstallLocation</c> from the per-user uninstall entry the
        /// installer creates. Returns false when the key or the value is
        /// missing, unreadable, or not a usable path.
        /// </summary>
        public static bool TryGetInstallDir(out string installDir)
        {
            installDir = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false))
                {
                    if (key == null) return false;

                    string value = key.GetValue(InstallLocationValueName) as string;
                    string normalized = NormalizeDirectory(value);
                    if (normalized == null) return false;

                    installDir = normalized;
                    return true;
                }
            }
            catch (Exception ex)
            {
                // A missing or unreadable registry key simply means "this is
                // not an install we can update", never an app failure.
                Debug.WriteLine("[Kobold] install location lookup failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// True when the running copy is the installed copy: the install folder
        /// exists, <see cref="AppDomain.BaseDirectory"/> is that same folder
        /// once normalized, the folder is writable, and Kobold.exe is inside it.
        /// </summary>
        public static bool IsSelfUpdatable()
        {
            string installDir;
            if (!TryGetInstallDir(out installDir)) return false;
            return IsSelfUpdatable(installDir, AppDomain.CurrentDomain.BaseDirectory);
        }

        /// <summary>
        /// Pure overload: same rules as <see cref="IsSelfUpdatable()"/> but with
        /// the install folder and the running base directory supplied by the
        /// caller. The comparison ignores case and trailing separators, matching
        /// Windows path semantics.
        /// </summary>
        public static bool IsSelfUpdatable(string installDir, string baseDirectory)
        {
            string install = NormalizeDirectory(installDir);
            string running = NormalizeDirectory(baseDirectory);
            if (install == null || running == null) return false;

            if (!Directory.Exists(install)) return false;
            if (!string.Equals(install, running, StringComparison.OrdinalIgnoreCase)) return false;
            if (!File.Exists(Path.Combine(install, ExeName))) return false;
            return IsDirectoryWritable(install);
        }

        /// <summary>
        /// Returns <c>&lt;installDir&gt;\tools\&lt;fileName&gt;</c> when that file
        /// exists, otherwise null. The install folder comes from the uninstall
        /// entry; see the overload for the testable form.
        /// </summary>
        public static string ToolsScriptPath(string fileName)
        {
            string installDir;
            if (!TryGetInstallDir(out installDir)) return null;
            return ToolsScriptPath(installDir, fileName);
        }

        /// <summary>
        /// Pure overload: looks for a shipped script under the install folder's
        /// tools directory. A blank name, a bad folder or a missing file all
        /// return null - the caller then falls back to the download page.
        /// </summary>
        public static string ToolsScriptPath(string installDir, string fileName)
        {
            string install = NormalizeDirectory(installDir);
            if (install == null || string.IsNullOrWhiteSpace(fileName)) return null;

            string path = Path.Combine(install, ToolsFolderName, fileName);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Writes one unique probe file and deletes it again. This catches the
        /// Program Files case where the folder exists and holds the exe but the
        /// current user cannot replace anything inside it.
        /// </summary>
        private static bool IsDirectoryWritable(string directory)
        {
            string probe = Path.Combine(directory,
                WriteProbePrefix + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(probe, string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] install folder is not writable: " + ex.Message);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(probe)) File.Delete(probe);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// Normalizes a registry path to a full path without a trailing
        /// separator, for comparison with the running base directory. Strips
        /// the quotes an installer or user may have wrapped around the value.
        /// Returns null for blank or unparseable input.
        /// </summary>
        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                string root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root) && full.Length > root.Length)
                {
                    full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                return full;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (PathTooLongException)
            {
                return null;
            }
        }
    }
}
