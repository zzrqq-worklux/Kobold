using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Kobold.Core
{
    /// <summary>
    /// Owns the current user's "start with Windows" registration
    /// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Kobold). The
    /// control plane applies the desired value immediately instead of waiting
    /// for the Settings window; every failure is reported to the caller so a
    /// patch can surface a warning instead of silently disagreeing with disk.
    /// </summary>
    public static class StartupRegistryService
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Kobold";

        /// <summary>
        /// Test seam: when set, the checks fake the registry write. Null in
        /// production, where the real HKCU Run value is used. Internal so the
        /// production API stays one small <see cref="Apply"/> entry point.
        /// </summary>
        internal static Func<bool, bool> Applier { get; set; }

        /// <summary>
        /// Adds or removes the Run entry. Returns false when the key cannot be
        /// opened or the write throws; never throws.
        /// </summary>
        public static bool Apply(bool startWithWindows)
        {
            Func<bool, bool> applier = Applier;
            if (applier != null) return applier(startWithWindows);

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;

                    if (startWithWindows)
                    {
                        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue(ValueName, "\"" + exePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Kobold] startup registry update failed: " + ex.Message);
                return false;
            }
        }
    }
}
