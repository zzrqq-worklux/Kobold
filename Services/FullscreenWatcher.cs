using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Kobold.Core;

namespace Kobold.Services
{
    /// <summary>
    /// Watches foreground changes (out-of-context WinEvent hook) and reports
    /// when an external fullscreen window covers its monitor, so the island can
    /// stop being topmost. Events are debounced on the UI dispatcher; the
    /// callback only fires when the state actually changes. Every interop
    /// failure keeps the current state - the watcher never throws to the UI.
    /// </summary>
    public sealed class FullscreenWatcher : IDisposable
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint WineventOutofcontext = 0x0000;
        private const uint WineventSkipownprocess = 0x0002;
        private const int DwmwaExtendedFrameBounds = 9;
        private const int DebounceMs = 300;

        private readonly Action<bool> _onAvoidChanged;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _debounce;
        private readonly WinEventDelegate _callback; // native side holds a raw pointer: keep this alive
        private IntPtr _hook;
        private bool _lastAvoid;
        private bool _disposed;

        /// <param name="onAvoidChanged">
        /// Called on the UI dispatcher with true when an external fullscreen
        /// window is up (island should step aside) and false when it is gone.
        /// </param>
        public FullscreenWatcher(Action<bool> onAvoidChanged)
        {
            _onAvoidChanged = onAvoidChanged ?? throw new ArgumentNullException(nameof(onAvoidChanged));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMs)
            };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                Evaluate();
            };
            _callback = OnWinEvent;
        }

        /// <summary>Arms the hook. A zero handle (no desktop session) is kept silently.</summary>
        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                    _callback, 0, 0, WineventOutofcontext | WineventSkipownprocess);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] fullscreen watcher hook failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Stop();
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_hook); }
                catch (Exception ex) { Debug.WriteLine($"[Kobold] fullscreen watcher unhook failed: {ex.Message}"); }
                _hook = IntPtr.Zero;
            }
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint eventThread, uint eventTime)
        {
            // Delivered on a system thread: only restart the debounce timer here.
            try
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    _debounce.Stop();
                    _debounce.Start();
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] fullscreen watcher dispatch failed: {ex.Message}");
            }
        }

        private void Evaluate()
        {
            if (_disposed) return;
            try
            {
                IntPtr foreground = GetForegroundWindow();
                bool avoid = false;
                if (foreground != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(foreground, out uint pid);
                    bool own = pid == (uint)Process.GetCurrentProcess().Id;
                    bool visible = IsWindowVisible(foreground);
                    bool minimized = IsIconic(foreground);
                    string className = GetClassNameSafe(foreground);
                    Rect windowDevice = GetWindowBoundsDevice(foreground);
                    MonitorInfo monitor = MonitorInterop.GetMonitorForWindow(foreground);
                    if (monitor != null)
                    {
                        avoid = FullscreenPolicy.ShouldAvoidTopmost(own, visible, minimized,
                            className, windowDevice, monitor.BoundsDevice);
                    }
                }

                if (avoid == _lastAvoid) return;
                _lastAvoid = avoid;
                _onAvoidChanged(avoid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] fullscreen evaluation failed: {ex.Message}");
            }
        }

        private static Rect GetWindowBoundsDevice(IntPtr hwnd)
        {
            // The extended frame skips the invisible resize border; the classic
            // rect is the fallback when DWM is unavailable.
            try
            {
                if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out RECT extended,
                        Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    return ToRect(extended);
                }
            }
            catch (Exception)
            {
                // dwmapi missing - fall through to GetWindowRect.
            }

            try
            {
                if (GetWindowRect(hwnd, out RECT fallback)) return ToRect(fallback);
            }
            catch (Exception)
            {
                // Window vanished mid-read - treated as "not fullscreen".
            }
            return Rect.Empty;
        }

        private static Rect ToRect(RECT rect)
        {
            return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        private static string GetClassNameSafe(IntPtr hwnd)
        {
            try
            {
                var buffer = new StringBuilder(256);
                return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
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

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        #endregion
    }
}
