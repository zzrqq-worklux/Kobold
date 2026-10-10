using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Kobold.Controls;
using Kobold.Core;
using Localization = Kobold.Core.Localization;

namespace Kobold.Helpers
{
    /// <summary>
    /// Builds the common widget menu (show/hide, rename, colour, lock, pin,
    /// delete) plus the kind-specific appendices (todo: the holiday region).
    /// The folder layout entries (columns / max rows / item size) are retired:
    /// the panel adapts its grid to its own size. Hosts are widget windows, so
    /// the shared window operations are reached through
    /// <see cref="WidgetWindowBase"/>.
    /// </summary>
    public static class WidgetMenuBuilder
    {
        /// <summary>
        /// The kind-aware widget menu. <paramref name="onClosed"/> fires once
        /// the menu is gone (the island uses it to resume auto-collapse).
        /// </summary>
        public static ContextMenu Build(IWidgetWindow host, WidgetKind kind, Action onClosed)
        {
            var menu = new MenuBuilder(host.Data.Color)
                .AddItem(host.IsPanelOpen ? "Menu_HideWidget" : "Menu_ShowWidget",
                    () => WidgetManager.Instance.ToggleWidgetPanel(host))
                .AddItem("Menu_Rename", () => AsWindow(host)?.BeginRename())
                .AddItem("Menu_ChangeColor", () => AsWindow(host)?.BeginColorChange())
                .AddItem(host.Data.IsLocked ? "Menu_UnlockWidget" : "Menu_LockWidget",
                    () => ToggleLock(host))
                .AddItem(host.Data.IsPanelPinned ? "Menu_UnpinWidget" : "Menu_PinWidget",
                    () => TogglePin(host));

            if (host.Data is TodoData) menu.AddMenuItem(CreateHolidayRegionMenu(host));

            var built = menu.AddSeparator()
                .AddItem("Menu_Delete", () => Delete(host))
                .Build();

            if (onClosed != null) built.Closed += (s, e) => onClosed();
            return built;
        }

        /// <summary>
        /// One "New {kind}" entry per registered kind - shared by the island's
        /// + tile and the tray's new-widget submenu.
        /// </summary>
        public static IReadOnlyList<MenuItem> BuildKindChoices(Action<string> onCreate)
        {
            var items = new List<MenuItem>();
            foreach (var kind in WidgetKindRegistry.All)
            {
                string kindId = kind.Id;
                var item = new MenuItem
                {
                    Header = Localization.Format("UI_NewWidgetFormat", Localization.Get(KindNameKey(kind)))
                };
                item.Click += (s, e) => onCreate?.Invoke(kindId);
                items.Add(item);
            }
            return items;
        }

        /// <summary>The kind's label key: "UI_WidgetKindFolder" / "UI_WidgetKindTodo".</summary>
        private static string KindNameKey(WidgetKind kind)
        {
            return "UI_WidgetKind" + char.ToUpperInvariant(kind.Id[0]) + kind.Id.Substring(1);
        }

        private static WidgetWindowBase AsWindow(IWidgetWindow host) => host as WidgetWindowBase;

        private static void ToggleLock(IWidgetWindow host)
        {
            var window = AsWindow(host);
            if (window != null)
            {
                window.ToggleLock();
                return;
            }
            host.Data.IsLocked = !host.Data.IsLocked;
            DataChanged(host);
        }

        private static void TogglePin(IWidgetWindow host)
        {
            var window = AsWindow(host);
            if (window != null)
            {
                window.TogglePin();
                return;
            }
            host.Data.IsPanelPinned = !host.Data.IsPanelPinned;
            DataChanged(host);
        }

        private static void Delete(IWidgetWindow host)
        {
            var window = AsWindow(host);
            if (window != null)
            {
                window.RequestDelete();
                return;
            }
            // A host without the shared base cannot raise OnDeleted itself.
            WidgetManager.Instance.RemoveWidget(host.WidgetId);
        }

        /// <summary>
        /// Commits a direct data edit: repaint, then the usual save + island
        /// refresh path (through the base's OnDataChanged when available).
        /// </summary>
        private static void DataChanged(IWidgetWindow host)
        {
            host.UpdateUI();

            var window = AsWindow(host);
            if (window != null)
            {
                window.NotifyDataChanged();
                return;
            }
            WidgetManager.Instance.SaveConfig();
            WidgetManager.Instance.RefreshIsland();
        }

        #region Kind-specific appendices (todo)

        /// <summary>
        /// Task 2.5: the todo widget's 假日地区 single-select submenu
        /// (中国/日本/美国/英国/关闭). The checked item follows the data through
        /// <see cref="PublicHolidays.NormalizeRegion"/> (missing/unknown = 中国),
        /// and a pick writes the region back through the widget's usual change
        /// path (repaint + persist). The day cells keep their height.
        /// </summary>
        private static MenuItem CreateHolidayRegionMenu(IWidgetWindow host)
        {
            var regionItem = new MenuItem { Header = Localization.Get("Menu_HolidayRegion") };
            string current = PublicHolidays.NormalizeRegion((host.Data as TodoData)?.HolidayRegion);

            string[] keys =
            {
                "Holiday_RegionCn", "Holiday_RegionJp", "Holiday_RegionUs",
                "Holiday_RegionUk", "Holiday_RegionNone"
            };
            string[] ids =
            {
                PublicHolidays.RegionCn, PublicHolidays.RegionJp, PublicHolidays.RegionUs,
                PublicHolidays.RegionUk, PublicHolidays.RegionNone
            };

            for (int i = 0; i < keys.Length; i++)
            {
                string id = ids[i];
                var item = new MenuItem
                {
                    Header = Localization.Get(keys[i]),
                    IsChecked = current == id
                };
                item.Click += (s, e) =>
                {
                    if (host.Data is TodoData data) data.HolidayRegion = id;
                    DataChanged(host);
                };
                regionItem.Items.Add(item);
            }
            return regionItem;
        }

        #endregion
    }
}
