using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kobold.Core
{
    /// <summary>
    /// Controls the Explorer desktop icons for the current user.
    /// Writes the persisted "HideIcons" value (so it survives a restart) and also
    /// hides/shows the desktop icon-list window directly, because Explorer does
    /// not reliably re-read the value on its own. Falls back to a shell notify.
    /// </summary>
    public static class DesktopIcons
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string ValueName = "HideIcons"; // 1 = hidden, 0/absent = shown

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
        private const uint SHCNF_FLUSH = 0x1000;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>Raw registry value; null when the key or value is absent.</summary>
        public static int? ReadRaw()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    var value = key?.GetValue(ValueName);
                    return value == null ? (int?)null : Convert.ToInt32(value);
                }
            }
            catch { return null; }
        }

        /// <summary>Hides/shows the desktop icons (persisted + applied immediately).</summary>
        public static bool SetHidden(bool hide)
        {
            bool ok = WriteRaw(hide ? 1 : 0);
            if (!ApplyToDesktopWindow(hide)) Notify();
            return ok;
        }

        /// <summary>Restores the raw original value (null deletes it) and applies it.</summary>
        public static void Restore(int? original)
        {
            if (original.HasValue) WriteRaw(original.Value);
            else DeleteRaw();

            bool hide = original.HasValue && original.Value == 1;
            if (!ApplyToDesktopWindow(hide)) Notify();
        }

        private static bool WriteRaw(int value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return false;
                    key.SetValue(ValueName, value, RegistryValueKind.DWord);
                }
                return true;
            }
            catch { return false; }
        }

        private static void DeleteRaw()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }

        /// <summary>
        /// Locates the SysListView32 that holds the desktop icons, or IntPtr.Zero.
        /// Exposed so tests/DesktopIconsCheck can verify the lookup on the current
        /// machine (Explorer reparents the DefView differently across systems).
        /// </summary>
        public static IntPtr FindDesktopIconList()
        {
            try
            {
                // Common case: SHELLDLL_DefView is a direct child of Progman.
                var defView = FindDefViewUnderProgman();

                // Fallback: wallpaper apps and some Windows versions reparent the
                // DefView under a WorkerW top-level window instead.
                if (defView == IntPtr.Zero) defView = FindDefViewInTopLevelWindows();

                if (defView == IntPtr.Zero) return IntPtr.Zero;
                return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr FindDefViewUnderProgman()
        {
            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return IntPtr.Zero;
            return FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }

        private static IntPtr FindDefViewInTopLevelWindows()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                var defView = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView == IntPtr.Zero) return true; // keep looking
                found = defView;
                return false; // found it - stop
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Hides/shows the desktop icon list. This changes just the icons - the
        /// wallpaper (drawn by the DefView) stays visible.
        /// </summary>
        private static bool ApplyToDesktopWindow(bool hide)
        {
            try
            {
                var listView = FindDesktopIconList();
                if (listView == IntPtr.Zero) return false;

                ShowWindow(listView, hide ? SW_HIDE : SW_SHOW);
                return true;
            }
            catch { return false; }
        }

        private static void Notify()
        {
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }
    }
}
