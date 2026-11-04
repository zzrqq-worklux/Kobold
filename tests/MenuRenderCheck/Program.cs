using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kobold.Controls;
using Kobold.Core;
using Kobold.Core.Icons;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.MenuRenderCheck
{
    /// <summary>
    /// Builds the single folder-color menu entry offscreen and checks that its
    /// header is a swatch + label (the slot the app's MenuItem template
    /// renders) rather than a submenu. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/MenuRenderCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            // Redirect the singleton's config to a throwaway sandbox before
            // anything can construct it - the check must never touch the
            // user's real config.
            string configSandbox = Path.Combine(Path.GetTempPath(), "kobold-check-menurender");
            if (Directory.Exists(configSandbox)) Directory.Delete(configSandbox, true);
            Directory.CreateDirectory(configSandbox);
            WidgetManager.OverrideConfigPathsForChecks(
                Path.Combine(configSandbox, "config.json"),
                Path.Combine(configSandbox, "config.json.backup"));

            string tempFolder = Path.Combine(Path.GetTempPath(),
                "kobold-menurender-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempFolder);

            try
            {
                var app = new App();
                app.InitializeComponent();               // App.xaml: the menu styles
                WidgetManager.Instance.ToString();       // theme tokens
                WidgetPaletteMatchesFolderPalette();
                MenuTemplateHostsIconPresenter();
                GroupTabMenuCarriesTheGroupActions();

                var widget = new FolderWidget(new FolderData());
                WidgetMenuUsesIconPresenters(widget);
                MenuHeadersCarryNoGlyphPrefixes(widget);
                var factory = typeof(FolderWidget).GetMethod("CreateFolderColorEntry",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (factory == null) { Console.WriteLine("TOP FAIL: CreateFolderColorEntry not found"); return 1; }

                var item = new DisplayItem { Path = tempFolder, IsDirectory = true };
                var entry = factory.Invoke(widget, new object[] { item }) as MenuItem;
                if (entry == null) { Console.WriteLine("TOP FAIL: the folder color entry was not built"); return 1; }

                FolderColorEntryIsSingle(entry);
                FolderColorEntryUsesPalette(entry);

                Check(!FolderColor.HasCustomIcon(null),
                    "folder color: no custom icon hides the default");
                Check(FolderColor.HasCustomIcon("C:\\x.ico,0"),
                    "folder color: any custom icon offers the default");

                var rootMenu = (ContextMenu)typeof(FolderWidget)
                    .GetMethod("BuildRootItemMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(widget, new object[] { item });
                var browseMenu = (ContextMenu)typeof(FolderWidget)
                    .GetMethod("BuildBrowseItemMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(widget, new object[] { item, new List<DisplayItem> { item } });
                FolderColorEntryIsWired(rootMenu, "root");
                FolderColorEntryIsWired(browseMenu, "browse");
            }
            catch (Exception ex)
            {
                Console.WriteLine("TOP FAIL " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            finally
            {
                try { Directory.Delete(tempFolder, true); } catch { }
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL MENU-RENDER CHECKS PASSED");
                return 0;
            }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        /// <summary>
        /// T4 + T6: the group window's tab/current menu keeps the structure
        /// the group actions hang off - one switch entry per member, the live
        /// detach entry (enabled for an unlocked pair, disabled once the group
        /// is locked) and the hide-group entry.
        /// </summary>
        private static void GroupTabMenuCarriesTheGroupActions()
        {
            ThemeManager.Initialize("dark");

            var todo = new TodoData { Id = "todo-1", Name = "Todo", Color = "#6A9BCC" };
            var note = new NoteData { Id = "note-1", Name = "Note", Color = "#788C5D" };
            var group = new WidgetGroup { Id = "g1", ActiveWidgetId = "todo-1" };
            var window = new WidgetGroupWindow(group, new List<WidgetData> { todo, note });

            ContextMenu menu = window.BuildWidgetMenu(null);
            int switches = 0;
            int checkedSwitches = 0;
            MenuItem detach = null;
            MenuItem hide = null;

            foreach (var raw in menu.Items)
            {
                var item = raw as MenuItem;
                if (item == null) continue;
                var header = item.Header as string;
                if (header != null && header.StartsWith("切换到该标签", StringComparison.Ordinal))
                {
                    switches++;
                    if (item.IsChecked) checkedSwitches++;
                }
                else if (header == "分离此组件")
                {
                    detach = item;
                }
                else if (header == "隐藏整组")
                {
                    hide = item;
                }
            }

            Check(switches == 2, "group menu: one switch entry per member (got " + switches + ")");
            Check(checkedSwitches == 1, "group menu: exactly the active member is checked (got " + checkedSwitches + ")");
            Check(detach != null, "group menu: the detach entry exists");
            Check(detach != null && detach.IsEnabled,
                "group menu: the detach entry is enabled for an unlocked pair");
            Check(detach != null && detach.ToolTip is string,
                "group menu: the detach entry explains the drag-out/离开 action");
            Check(hide != null, "group menu: the hide-group entry exists");

            // T6: the same entry turns disabled while the group is locked.
            group.IsLocked = true;
            MenuItem lockedDetach = null;
            foreach (var raw in window.BuildWidgetMenu(null).Items)
            {
                var item = raw as MenuItem;
                if (item != null && string.Equals(item.Header as string, "分离此组件", StringComparison.Ordinal))
                {
                    lockedDetach = item;
                    break;
                }
            }
            Check(lockedDetach != null, "group menu: a locked group still shows the detach entry");
            Check(lockedDetach != null && !lockedDetach.IsEnabled,
                "group menu: the detach entry is disabled for a locked group");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        /// <summary>
        /// The implicit MenuItem template must reserve an icon column and host
        /// an IconPresenter; before Claude Warm it had no icon slot at all.
        /// </summary>
        private static void MenuTemplateHostsIconPresenter()
        {
            var style = Application.Current.TryFindResource(typeof(MenuItem)) as Style;
            if (style == null)
            {
                Errors.Add("menu template: the implicit MenuItem style is missing");
                return;
            }

            var item = new MenuItem
            {
                Header = "template-check",
                Icon = MenuIcons.Create(IconId.Eye)
            };
            item.Style = style;
            item.Measure(new Size(240, 48));
            item.Arrange(new Rect(0, 0, 240, 48));
            item.UpdateLayout();
            item.ApplyTemplate();

            Check(FindVisual<IconPresenter>(item) != null,
                "menu template: hosts an IconPresenter (icon/check/arrow column)");

            var iconHost = item.Template.FindName("IconHost", item) as ContentPresenter;
            var headerHost = item.Template.FindName("HeaderHost", item) as ContentPresenter;
            if (iconHost == null || headerHost == null)
            {
                Errors.Add("menu template: IconHost/HeaderHost were not found");
                return;
            }

            Check(iconHost.ActualWidth >= 15.5,
                "menu template: the icon column is 16px wide (got " + iconHost.ActualWidth + ")");

            double iconLeft = iconHost.TranslatePoint(new Point(0, 0), item).X;
            double iconRight = iconHost.TranslatePoint(new Point(iconHost.ActualWidth, 0), item).X;
            double headerLeft = headerHost.TranslatePoint(new Point(0, 0), item).X;
            Check(headerLeft >= iconRight,
                "menu template: the header starts after the icon (icon right " + iconRight +
                ", header left " + headerLeft + ")");
            Check(iconLeft <= 10,
                "menu template: the icon starts near the menu edge (got " + iconLeft + ")");
            Check(headerLeft - iconLeft <= 27,
                "menu template: no empty check gutter (icon " + iconLeft + ", header " + headerLeft + ")");
        }

        /// <summary>
        /// Every icon the common widget menu puts on an item must be an
        /// IconPresenter whose id has both baked styles. The check also pins
        /// that the menu actually carries icons (it used to carry none).
        /// </summary>
        private static void WidgetMenuUsesIconPresenters(FolderWidget widget)
        {
            ContextMenu menu = WidgetMenuBuilder.Build(widget, null);
            int icons = 0;
            int bad = 0;
            foreach (var item in Flatten(menu.Items))
            {
                if (item.Icon == null) continue;
                icons++;
                var presenter = item.Icon as IconPresenter;
                if (presenter == null)
                {
                    bad++;
                    continue;
                }

                if (string.IsNullOrEmpty(IconCatalog.GetPathData(presenter.Icon, false)) ||
                    string.IsNullOrEmpty(IconCatalog.GetPathData(presenter.Icon, true)))
                {
                    bad++;
                }
            }

            Check(icons >= 5, "widget menu: carries at least five IconPresenter icons (got " + icons + ")");
            Check(bad == 0, "widget menu: every icon is an IconPresenter with both styles (" + bad + " bad)");
        }

        private static IEnumerable<MenuItem> Flatten(ItemCollection items)
        {
            foreach (var raw in items)
            {
                var item = raw as MenuItem;
                if (item == null) continue;
                yield return item;
                foreach (var child in Flatten(item.Items)) yield return child;
            }
        }

        private static void MenuHeadersCarryNoGlyphPrefixes(FolderWidget widget)
        {
            var rootMenu = (ContextMenu)typeof(FolderWidget)
                .GetMethod("BuildRootItemMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(widget, new object[] { new DisplayItem { Path = "C:\\missing", IsDirectory = false } });

            int bad = 0;
            foreach (var item in Flatten(rootMenu.Items))
            {
                string header = item.Header as string;
                if (header == null) continue;
                if (header.StartsWith("＋", StringComparison.Ordinal) ||
                    header.IndexOf("✓", StringComparison.Ordinal) >= 0)
                {
                    bad++;
                }
            }

            Check(bad == 0, "menus: no ＋/✓ text glyphs in headers (" + bad + " bad)");
        }

        private static T FindVisual<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var match = child as T;
                if (match != null) return match;
                var nested = FindVisual<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static void FolderColorEntryIsSingle(MenuItem entry)
        {
            if (entry.Items.Count != 0)
                Errors.Add("the folder color entry has a submenu");
            if (!"FolderColorEntry".Equals(entry.Tag as string))
                Errors.Add("the folder color entry is not tagged");
            var header = entry.Header as string;
            if (header == null || header.IndexOf(Localization.Get("Menu_FolderColor"), StringComparison.Ordinal) < 0)
                Errors.Add("the folder color entry has no localized label");
        }

        private static void FolderColorEntryUsesPalette(MenuItem entry)
        {
            var icon = entry.Icon as IconPresenter;
            if (icon == null)
                Errors.Add("the folder color entry has no IconPresenter");
            else if (icon.Icon != IconId.Palette)
                Errors.Add("the folder color entry icon is " + icon.Icon + " (expected Palette)");
            else
                Console.WriteLine("PASS folder color entry: Palette IconPresenter");
        }

        private static void FolderColorEntryIsWired(ContextMenu menu, string where)
        {
            foreach (var raw in menu.Items)
            {
                var entry = raw as MenuItem;
                if (entry == null || !"FolderColorEntry".Equals(entry.Tag as string)) continue;
                if (entry.Items.Count != 0)
                    Errors.Add("the " + where + " folder color entry has a submenu");
                return;
            }
            Errors.Add("the " + where + " menu has no folder color entry");
        }

        /// <summary>
        /// Pins the widget colour picker to the folder-colour picker: the same
        /// nine hues in the same order, so the two pickers read as one family.
        /// </summary>
        private static void WidgetPaletteMatchesFolderPalette()
        {
            var widgets = UiTokens.FolderPalette;
            var folders = UiTokens.FolderIconPalette;

            Check(widgets.Length == 9,
                "widget palette: offers nine colours (got " + widgets.Length + ")");
            Check(widgets.Length == folders.Length,
                "widget palette: same count as the folder palette (" + widgets.Length +
                " vs " + folders.Length + ")");

            for (int i = 0; i < widgets.Length && i < folders.Length; i++)
            {
                Check(string.Equals(widgets[i], folders[i], StringComparison.OrdinalIgnoreCase),
                    "widget palette: entry " + i + " matches the folder palette (" +
                    widgets[i] + " vs " + folders[i] + ")");
            }

            // Hard-coded so breaking both arrays at once still trips the pin.
            var expected = new[]
            {
                "#CC785C", "#B5654A", "#D4A017", "#788C5D", "#5DB8A6",
                "#6A9BCC", "#9C7CA5", "#B56576", "#948B7D"
            };
            Check(widgets.Length == expected.Length,
                "widget palette: matches the agreed nine-colour count");
            for (int i = 0; i < widgets.Length && i < expected.Length; i++)
            {
                Check(string.Equals(widgets[i], expected[i], StringComparison.OrdinalIgnoreCase),
                    "widget palette: entry " + i + " is the agreed hex " + expected[i]);
            }
        }
    }
}
