using System;
using System.Threading;
using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FoldRa.Core;
using FoldRa.Controls;
using FoldRa.Services;

namespace FoldRa
{
    public partial class App : Application
    {
        private const string MutexName = "FoldRa_SingleInstance_Mutex_v3";
        private const string EventName = "FoldRa_ActivateEvent_v3";
        private static Mutex _mutex;
        private static EventWaitHandle _eventWaitHandle;
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

            // Start listening for activation requests
            var activationThread = new Thread(() =>
            {
                while (_eventWaitHandle.WaitOne())
                {
                    Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        WidgetManager.Instance.ShowAll();
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
            
            // Sync registry with config (ensures startup setting is applied)
            SyncStartupRegistry();
            
            // Initialize system tray
            _trayService = new TrayIconService();
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
                            key.SetValue("FoldRa", $"\"{exePath}\"");
                        }
                        else
                        {
                            key.DeleteValue("FoldRa", false);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[FoldRa] Registry update failed: {ex.Message}"); }
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
            _trayService?.Dispose();
            _eventWaitHandle?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}


