using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kobold.Core;
using Kobold.Controls;
using Kobold.Services;

namespace Kobold
{
    public partial class App : Application
    {
        private const string MutexName = "Kobold_SingleInstance_Mutex_v3";
        private const string EventName = "Kobold_ActivateEvent_v3";
        private static Mutex _mutex;
        private static EventWaitHandle _eventWaitHandle;

        /// <summary>
        /// True only for the instance that actually created (and therefore owns) the
        /// single-instance mutex. A second launch must not undo the running
        /// instance's state, and must not release a mutex it never owned - doing so
        /// throws ApplicationException from OnExit.
        /// </summary>
        private static bool _isPrimaryInstance;
        private TrayIconService _trayService;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            
            if (!createdNew)
            {
                // Another instance is running - signal it and exit
                try
                {
                    _eventWaitHandle = EventWaitHandle.OpenExisting(EventName);
                    _eventWaitHandle.Set();
                }
                catch
                {
                    // Event handle doesn't exist, try fallback method
                    ActivateExistingInstance();
                }
                
                Current.Shutdown();
                return;
            }

            // Create event handle for this instance
            _eventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _isPrimaryInstance = true;

            // Start listening for activation requests
            var activationThread = new Thread(() =>
            {
                while (_eventWaitHandle.WaitOne())
                {
                    Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        WidgetManager.Instance.ShowIsland();
                    }));
                }
            })
            {
                IsBackground = true
            };
            activationThread.Start();

            base.OnStartup(e);
            
            // Initialize widget manager and create widgets
            WidgetManager.Instance.Initialize();
            WidgetManager.Instance.ApplyDesktopIconsOnStartup();

            // Any input anywhere in the app restarts the idle window the memory
            // trim uses (see IdleTrimPolicy / MemoryTrimmer).
            InputManager.Current.PostProcessInput +=
                (s, args) => WidgetManager.Instance.NotifyInteraction();

            // Sync registry with config (ensures startup setting is applied)
            SyncStartupRegistry();
            
            // Initialize system tray
            _trayService = new TrayIconService();

            // Windows logoff/shutdown: land one final config write. Normal exit
            // paths (tray exit / OnExit) keep their existing behaviour.
            Current.SessionEnding += (sender, args) =>
            {
                try { WidgetManager.Instance.BeginExitSave(); }
                catch (Exception ex) { Debug.WriteLine($"[Kobold] session-end save failed: {ex.Message}"); }
            };
        }
        
        private void SyncStartupRegistry()
        {
            try
            {
                bool startWithWindows = WidgetManager.Instance.Config.StartWithWindows;
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (startWithWindows)
                        {
                            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                            key.SetValue("Kobold", $"\"{exePath}\"");
                        }
                        else
                        {
                            key.DeleteValue("Kobold", false);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Kobold] Registry update failed: {ex.Message}"); }
        }

        private void ActivateExistingInstance()
        {
            Process current = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Only the instance that started the desktop-icon session may end it,
            // and only that instance owns the mutex. A second launch exits here
            // without touching either.
            if (_isPrimaryInstance)
            {
                try { WidgetManager.Instance.RestoreDesktopIconsOnExit(); } catch { }
                try { ThemeManager.StopSystemPreferenceWatch(); } catch { }

                try { _mutex?.ReleaseMutex(); }
                catch (Exception ex) { Debug.WriteLine($"[Kobold] ReleaseMutex failed: {ex.Message}"); }
            }

            _trayService?.Dispose();
            _eventWaitHandle?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}


