using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Kobold.Core
{
    /// <summary>
    /// Win32 monitor facts (bounds, work area, per-monitor DPI) for the
    /// placement logic in PanelPlacement. The list is cached and invalidated
    /// when the display configuration changes; every call fails soft - an
    /// empty list means "fall back to the old behaviour".
    /// </summary>
    public static class MonitorInterop
    {
        private const uint MonitorDefaultToNearest = 2;
        private const int MdtEffectiveDpi = 0;

        private static readonly object CacheLock = new object();
        private static List<MonitorInfo> _monitors;
        private static List<IntPtr> _handles;

        static MonitorInterop()
        {
            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => Invalidate();
            }
            catch (Exception)
            {
                // SystemEvents may be unavailable; the cache then lives for the
                // process lifetime, which is the old behaviour anyway.
            }
        }

        /// <summary>Drops the cached monitor list (next call re-enumerates).</summary>
        public static void Invalidate()
        {
            lock (CacheLock)
            {
                _monitors = null;
                _handles = null;
            }
        }

        public static IList<MonitorInfo> GetMonitors()
        {
            lock (CacheLock)
            {
                if (_monitors == null) BuildCache();
                return _monitors;
            }
        }

        public static MonitorInfo GetMonitorForWindow(IntPtr hwnd)
        {
            return FindByHandle(MonitorFromWindow(hwnd, MonitorDefaultToNearest));
        }

        public static MonitorInfo GetMonitorForDevicePoint(Point devicePoint)
        {
            var point = new POINT { X = (int)devicePoint.X, Y = (int)devicePoint.Y };
            return FindByHandle(MonitorFromPoint(point, MonitorDefaultToNearest));
        }

        /// <summary>
        /// The work area of the window's current monitor, in WPF DIPs. Before
        /// the window has a handle (or when the monitor data is unavailable),
        /// this falls back to the primary monitor's work area.
        /// </summary>
        public static Rect WorkAreaDipForWindow(Window window)
        {
            try
            {
                IntPtr hwnd = window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;

                MonitorInfo monitor = FindByHandle(MonitorFromWindow(hwnd, MonitorDefaultToNearest));
                HwndSource source = HwndSource.FromHwnd(hwnd);
                if (monitor == null || source == null || source.CompositionTarget == null)
                    return SystemParameters.WorkArea;

                var toDip = source.CompositionTarget.TransformFromDevice;
                Point topLeft = toDip.Transform(new Point(monitor.WorkAreaDevice.X, monitor.WorkAreaDevice.Y));
                Point bottomRight = toDip.Transform(new Point(monitor.WorkAreaDevice.Right, monitor.WorkAreaDevice.Bottom));
                return new Rect(topLeft, bottomRight);
            }
            catch (Exception)
            {
                return SystemParameters.WorkArea;
            }
        }

        private static MonitorInfo FindByHandle(IntPtr handle)
        {
            lock (CacheLock)
            {
                if (_monitors == null) BuildCache();
                int index = _handles.IndexOf(handle);
                if (index >= 0) return _monitors[index];

                // The handle was not in the cache: the display layout may have
                // changed since it was built - rebuild once and retry.
                Invalidate();
                BuildCache();
                index = _handles.IndexOf(handle);
                return index >= 0 ? _monitors[index] : null;
            }
        }

        private static void BuildCache()
        {
            var monitors = new List<MonitorInfo>();
            var handles = new List<IntPtr>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
                {
                    var info = new MONITORINFOEX();
                    info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    if (!GetMonitorInfo(hMonitor, ref info)) return true;

                    monitors.Add(new MonitorInfo
                    {
                        DeviceName = info.szDevice,
                        IsPrimary = (info.dwFlags & 1) != 0, // MONITORINFOF_PRIMARY
                        BoundsDevice = new Rect(
                            info.rcMonitor.Left, info.rcMonitor.Top,
                            info.rcMonitor.Right - info.rcMonitor.Left,
                            info.rcMonitor.Bottom - info.rcMonitor.Top),
                        WorkAreaDevice = new Rect(
                            info.rcWork.Left, info.rcWork.Top,
                            info.rcWork.Right - info.rcWork.Left,
                            info.rcWork.Bottom - info.rcWork.Top),
                        DpiScale = ReadDpiScale(hMonitor)
                    });
                    handles.Add(hMonitor);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
                monitors.Clear();
                handles.Clear();
            }

            _monitors = monitors;
            _handles = handles;
        }

        private static double ReadDpiScale(IntPtr hMonitor)
        {
            try
            {
                uint dpiX, dpiY;
                if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out dpiX, out dpiY) == 0 && dpiX > 0)
                    return dpiX / 96.0;
            }
            catch (Exception)
            {
                // Shcore.dll missing (pre-8.1) - fall back to 100%.
            }
            return 1.0;
        }

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

        #endregion
    }
}
