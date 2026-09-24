using System;
using System.Collections.Generic;
using System.Windows;

namespace Kobold.Core
{
    /// <summary>A monitor's bounds and work area in physical (device) pixels.</summary>
    public sealed class MonitorInfo
    {
        public string DeviceName { get; set; }
        public bool IsPrimary { get; set; }
        public Rect BoundsDevice { get; set; }
        public Rect WorkAreaDevice { get; set; }
        public double DpiScale { get; set; }
    }

    /// <summary>
    /// Pure placement maths for widget panels. Window positions are saved as
    /// DIPs plus the DPI scale they were captured at - restoring multiplies
    /// back to physical pixels, picks the monitor the panel belongs to, clamps
    /// it into that monitor's work area, and converts back to DIPs using the
    /// destination monitor's scale. All inputs are injectable so the tests need
    /// no real screens; the Win32 half lives in MonitorInterop.
    /// </summary>
    public static class PanelPlacement
    {
        public static Rect ToDeviceRect(double dipX, double dipY, double dipWidth, double dipHeight,
            double savedScale, double fallbackScale)
        {
            double scale = savedScale > 0 ? savedScale : (fallbackScale > 0 ? fallbackScale : 1.0);
            return new Rect(dipX * scale, dipY * scale, dipWidth * scale, dipHeight * scale);
        }

        public static Rect ClampToMonitors(Rect panelDevice, IList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return panelDevice;
            MonitorInfo target = FindBestMonitor(panelDevice, monitors);
            return ClampToWorkArea(panelDevice, target.WorkAreaDevice);
        }

        public static Rect ToDipRect(Rect deviceRect, MonitorInfo target)
        {
            double scale = target != null && target.DpiScale > 0 ? target.DpiScale : 1.0;
            return new Rect(deviceRect.X / scale, deviceRect.Y / scale,
                deviceRect.Width / scale, deviceRect.Height / scale);
        }

        /// <summary>
        /// The monitor with the largest overlap; when the panel is fully
        /// off-screen, the one nearest to it.
        /// </summary>
        private static MonitorInfo FindBestMonitor(Rect rect, IList<MonitorInfo> monitors)
        {
            MonitorInfo best = null;
            double bestArea = 0;
            foreach (var monitor in monitors)
            {
                double area = IntersectionArea(rect, monitor.BoundsDevice);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = monitor;
                }
            }
            if (best != null) return best;

            Point centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            double bestDistance = double.MaxValue;
            foreach (var monitor in monitors)
            {
                Point monitorCentre = new Point(
                    monitor.BoundsDevice.X + monitor.BoundsDevice.Width / 2,
                    monitor.BoundsDevice.Y + monitor.BoundsDevice.Height / 2);
                double dx = monitorCentre.X - centre.X;
                double dy = monitorCentre.Y - centre.Y;
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = monitor;
                }
            }
            return best;
        }

        private static double IntersectionArea(Rect a, Rect b)
        {
            double width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
            double height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            return (width > 0 && height > 0) ? width * height : 0;
        }

        private static Rect ClampToWorkArea(Rect panel, Rect work)
        {
            // An oversized panel anchors at the work-area origin instead of
            // centring, so its header (the drag handle) stays reachable.
            double x = panel.Width >= work.Width
                ? work.X
                : Math.Max(work.X, Math.Min(panel.X, work.Right - panel.Width));
            double y = panel.Height >= work.Height
                ? work.Y
                : Math.Max(work.Y, Math.Min(panel.Y, work.Bottom - panel.Height));
            return new Rect(x, y, panel.Width, panel.Height);
        }
    }
}
