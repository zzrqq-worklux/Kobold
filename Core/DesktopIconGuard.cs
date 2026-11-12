using System;

namespace Kobold.Core
{
    /// <summary>What the exit guard does to the desktop icons.</summary>
    public enum DesktopIconAction
    {
        None,
        Show
    }

    /// <summary>
    /// Hides the desktop icons while the app runs when the user asked for it,
    /// and always shows them on exit. The original registry value is still
    /// recorded for diagnostics, but it does not drive the exit action: the
    /// user's preference re-hides them on the next startup. The actual registry
    /// and desktop-window operations sit behind <see cref="IDesktopIconState"/>
    /// so the checks can exercise the policy without touching a real desktop.
    /// </summary>
    public static class DesktopIconGuard
    {
        /// <summary>
        /// The two desktop operations the guard needs. Internal so production
        /// keeps one small surface and only the checks inject a fake.
        /// </summary>
        internal interface IDesktopIconState
        {
            int? ReadRaw();
            bool SetHidden(bool hide);
        }

        private sealed class SystemDesktopIconState : IDesktopIconState
        {
            public int? ReadRaw()
            {
                return DesktopIcons.ReadRaw();
            }

            public bool SetHidden(bool hide)
            {
                return DesktopIcons.SetHidden(hide);
            }
        }

        private static readonly IDesktopIconState SystemState = new SystemDesktopIconState();

        /// <summary>Test seam: the checks swap in a fake and restore this afterwards.</summary>
        internal static IDesktopIconState State { get; set; } = SystemState;

        /// <summary>
        /// The exit decision: a run that currently hides the icons shows them
        /// on exit, whatever the pre-run value was. Everything else is a no-op.
        /// </summary>
        public static DesktopIconAction Policy(bool originalHidden, bool currentHidden, bool hideDesktopIcons)
        {
            if (!hideDesktopIcons || !currentHidden) return DesktopIconAction.None;
            return DesktopIconAction.Show;
        }

        /// <summary>
        /// Startup: remember the raw value, hide now when configured, and save
        /// the config (the record must survive a crash). Mirrors
        /// WidgetManager.ApplyDesktopIconsOnStartup, which App used before.
        /// </summary>
        public static void ApplyOnStartup(AppConfig config)
        {
            if (config == null) return;

            try
            {
                config.HideIconsOriginal = State.ReadRaw();
                if (config.HideDesktopIcons && !State.SetHidden(true))
                {
                    AppDiagnostics.Write("desktop guard: hiding the icons was refused");
                }
                config.Save();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("desktop guard startup failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Exit: always show the icons when the live preference still hides
        /// them. Idempotent and never throws; the next startup re-hides from
        /// the same config value.
        /// </summary>
        public static void RestoreOnExit()
        {
            try
            {
                AppConfig config = WidgetManager.Instance.Config;
                if (config == null || !config.HideDesktopIcons) return;

                AppDiagnostics.Write(State.SetHidden(false)
                    ? "desktop guard: restored icons on exit"
                    : "desktop guard: showing the icons was refused");
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("desktop guard failed: " + ex.Message);
            }
        }
    }
}
