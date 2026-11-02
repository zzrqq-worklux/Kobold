using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kobold.Controls;
using Kobold.Core.Icons;

namespace Kobold.IconCatalogCheck
{
    /// <summary>
    /// Guards the 24px icon layer: every IconId has a classic and a
    /// hand-drawn geometry, both parse, both have real bounds, and the
    /// presenter actually paints pixels with either style. Run via:
    /// dotnet run --project tests/IconCatalogCheck
    /// </summary>
    internal static class Program
    {
        private const int ExpectedCount = 35;
        private static readonly List<string> Errors = new List<string>();

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            var ids = Enum.GetValues(typeof(IconId)).Cast<IconId>().ToArray();
            Check(ids.Length == ExpectedCount,
                "catalog: exposes " + ids.Length + " ids (expected " + ExpectedCount + ")");

            foreach (var id in ids)
            {
                CheckIcon(id, handDrawn: false);
                CheckIcon(id, handDrawn: true);
                CheckDifferent(id);
            }

            CheckPresenter(IconId.Settings, "classic");
            CheckPresenter(IconId.Settings, "handdrawn");
            CheckPresenter(IconId.Chevron, "handdrawn");
            CheckPresenterScalesToSize();
            CheckStyleManagerNormalizes();

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL ICON-CATALOG CHECKS PASSED");
                return 0;
            }

            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var error in Errors) Console.WriteLine("  - " + error);
            return 1;
        }

        private static void CheckIcon(IconId id, bool handDrawn)
        {
            string style = handDrawn ? "handdrawn" : "classic";
            string path = IconCatalog.GetPathData(id, handDrawn);
            if (string.IsNullOrWhiteSpace(path))
            {
                Errors.Add(id + " [" + style + "]: empty path data");
                return;
            }

            Geometry geometry;
            try
            {
                geometry = IconCatalog.Get(id, handDrawn);
            }
            catch (Exception ex)
            {
                Errors.Add(id + " [" + style + "]: " + ex.GetType().Name + " " + ex.Message);
                return;
            }

            if (geometry == null)
            {
                Errors.Add(id + " [" + style + "]: null geometry");
                return;
            }

            Rect bounds = geometry.Bounds;
            if (bounds.IsEmpty || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height) ||
                bounds.Width < 0.5 || bounds.Height < 0.5)
            {
                Errors.Add(id + " [" + style + "]: empty bounds " + bounds);
                return;
            }

            if (bounds.Left < -2 || bounds.Top < -2 || bounds.Right > 26 || bounds.Bottom > 26)
            {
                Errors.Add(id + " [" + style + "]: out of the 24px canvas " + bounds);
            }
        }

        private static void CheckDifferent(IconId id)
        {
            string classic = IconCatalog.GetPathData(id, false);
            string handDrawn = IconCatalog.GetPathData(id, true);
            if (string.Equals(classic, handDrawn, StringComparison.Ordinal))
            {
                Errors.Add(id + ": the hand-drawn path is identical to classic");
            }
        }

        private static void CheckPresenter(IconId id, string style)
        {
            const int size = 24;
            var presenter = new IconPresenter
            {
                Icon = id,
                IconStyle = style,
                Size = size,
                Brush = Brushes.Black
            };

            presenter.Measure(new Size(size, size));
            presenter.Arrange(new Rect(0, 0, size, size));
            presenter.UpdateLayout();

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(presenter);

            int painted = CountPaintedPixels(bitmap);
            Check(painted > 20, "presenter: " + id + " [" + style + "] painted " + painted + " pixels");
        }

        /// <summary>
        /// A 15 DIP presenter must draw the whole 24x24 geometry scaled down,
        /// not a clipped 24x24 top-left corner. The Grid icon's bottom-right
        /// square sits around x/y 9..13 after scaling; a clipped 24x24 draw
        /// leaves that region empty (its square starts at 14).
        /// </summary>
        private static void CheckPresenterScalesToSize()
        {
            const int size = 15;
            var presenter = new IconPresenter
            {
                Icon = IconId.Grid,
                IconStyle = "classic",
                Size = size,
                Brush = Brushes.Black
            };
            presenter.Measure(new Size(size, size));
            presenter.Arrange(new Rect(0, 0, size, size));
            presenter.UpdateLayout();

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(presenter);

            var pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);
            int painted = 0;
            for (int y = 9; y < size; y++)
            {
                for (int x = 9; x < size; x++)
                {
                    int alpha = pixels[(y * size + x) * 4 + 3];
                    if (alpha > 0) painted++;
                }
            }

            Check(painted > 0,
                "presenter: scales the full 24px geometry at 15px (bottom-right grid square painted: " + painted + ")");
        }

        private static int CountPaintedPixels(BitmapSource bitmap)
        {
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);

            int painted = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 0) painted++;
            }
            return painted;
        }

        private static void CheckStyleManagerNormalizes()
        {
            string original = IconStyleManager.Current;
            try
            {
                IconStyleManager.Set("handdrawn");
                Check(IconStyleManager.IsHandDrawn, "style: handdrawn is recognised");
                IconStyleManager.Set("not-a-style");
                Check(!IconStyleManager.IsHandDrawn, "style: an unknown id falls back to classic");
            }
            finally
            {
                IconStyleManager.Set(original);
            }
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what);
            else Errors.Add(what);
        }
    }
}
