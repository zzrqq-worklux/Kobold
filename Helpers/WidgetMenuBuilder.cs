using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Kobold.Controls;
using Kobold.Core;
using Kobold.Core.FileSearch;
using Kobold.Core.Icons;
using Localization = Kobold.Core.Localization;

namespace Kobold.Helpers
{
    /// <summary>
    /// The actions a widget menu entry invokes. A standalone window passes its
    /// own operations; a group window passes the active member's operations
    /// while keeping its group lock and panel visibility.
    /// </summary>
    public sealed class WidgetMenuActions
    {
        /// <summary>True while the host panel is open (labels show/hide).</summary>
        public bool IsPanelOpen { get; set; }

        /// <summary>
        /// Optional lock-state override. A group lock lives on the group, not
        /// on the active member's datum, so the group supplies it here.
        /// </summary>
        public bool? IsLocked { get; set; }

        public Action ToggleVisibility { get; set; }
        public Action Rename { get; set; }
        public Action ChangeColor { get; set; }
        public Action ToggleLock { get; set; }
        public Action TogglePin { get; set; }
        public Action Delete { get; set; }

        /// <summary>Runs after a kind-specific edit (font, holiday region).</summary>
        public Action Changed { get; set; }
    }

    /// <summary>
    /// Builds the common widget menu (show/hide, rename, colour, lock, pin,
    /// delete) plus the kind-specific appendices (folder: the item-size
    /// picker; todo: the holiday region; note: the font scale). The folder
    /// layout entries (columns / max rows) are retired: the panel adapts its
    /// own column count and spacing. The icon size stays a user choice - a
    /// folder-only appendix whose scale feeds the folder rendering. Hosts are
    /// widget windows, so the shared window operations are reached through
    /// <see cref="WidgetWindowBase"/>; a group supplies its active member's
    /// data through the core overload.
    /// </summary>
    public static class WidgetMenuBuilder
    {
        /// <summary>
        /// The kind-aware widget menu. <paramref name="onClosed"/> fires once
        /// the menu is gone (the island uses it to resume auto-collapse).
        /// </summary>
        public static ContextMenu Build(IWidgetWindow host, Action onClosed)
        {
            if (host == null) throw new ArgumentNullException("host");

            return Build(host.Data, new WidgetMenuActions
            {
                IsPanelOpen = host.IsPanelOpen,
                ToggleVisibility = () => WidgetManager.Instance.ToggleWidgetPanel(host),
                Rename = () => AsWindow(host)?.BeginRename(),
                ChangeColor = () => AsWindow(host)?.BeginColorChange(),
                ToggleLock = () => ToggleLock(host),
                TogglePin = () => TogglePin(host),
                Delete = () => Delete(host),
                Changed = () => DataChanged(host)
            }, onClosed);
        }

        /// <summary>
        /// The shared core: the common entries plus the kind appendices read
        /// from <paramref name="data"/>, with every action supplied by the
        /// caller. A standalone host adapts itself above; a group window passes
        /// its active member's datum and operations.
        /// </summary>
        public static ContextMenu Build(WidgetData data, WidgetMenuActions actions, Action onClosed)
        {
            if (data == null) throw new ArgumentNullException("data");
            actions = actions ?? new WidgetMenuActions();

            bool locked = actions.IsLocked ?? data.IsLocked;
            var menu = new MenuBuilder(data.Color)
                .AddItem(actions.IsPanelOpen ? "Menu_HideWidget" : "Menu_ShowWidget",
                    actions.ToggleVisibility, true,
                    actions.IsPanelOpen ? IconId.EyeOff : IconId.Eye)
                .AddItem("Menu_Rename", actions.Rename, true, IconId.Edit)
                .AddItem("Menu_ChangeColor", actions.ChangeColor, true, IconId.Palette)
                .AddItem(locked ? "Menu_UnlockWidget" : "Menu_LockWidget",
                    actions.ToggleLock, true, IconId.Lock)
                .AddItem(data.IsPanelPinned ? "Menu_UnpinWidget" : "Menu_PinWidget",
                    actions.TogglePin, true, IconId.Pin);

            if (data is FolderData) menu.AddMenuItem(CreateItemSizeMenu());
            if (data is TodoData) menu.AddMenuItem(CreateHolidayRegionMenu((TodoData)data, actions));
            if (data is NoteData) menu.AddMenuItem(CreateNoteFontMenu((NoteData)data, actions));
            if (data is SearchData) AddSearchActions(menu);

            var built = menu.AddSeparator()
                .AddItem("Menu_Delete", actions.Delete, true, IconId.Delete, ThemeManager.DangerBrush)
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
                    Header = Localization.Format("UI_NewWidgetFormat", Localization.Get(KindNameKey(kind))),
                    Icon = MenuIcons.Create(KindIcon(kind.Id))
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

        private static IconId KindIcon(string kindId)
        {
            if (kindId == "todo") return IconId.Check;
            if (kindId == "note") return IconId.Edit;
            if (kindId == "search") return IconId.Search;
            return IconId.Folder;
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

        /// <summary>
        /// The folder's six-step icon-size picker (0.9/1.0/1.1/1.2/1.3/1.5).
        /// The checked entry follows Config.ItemScale; a pick writes the config
        /// and re-renders every widget (folders through ApplyItemScaleChange,
        /// so an open panel below the new minimum grows to it).
        /// </summary>
        private static MenuItem CreateItemSizeMenu()
        {
            var sizeItem = new MenuItem
            {
                Header = Localization.Get("Menu_ItemSize"),
                Icon = MenuIcons.Create(IconId.IconSize)
            };
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
                if (widget is FolderWidget folder)
                {
                    folder.ApplyItemScaleChange();
                    continue;
                }
                if (widget is WidgetGroupWindow group)
                {
                    // The item-size picker reaches the active grouped folder;
                    // the group keeps its own geometry.
                    group.ApplyItemScaleChange();
                    continue;
                }
                widget.UpdateUI();
            }
        }

        #endregion

        #region Kind-specific appendices (todo)

        /// <summary>
        /// Task 2.5: the todo widget's 假日地区 single-select submenu
        /// (中国/日本/美国/英国/关闭). The checked item follows the data through
        /// <see cref="PublicHolidays.NormalizeRegion"/> (missing/unknown = 中国),
        /// and a pick writes the region back through the widget's usual change
        /// path (repaint + persist). The day cells keep their height.
        /// </summary>
        private static MenuItem CreateHolidayRegionMenu(TodoData data, WidgetMenuActions actions)
        {
            var regionItem = new MenuItem
            {
                Header = Localization.Get("Menu_HolidayRegion"),
                Icon = MenuIcons.Create(IconId.Calendar)
            };
            string current = PublicHolidays.NormalizeRegion(data == null ? null : data.HolidayRegion);

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
                    if (data != null) data.HolidayRegion = id;
                    if (actions.Changed != null) actions.Changed();
                };
                regionItem.Items.Add(item);
            }
            return regionItem;
        }

        #endregion

        #region Kind-specific appendices (search)

        /// <summary>
        /// Task 9: the search widget owns the index actions. Rebuild asks the
        /// service directly (no confirmation here: the menu is the user's
        /// explicit request), and the pause entry flips to Resume while the
        /// service reports Paused. Labels are localization keys, never text.
        /// </summary>
        private static void AddSearchActions(MenuBuilder menu)
        {
            FileSearchIndexService service = FileSearchIndexService.Instance;
            menu.AddItem("Search_Rebuild", () => service.RequestRebuild(), true, IconId.Refresh);

            // The cancel entry is always present so the menu's shape is stable;
            // it only does something while a full scan is running.
            menu.AddItem("Search_Cancel", () => service.CancelScan(),
                service.Status.State == FileSearchState.Indexing, IconId.Close);

            bool paused = service.Status.State == FileSearchState.Paused;
            menu.AddItem(paused ? "Search_Resume" : "Search_Pause", () =>
            {
                if (service.Status.State == FileSearchState.Paused)
                {
                    service.Resume();
                }
                else
                {
                    service.Pause();
                }
            }, true, paused ? IconId.Play : IconId.Pause);
        }

        #endregion

        #region Kind-specific appendices (note)

        /// <summary>
        /// Task 6: the note widget's 字号 three-step picker (小 / 中 / 大 =
        /// 0.9 / 1.0 / 1.2 of <see cref="UiTokens.FontBody"/>). The checked
        /// entry follows the data's normalized scale (0.001 tolerance); a pick
        /// writes <see cref="NoteData.FontScale"/> and re-renders through the
        /// widget's usual change path (repaint + persist), so the document and
        /// the inline edit box rescale together while the home cards keep
        /// their own type.
        /// </summary>
        private static MenuItem CreateNoteFontMenu(NoteData data, WidgetMenuActions actions)
        {
            var root = new MenuItem
            {
                Header = Localization.Get("Menu_NoteFontSize"),
                Icon = MenuIcons.Create(IconId.TextSize)
            };
            AddFontEntry(root, data, actions, "Note_FontSmall", NoteFontScale.Small);
            AddFontEntry(root, data, actions, "Note_FontMedium", NoteFontScale.Medium);
            AddFontEntry(root, data, actions, "Note_FontLarge", NoteFontScale.Large);
            return root;
        }

        private static void AddFontEntry(MenuItem root, NoteData data, WidgetMenuActions actions,
            string key, double scale)
        {
            var item = new MenuItem
            {
                Header = Localization.Get(key),
                IsChecked = Math.Abs(NoteFontScale.Normalize(data.FontScale) - scale) < 0.001
            };
            item.Click += (s, e) =>
            {
                data.FontScale = scale;
                if (actions.Changed != null) actions.Changed();
            };
            root.Items.Add(item);
        }

        #endregion
    }
}
