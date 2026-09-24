using System;
using System.Windows;

namespace Kobold.Core
{
    /// <summary>
    /// Decides when the always-on-top island should step aside for a fullscreen
    /// foreground window. Pure so the rules are testable without real windows;
    /// the WinEvent half lives in Services/FullscreenWatcher.
    /// </summary>
    public static class FullscreenPolicy
    {
        public const double TolerancePx = 2;

        /// <summary>True when the window covers the monitor within a small tolerance.</summary>
        public static bool CoversMonitor(Rect windowDevice, Rect monitorDevice, double tolerancePx = TolerancePx)
        {
            return windowDevice.Left <= monitorDevice.Left + tolerancePx
                && windowDevice.Top <= monitorDevice.Top + tolerancePx
                && windowDevice.Right >= monitorDevice.Right - tolerancePx
                && windowDevice.Bottom >= monitorDevice.Bottom - tolerancePx;
        }

        /// <summary>Shell host windows (desktop, taskbar) are never treated as fullscreen apps.</summary>
        public static bool IsShellWindow(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            return className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd";
        }

        /// <summary>
        /// The island only steps aside for a visible, non-minimised, external
        /// window that covers its own monitor. Every other case keeps the
        /// current behaviour (fail towards staying put).
        /// </summary>
        public static bool ShouldAvoidTopmost(bool isOwnProcess, bool isVisible, bool isMinimized,
            string className, Rect windowDevice, Rect monitorDevice)
        {
            if (isOwnProcess || !isVisible || isMinimized) return false;
            if (IsShellWindow(className)) return false;
            return CoversMonitor(windowDevice, monitorDevice);
        }
    }
}
