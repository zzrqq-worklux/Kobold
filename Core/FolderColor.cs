using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Kobold.Core
{
    /// <summary>Outcome of a folder color operation.</summary>
    public enum FolderColorResult
    {
        Applied,
        Restored,
        RefusedSpecialFolder,
        Failed
    }

    /// <summary>
    /// Folder "coloring" is an icon swap: Explorer shows the folder's
    /// [.ShellClassInfo] IconResource from desktop.ini, and it only reads that
    /// file for folders carrying the System flag. Everything here is Win32/IO -
    /// no UI - so tests/FolderColorCheck can drive it on temp folders.
    /// </summary>
    public static class FolderColor
    {
        private const string ShellClassInfo = ".ShellClassInfo";

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        // A desktop.ini holding any of these is the user's, not ours: restoring
        // then removes only the icon entries and leaves the rest alone.
        private static readonly string[] KeepMarkers =
        {
            "[extshellfolderviews]", "[viewstate]", "iconarea_image=",
            "iconarea_text=", "infotip=", "nosharing=", "logo="
        };

        /// <summary>
        /// Points the folder's icon at iconPath (index iconIndex). Existing
        /// desktop.ini settings are preserved; a folder whose icon comes from
        /// C:\Windows is refused because restoring it would be guesswork.
        /// </summary>
        public static FolderColorResult Apply(string folderPath, string iconPath, int iconIndex)
        {
            if (!IsFolder(folderPath) || string.IsNullOrWhiteSpace(iconPath)) return FolderColorResult.Failed;
            if (!File.Exists(iconPath)) return FolderColorResult.Failed;

            try
            {
                if (IsSpecialFolderIcon(GetIconResource(folderPath)))
                {
                    return FolderColorResult.RefusedSpecialFolder;
                }

                string iniPath = Path.Combine(folderPath, "desktop.ini");

                // Writing the entry ourselves keeps whatever else a desktop.ini
                // holds; the shell's folder-customisation API rewrites the file.
                WritePrivateProfileStringW(ShellClassInfo, "IconResource",
                    iconPath + "," + iconIndex, iniPath);
                WritePrivateProfileStringW(ShellClassInfo, "IconFile", null, iniPath);
                WritePrivateProfileStringW(ShellClassInfo, "IconIndex", null, iniPath);

                // Explorer only honours a desktop.ini that is hidden and system,
                // and only reads it at all for folders carrying the System flag.
                File.SetAttributes(iniPath, FileAttributes.Hidden | FileAttributes.System);
                PathMakeSystemFolder(folderPath);

                // PathMakeSystemFolder's own effect varies by Windows build; the
                // System bit on the folder is the part Explorer actually checks.
                File.SetAttributes(folderPath, File.GetAttributes(folderPath) | FileAttributes.System);

                RefreshShell();
                return FolderColorResult.Applied;
            }
            catch (Exception)
            {
                return FolderColorResult.Failed;
            }
        }

        /// <summary>
        /// Removes our icon entries. A desktop.ini that still holds the user's own
        /// settings (a ViewState, an InfoTip, a background image...) is kept - only
        /// the icon lines go; one we created ourselves is deleted outright.
        /// </summary>
        public static FolderColorResult Restore(string folderPath)
        {
            if (!IsFolder(folderPath)) return FolderColorResult.Failed;

            try
            {
                string iniPath = Path.Combine(folderPath, "desktop.ini");
                if (!File.Exists(iniPath))
                {
                    PathUnmakeSystemFolder(folderPath);
                    return FolderColorResult.Restored;
                }

                if (HasUserSettings(File.ReadAllText(iniPath)))
                {
                    RemoveIconEntries(iniPath);

                    // A section left without values is noise - drop it too.
                    if (!SectionHasValues(iniPath))
                    {
                        WritePrivateProfileStringW(ShellClassInfo, null, null, iniPath);
                    }

                    RefreshShell();
                    return FolderColorResult.Restored;
                }

                File.Delete(iniPath);
                PathUnmakeSystemFolder(folderPath);
                RefreshShell();
                return FolderColorResult.Restored;
            }
            catch (Exception)
            {
                return FolderColorResult.Failed;
            }
        }

        /// <summary>
        /// The icon the folder points at, as "file,index" - the form desktop.ini
        /// uses - or null when the folder shows the default icon.
        /// </summary>
        public static string GetIconResource(string folderPath)
        {
            if (!IsFolder(folderPath)) return null;

            try
            {
                string iniPath = Path.Combine(folderPath, "desktop.ini");
                if (!File.Exists(iniPath)) return null;

                string section = ReadSection(File.ReadAllText(iniPath), ShellClassInfo);
                if (string.IsNullOrEmpty(section)) return null;

                string index = ReadValue(section, "IconIndex");

                string resource = ReadValue(section, "IconResource");
                if (!string.IsNullOrWhiteSpace(resource)) return WithIndex(resource, index);

                string file = ReadValue(section, "IconFile");
                if (!string.IsNullOrWhiteSpace(file)) return WithIndex(file, index);

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True for icons that live in C:\Windows - those folders (Downloads,
        /// Documents, Music...) come from the OS and restoring them later would be
        /// guesswork, so they are left alone.
        /// </summary>
        public static bool IsSpecialFolderIcon(string iconResource)
        {
            if (string.IsNullOrWhiteSpace(iconResource)) return false;

            string path = iconResource;
            int comma = path.LastIndexOf(',');
            if (comma > 0) path = path.Substring(0, comma);
            path = path.Trim().Trim('"').ToLowerInvariant();

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                .ToLowerInvariant().TrimEnd('\\') + "\\";
            return path.StartsWith(windows, StringComparison.Ordinal);
        }

        /// <summary>Tells the shell its icon cache is stale.</summary>
        public static void RefreshShell()
        {
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

                IntPtr result;
                SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 5000, out result);
            }
            catch (Exception)
            {
                // A refresh we cannot deliver is not worth failing the operation.
            }
        }

        #region desktop.ini text

        private static bool IsFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try { return Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        private static void RemoveIconEntries(string iniPath)
        {
            WritePrivateProfileStringW(ShellClassInfo, "IconFile", null, iniPath);
            WritePrivateProfileStringW(ShellClassInfo, "IconIndex", null, iniPath);
            WritePrivateProfileStringW(ShellClassInfo, "IconResource", null, iniPath);
        }

        private static bool HasUserSettings(string iniText)
        {
            if (string.IsNullOrEmpty(iniText)) return false;
            if (iniText.IndexOf('{') >= 0) return true; // GUID-style extended attributes

            string lower = iniText.ToLowerInvariant();
            foreach (var marker in KeepMarkers)
            {
                if (lower.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool SectionHasValues(string iniPath)
        {
            try
            {
                string section = ReadSection(File.ReadAllText(iniPath), ShellClassInfo);
                if (string.IsNullOrEmpty(section)) return false;

                foreach (var raw in section.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length > 0 && line.IndexOf('=') > 0) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string WithIndex(string resource, string index)
        {
            string trimmed = resource.Trim();
            if (trimmed.IndexOf(',') >= 0) return trimmed;

            int parsed;
            return trimmed + "," + (int.TryParse(index, out parsed) ? parsed.ToString() : "0");
        }

        /// <summary>The raw lines of one section, or null when it is not there.</summary>
        private static string ReadSection(string iniText, string section)
        {
            if (string.IsNullOrEmpty(iniText)) return null;

            var builder = new StringBuilder();
            bool seen = false;
            bool inside = false;

            foreach (var raw in iniText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '[')
                {
                    seen = seen || line.Trim('[', ']', ' ')
                        .Equals(section, StringComparison.OrdinalIgnoreCase);
                    inside = line.Trim('[', ']', ' ')
                        .Equals(section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inside) builder.AppendLine(line);
            }

            return seen ? builder.ToString() : null;
        }

        private static string ReadValue(string sectionText, string key)
        {
            if (string.IsNullOrEmpty(sectionText)) return null;

            foreach (var raw in sectionText.Split('\n'))
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (!line.Substring(0, equals).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                return line.Substring(equals + 1).Trim();
            }
            return null;
        }

        #endregion

        #region Win32

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint WritePrivateProfileStringW(string section, string key,
            string value, string file);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool PathMakeSystemFolder(string path);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool PathUnmakeSystemFolder(string path);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        #endregion
    }
}
