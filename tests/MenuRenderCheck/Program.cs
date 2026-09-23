using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kobold.Controls;
using Kobold.Core;

namespace Kobold.MenuRenderCheck
{
    /// <summary>
    /// Renders the folder-color menu entries offscreen and checks that each one
    /// really paints its swatch. The app's MenuItem template (App.xaml) renders
    /// only the header and the submenu arrow, so an entry built on MenuItem.Icon
    /// comes out blank - this check exists because exactly that shipped once.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/MenuRenderCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            string tempFolder = Path.Combine(Path.GetTempPath(),
                "kobold-menurender-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempFolder);

            try
            {
                var app = new App();
                app.InitializeComponent();               // App.xaml: the menu styles
                WidgetManager.Instance.ToString();       // theme tokens

                var widget = new FolderWidget(new FolderData());
                var factory = typeof(FolderWidget).GetMethod("CreateFolderColorMenu",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (factory == null)
                {
                    Console.WriteLine("TOP FAIL: FolderWidget.CreateFolderColorMenu not found");
                    return 1;
                }

                var item = new DisplayItem { Path = tempFolder, IsDirectory = true };
                var menu = factory.Invoke(widget, new object[] { item }) as MenuItem;
                if (menu == null)
                {
                    Console.WriteLine("TOP FAIL: the folder color menu was not built");
                    return 1;
                }

                SwatchesArePainted(menu);
                ResetEntryIsLabelled(menu);
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

        private static void SwatchesArePainted(MenuItem menu)
        {
            var colors = UiTokens.FolderIconPalette;
            var names = UiTokens.FolderIconPaletteNames;
            Check(colors.Length == names.Length,
                "palette: every colour carries a name key (" + colors.Length + " colours, " +
                names.Length + " names)");

            Check(menu.Items.Count == colors.Length + 2,
                "menu: " + colors.Length + " colors, a separator and the reset entry (got " +
                menu.Items.Count + ")");

            for (int i = 0; i < colors.Length && i < menu.Items.Count; i++)
            {
                var entry = menu.Items[i] as MenuItem;
                if (entry == null)
                {
                    Errors.Add("entry " + colors[i] + ": not a menu item");
                    continue;
                }

                Check(SwatchOf(entry) != null,
                    "entry " + colors[i] + ": the swatch rides in the header (the slot the template renders)");

                string label = LabelOf(entry);
                Check(!string.IsNullOrWhiteSpace(label) && label != names[i],
                    "entry " + colors[i] + ": carries its name (" + label + ")");

                var painted = PaintedColor(entry);
                var wanted = (Color)ColorConverter.ConvertFromString(colors[i]);
                Check(painted != null && SameColor(painted.Value, wanted),
                    "entry " + colors[i] + ": renders " + Describe(painted) + ", wanted " + Describe(wanted));
            }

            // A key that does not resolve comes back as the key itself, and a
            // mis-aligned name array shows up as duplicates.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < colors.Length && i < menu.Items.Count; i++)
            {
                string label = LabelOf(menu.Items[i] as MenuItem);
                Check(label != null && seen.Add(label),
                    "entry " + colors[i] + ": the name is unique (" + label + ")");
            }
        }

        /// <summary>The coloured square inside a menu entry's header, if any.</summary>
        private static System.Windows.Shapes.Rectangle SwatchOf(MenuItem entry)
        {
            return HeaderPart<System.Windows.Shapes.Rectangle>(entry);
        }

        /// <summary>The colour name inside a menu entry's header, if any.</summary>
        private static string LabelOf(MenuItem entry)
        {
            var text = HeaderPart<System.Windows.Controls.TextBlock>(entry);
            return text == null ? null : text.Text;
        }

        private static T HeaderPart<T>(MenuItem entry) where T : System.Windows.DependencyObject
        {
            var panel = entry == null ? null : entry.Header as System.Windows.Controls.Panel;
            if (panel == null) return null;

            foreach (var child in panel.Children)
            {
                var match = child as T;
                if (match != null) return match;
            }
            return null;
        }

        private static void ResetEntryIsLabelled(MenuItem menu)
        {
            int index = UiTokens.FolderIconPalette.Length + 1;
            if (menu.Items.Count <= index)
            {
                Errors.Add("reset: the entry is missing");
                return;
            }

            var reset = menu.Items[index] as MenuItem;
            Check(reset != null && reset.Header is string && !string.IsNullOrEmpty((string)reset.Header),
                "reset: keeps a text header");
            Check(reset != null && !reset.IsEnabled,
                "reset: disabled while the folder shows the default icon");
        }

        /// <summary>
        /// Paints one menu item offscreen and returns the colour of its most
        /// saturated pixel - the swatch - or null when nothing was drawn.
        /// </summary>
        private static Color? PaintedColor(MenuItem entry)
        {
            try
            {
                entry.ApplyTemplate();
                entry.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                entry.Arrange(new Rect(new Point(0, 0), entry.DesiredSize));
                entry.UpdateLayout();

                int width = (int)Math.Ceiling(entry.ActualWidth);
                int height = (int)Math.Ceiling(entry.ActualHeight);
                if (width <= 0 || height <= 0) return null;

                var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                target.Render(entry);

                var pixels = new byte[width * height * 4];
                target.CopyPixels(pixels, width * 4, 0);

                Color? best = null;
                double bestSat = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] < 200) continue;

                    double hue, sat, val;
                    RgbToHsv(pixels[i + 2], pixels[i + 1], pixels[i], out hue, out sat, out val);
                    if (sat > bestSat)
                    {
                        bestSat = sat;
                        best = Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                    }
                }
                return best;
            }
            catch (Exception ex)
            {
                Errors.Add("render failed: " + ex.Message);
                return null;
            }
        }

        private static bool SameColor(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) <= 6 && Math.Abs(a.G - b.G) <= 6 && Math.Abs(a.B - b.B) <= 6;
        }

        private static string Describe(Color? color)
        {
            return color == null
                ? "nothing"
                : "#" + color.Value.R.ToString("X2") + color.Value.G.ToString("X2") + color.Value.B.ToString("X2");
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double hue, out double sat, out double val)
        {
            double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
            double max = Math.Max(rr, Math.Max(gg, bb));
            double min = Math.Min(rr, Math.Min(gg, bb));
            val = max;
            sat = max <= 0 ? 0 : (max - min) / max;
            hue = 0;
        }
    }
}
