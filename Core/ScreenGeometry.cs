using System.Windows;

namespace Kobold.Core
{
    /// <summary>
    /// Window/screen geometry helpers. Cursor coordinates (WinForms) are physical
    /// device pixels, while WPF window bounds are DIPs - on a scaled display
    /// (e.g. 200%) a physical point must be converted before it can be compared
    /// against Window.Left/Top/Width/Height.
    /// </summary>
    public static class ScreenGeometry
    {
        /// <summary>
        /// True when a physical screen point lies inside a window whose bounds
        /// are given in WPF DIPs.
        /// </summary>
        public static bool ContainsPhysicalPoint(
            Rect windowBoundsDips, double dpiScaleX, double dpiScaleY,
            double physicalX, double physicalY)
        {
            if (dpiScaleX <= 0 || dpiScaleY <= 0) return false;

            double localX = physicalX / dpiScaleX - windowBoundsDips.X;
            double localY = physicalY / dpiScaleY - windowBoundsDips.Y;

            return localX >= 0 && localX < windowBoundsDips.Width &&
                   localY >= 0 && localY < windowBoundsDips.Height;
        }

        /// <summary>
        /// True when a popup of the given physical width fits between the cursor
        /// and the right edge of the monitor's work area. Used to decide whether
        /// a context menu may open to the right without being clipped.
        /// </summary>
        public static bool FitsToTheRight(
            double physicalCursorX, double physicalPopupWidth, double physicalWorkAreaRight)
        {
            return physicalCursorX + physicalPopupWidth <= physicalWorkAreaRight;
        }

        /// <summary>
        /// Physical pixels a popup must move up so its bottom stays inside the
        /// work area (0 when it already fits). Mirrors FitsToTheRight for the
        /// vertical axis.
        /// </summary>
        public static double OverflowPastBottom(
            double physicalCursorY, double physicalPopupHeight, double physicalWorkAreaBottom)
        {
            double overflow = physicalCursorY + physicalPopupHeight - physicalWorkAreaBottom;
            return overflow > 0 ? overflow : 0;
        }
    }
}
