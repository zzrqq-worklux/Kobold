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
    /// delete) plus the kind-specific appendices the registry enables (folder:
    /// columns / max rows / item size). Hosts are widget windows, so the shared
    /// window operations are reached through <see cref="WidgetWindowBase"/>.
    /// FolderWidget keeps its own menu until it moves onto the base (phase 3).
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

            if (kind != null && kind.SupportsGridColumns) menu.AddMenuItem(CreateGridSizeMenu(host));
            if (kind != null && kind.SupportsMaxRows) menu.AddMenuItem(CreateMaxRowsMenu(host));
            if (kind != null && kind.SupportsItemScale) menu.AddMenuItem(CreateItemSizeMenu());

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

        #region Kind-specific appendices (folder)

        private static MenuItem CreateGridSizeMenu(IWidgetWindow host)
        {
            var gridItem = new MenuItem { Header = Localization.Get("Menu_GridSize") };
            int current = host.Data is FolderData folder ? folder.GridColumns : WidgetConstants.DEFAULT_GRID_COLUMNS;

            for (int cols = 2; cols <= 6; cols++)
            {
                int c = cols;
                var colItem = new MenuItem
                {
                    Header = $"{cols} " + Localization.Get("Menu_Columns"),
                    IsChecked = current == cols
                };
                colItem.Click += (s, a) =>
                {
                    if (host.Data is FolderData data) data.GridColumns = c;
                    DataChanged(host);
                };
                gridItem.Items.Add(colItem);
            }
            return gridItem;
        }

        /// <summary>
        /// Per-widget row cap: how many item rows this panel grows to before it
        /// scrolls. One widget staying compact does not affect the others.
        /// </summary>
        private static MenuItem CreateMaxRowsMenu(IWidgetWindow host)
        {
            var rowsItem = new MenuItem { Header = Localization.Get("Menu_MaxRows") };
            int current = host.Data is FolderData folder ? folder.MaxPanelRows : PanelRows.DefaultMaxRows;

            for (int rows = PanelRows.MinAllowedRows; rows <= PanelRows.MaxAllowedRows; rows++)
            {
                int r = rows;
                var rowItem = new MenuItem
                {
                    Header = rows + " " + Localization.Get("Menu_Rows"),
                    IsChecked = current == rows
                };
                rowItem.Click += (s, a) =>
                {
                    if (host.Data is FolderData data) data.MaxPanelRows = r;
                    DataChanged(host);
                };
                rowsItem.Items.Add(rowItem);
            }
            return rowsItem;
        }

        private static MenuItem CreateItemSizeMenu()
        {
            var sizeItem = new MenuItem { Header = Localization.Get("Menu_ItemSize") };
            var sizes = new[] {
                ("Size_Small", 0.9),
                ("Size_Normal", 1.0),
                ("Size_Default", 1.1),
                ("Size_Medium", 1.2),
                ("Size_Large", 1.3),
                ("Size_ExtraLarge", 1.5)
            };
            double currentScale = WidgetManager.Instance.Config.ItemScale;
            foreach (var (locKey, scale) in sizes)
            {
                double s = scale;
                var scaleItem = new MenuItem
                {
                    Header = Localization.Get(locKey),
                    IsChecked = Math.Abs(currentScale - scale) < 0.05
                };
                scaleItem.Click += (ss, aa) => ApplyItemScale(s);
                sizeItem.Items.Add(scaleItem);
            }
            return sizeItem;
        }

        private static void ApplyItemScale(double scale)
        {
            WidgetManager.Instance.Config.ItemScale = scale;
            WidgetManager.Instance.SaveConfig();

            foreach (var widget in WidgetManager.Instance.Widgets)
            {
                widget.UpdateUI();
            }
        }

        #endregion
    }
}
