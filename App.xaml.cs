using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kobold.Core;
using Kobold.Core.Control;
using Kobold.Controls;
using Kobold.Helpers;
using Kobold.Services;
using Kobold.Services.Control;

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
        private ControlServer _controlServer;

        /// <summary>
        /// Test seam: the desktop self-checks load App.xaml resources but must
        /// not run the production startup (single-instance mutex, tray icon,
        /// control pipe, manager). WPF posts OnStartup to the dispatcher even
        /// when the checks never call Run, so a later pump would otherwise hit
        /// the user's running instance and shut the check process down.
        /// </summary>
        internal static bool SuppressStartupForChecks { get; set; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (SuppressStartupForChecks) return;

            // Restart-helper mode (spec decision 3B/11): wait for the exiting
            // instance and start a normal one. It must run before the
            // single-instance mutex is taken and never builds the manager, the
            // island or the control pipe.
            int helperParentPid;
            string helperExePath;
            if (RestartHelper.TryParse(e.Args, out helperParentPid, out helperExePath))
            {
                bool started = RestartHelper.Run(helperParentPid, helperExePath);
                Shutdown(started ? 0 : 1);
                return;
            }

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
            DesktopIconGuard.ApplyOnStartup(WidgetManager.Instance.Config);

            // Local-only control pipe for agents. Handlers always run on this UI
            // thread; the pipe thread only reads one JSON line per connection.
            _controlServer = new ControlServer(
                ControlPipeName.ForCurrentUser(),
                action => Dispatcher.Invoke(action),
                request => new ControlRequestRouter().Handle(request));
            _controlServer.Start();

            // Any input anywhere in the app restarts the idle window the memory
            // trim uses (see IdleTrimPolicy / MemoryTrimmer).
            InputManager.Current.PostProcessInput +=
                (s, args) => WidgetManager.Instance.NotifyInteraction();

            // Sync registry with config (ensures startup setting is applied)
            StartupRegistryService.Apply(WidgetManager.Instance.Config.StartWithWindows);
            
            // Initialize system tray
            _trayService = new TrayIconService();

            // Best-effort Quick Access reconciliation, off the critical path:
            // when the user wants the storage folder pinned, apply it once the
            // dispatcher is idle. Never auto-unpins (a manual pin survives a
            // desired state of false).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (WidgetManager.Instance.Config.PinStorageToQuickAccess)
                {
                    QuickAccess.EnsurePinned(Utils.GetStoragePath());
                }
            }), DispatcherPriority.Background);

            // Windows logoff/shutdown: land one final config write. Normal exit
            // paths (tray exit / OnExit) keep their existing behaviour.
            Current.SessionEnding += (sender, args) =>
            {
                try
                {
                    DesktopIconGuard.RestoreOnExit();
                    WidgetManager.Instance.BeginExitSave();
                }
                catch (Exception ex) { Debug.WriteLine($"[Kobold] session-end save failed: {ex.Message}"); }
            };
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
            // Stop accepting control requests before any teardown starts.
            try { _controlServer?.Stop(); } catch { }

            // Only the instance that started the desktop-icon session may end it,
            // and only that instance owns the mutex. A second launch exits here
            // without touching either.
            if (_isPrimaryInstance)
            {
                try { DesktopIconGuard.RestoreOnExit(); } catch { }
                try { ThemeManager.StopSystemPreferenceWatch(); } catch { }

                // Last chance for a synchronous write: every window has closed
                // by now, so a note's close-time splice is already in the data.
                // The save is a no-op if the manager was never built.
                try { WidgetManager.SaveConfigIfLoaded(); } catch { }

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


