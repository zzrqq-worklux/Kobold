using System;
using Kobold.Core.FileSearch;
using Kobold.Core.Icons;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Applies a validated config document to the running app. The router
    /// saves afterwards, so nothing here writes the config: a failed desktop
    /// or shell operation must not leave a half-saved file behind.
    /// The three shell-facing appliers are injectable so the checks can
    /// exercise the warning path without touching the registry, Explorer or
    /// the real desktop.
    /// </summary>
    public sealed class ConfigApplyService
    {
        private readonly WidgetManager _manager;
        private readonly Func<bool, bool> _startupRegistry;
        private readonly Func<bool, bool> _quickAccess;
        private readonly Func<bool, bool> _desktopIcons;

        public ConfigApplyService(WidgetManager manager)
            : this(
                manager,
                StartupRegistryService.Apply,
                QuickAccessService.Apply,
                manager == null
                    ? (Func<bool, bool>)(value => false)
                    : manager.ApplyHideDesktopIconsForControl)
        {
        }

        /// <summary>Check seam: fake shell services, same apply semantics.</summary>
        internal ConfigApplyService(
            WidgetManager manager,
            Func<bool, bool> startupRegistry,
            Func<bool, bool> quickAccess,
            Func<bool, bool> desktopIcons)
        {
            if (manager == null) throw new ArgumentNullException("manager");

            _manager = manager;
            _startupRegistry = startupRegistry ?? (value => false);
            _quickAccess = quickAccess ?? (value => false);
            _desktopIcons = desktopIcons ?? (value => false);
        }

        /// <summary>
        /// Copies the hot fields into the live config and pushes the side
        /// effects to the theme, localization, island, icon, item-scale, file
        /// search and shell services. Returns the per-field warnings for shell
        /// operations that were refused; never saves.
        /// </summary>
        public JObject Apply(AppConfig next)
        {
            var warnings = new JObject();
            if (next == null) return warnings;

            AppConfig live = _manager.Config;
            if (live == null) return warnings;

            // Shell side effects are attempted only for a changed value: a
            // theme patch must never re-show icons the user hid by hand while
            // the app's own preference is off, nor unpin a manual Quick Access
            // pin. The values themselves are copied below either way.
            bool startWithWindowsChanged = live.StartWithWindows != next.StartWithWindows;
            bool quickAccessChanged = live.PinStorageToQuickAccess != next.PinStorageToQuickAccess;
            bool desktopIconsChanged = live.HideDesktopIcons != next.HideDesktopIcons;

            live.Language = next.Language;
            live.Theme = next.Theme;
            live.PanelOpacity = next.PanelOpacity;
            live.IslandX = next.IslandX;
            live.IslandCollapseDelay = next.IslandCollapseDelay;
            live.DefaultGridColumns = next.DefaultGridColumns;
            live.IconStyle = next.IconStyle;
            live.ItemScale = next.ItemScale;
            live.StartWithWindows = next.StartWithWindows;
            live.HideDesktopSourceOnStore = next.HideDesktopSourceOnStore;
            live.HideDesktopIcons = next.HideDesktopIcons;
            live.PinStorageToQuickAccess = next.PinStorageToQuickAccess;
            live.FileSearch = next.FileSearch;

            // Theme, language and panel opacity all ride the same repaint:
            // RefreshAllWidgets runs RefreshTheme -> UpdateUI per host.
            Localization.SetLanguage(next.Language);
            ThemeManager.SetTheme(next.Theme);
            _manager.RefreshAllWidgets();

            _manager.ApplyIslandSettings();

            IconStyleManager.Set(next.IconStyle);
            _manager.RefreshAllWidgets();
            _manager.ApplyIconStyle();

            // ItemScale first re-adapts each folder/group host, then repaints.
            _manager.ApplyItemScaleChange();

            FileSearchIndexService.Instance.ApplySettings(next.FileSearch);

            bool startupApplied = !startWithWindowsChanged
                || Invoke(_startupRegistry, next.StartWithWindows);
            bool quickAccessApplied = !quickAccessChanged
                || Invoke(_quickAccess, next.PinStorageToQuickAccess);
            bool desktopApplied = !desktopIconsChanged
                || Invoke(_desktopIcons, next.HideDesktopIcons);

            return CollectWarnings(startupApplied, quickAccessApplied, desktopApplied);
        }

        /// <summary>
        /// Pure warning collection: one property per failed shell applier.
        /// The requested value stays in the config either way, so the next
        /// startup retries the same desired state.
        /// </summary>
        internal static JObject CollectWarnings(
            bool startWithWindowsApplied,
            bool quickAccessApplied,
            bool desktopIconsApplied)
        {
            var warnings = new JObject();
            if (!startWithWindowsApplied)
                warnings["StartWithWindows"] = "The Windows startup entry could not be updated.";
            if (!quickAccessApplied)
                warnings["PinStorageToQuickAccess"] = "Quick Access could not be updated.";
            if (!desktopIconsApplied)
                warnings["HideDesktopIcons"] = "The desktop icons could not be updated.";
            return warnings;
        }

        private static bool Invoke(Func<bool, bool> applier, bool value)
        {
            if (applier == null) return false;

            try
            {
                return applier(value);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Write("config apply: shell applier failed: " + ex.Message);
                return false;
            }
        }
    }
}
