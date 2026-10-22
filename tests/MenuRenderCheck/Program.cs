using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using Kobold.Controls;
using Kobold.Core;
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

                var widget = new FolderWidget(new FolderData());
                var factory = typeof(FolderWidget).GetMethod("CreateFolderColorEntry",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (factory == null) { Console.WriteLine("TOP FAIL: CreateFolderColorEntry not found"); return 1; }

                var item = new DisplayItem { Path = tempFolder, IsDirectory = true };
                var entry = factory.Invoke(widget, new object[] { item }) as MenuItem;
                if (entry == null) { Console.WriteLine("TOP FAIL: the folder color entry was not built"); return 1; }

                FolderColorEntryIsSingle(entry);
                FolderColorEntryPaintsItsSwatch(entry);

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

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }

        private static void FolderColorEntryIsSingle(MenuItem entry)
        {
            if (entry.Items.Count != 0)
                Errors.Add("the folder color entry has a submenu");
            if (!"FolderColorEntry".Equals(entry.Tag as string))
                Errors.Add("the folder color entry is not tagged");
            var header = entry.Header as StackPanel;
            if (header == null || header.Children.Count < 2)
                Errors.Add("the folder color entry header is not a swatch + label");
        }

        private static void FolderColorEntryPaintsItsSwatch(MenuItem entry)
        {
            var header = entry.Header as StackPanel;
            var swatch = header == null ? null : header.Children[0] as System.Windows.Shapes.Rectangle;
            if (swatch == null || swatch.Width < 16 || swatch.Height < 16)
                Errors.Add("the folder color entry has no 16px swatch");
            var label = header == null ? null : header.Children[1] as TextBlock;
            if (label == null || label.Text.IndexOf(Localization.Get("Menu_FolderColor"), StringComparison.Ordinal) < 0)
                Errors.Add("the folder color entry has no localized label");
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
                "#EF4444", "#F97316", "#F59E0B", "#84CC16", "#22C55E",
                "#06B6D4", "#3B82F6", "#8B5CF6", "#94A3B8"
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
