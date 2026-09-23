using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.FolderIconCheck
{
    /// <summary>
    /// Checks the folder icon factory: every palette color produces a valid
    /// multi-size .ico whose saturated pixels carry the requested hue.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FolderIconCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            _root = Path.Combine(Path.GetTempPath(), "kobold-foldericon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            try
            {
                EveryPaletteColorBuildsAnIcon();
                EnsureIconIsCachedAndStable();
                BadInputIsRejected();
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch { }
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL FOLDER-ICON CHECKS PASSED");
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

        private static void EveryPaletteColorBuildsAnIcon()
        {
            foreach (var hex in UiTokens.FolderIconPalette)
            {
                byte[] bytes = FolderIconFactory.BuildIconBytes(hex);
                if (bytes == null)
                {
                    Errors.Add("build " + hex + ": BuildIconBytes returned null");
                    continue;
                }

                var frames = DecodeFrames(bytes, hex);
                if (frames == null) continue;

                Check(frames.Count >= 2, "build " + hex + ": at least two sizes");
                Check(frames.Max(f => f.PixelWidth) >= 48, "build " + hex + ": a size >= 48px");
                Console.WriteLine("INFO " + hex + ": sizes=" +
                    string.Join(",", frames.Select(f => f.PixelWidth.ToString()).ToArray()));

                var largest = frames.OrderByDescending(f => f.PixelWidth).First();
                double wantHue, wantSat, wantVal;
                var target = (Color)ColorConverter.ConvertFromString(hex);
                RgbToHsv(target.R, target.G, target.B, out wantHue, out wantSat, out wantVal);

                double gotHue;
                Check(DominantHue(largest, wantSat, out gotHue) &&
                      Math.Abs(HueDelta(gotHue, wantHue)) <= 12,
                    "build " + hex + ": dominant hue " + gotHue.ToString("F0") +
                    " matches target " + wantHue.ToString("F0"));
            }
        }

        private static void EnsureIconIsCachedAndStable()
        {
            string outDir = Path.Combine(_root, "icons");
            string first = FolderIconFactory.EnsureIcon("#EF4444", outDir);
            Check(first != null && File.Exists(first), "ensure: writes the icon file");
            if (first == null) return;

            var written = File.ReadAllBytes(first);
            string again = FolderIconFactory.EnsureIcon("#EF4444", outDir);
            Check(again == first && File.ReadAllBytes(again).SequenceEqual(written),
                "ensure: a second call reuses the cached file");
            Check(Path.GetFileName(first).StartsWith("EF4444-", StringComparison.OrdinalIgnoreCase),
                "ensure: the cache file is named after the color and the OS build");
        }

        private static void BadInputIsRejected()
        {
            Check(FolderIconFactory.EnsureIcon(null, Path.Combine(_root, "icons")) == null,
                "bad input: a null color returns null");
            Check(FolderIconFactory.EnsureIcon("#EF4444", null) == null,
                "bad input: a null output directory returns null");
            Check(FolderIconFactory.BuildIconBytes("not-a-color") == null,
                "bad input: an unparsable color returns null");
        }

        private static List<BitmapSource> DecodeFrames(byte[] bytes, string what)
        {
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    return decoder.Frames.Cast<BitmapSource>().ToList();
                }
            }
            catch (Exception ex)
            {
                Errors.Add("build " + what + ": decoding failed - " + ex.Message);
                return null;
            }
        }

        /// <summary>Hue of the most saturated pixel (the folder body).</summary>
        private static bool DominantHue(BitmapSource source, double targetSat, out double hue)
        {
            hue = 0;
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            double bestSat = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 200) continue;

                double hh, ss, vv;
                RgbToHsv(pixels[i + 2], pixels[i + 1], pixels[i], out hh, out ss, out vv);
                if (ss > bestSat) { bestSat = ss; hue = hh; }
            }

            // A muted target (the grey) is barely saturated by design, so the
            // threshold follows the target instead of a fixed 20%.
            return bestSat > Math.Max(0.1, targetSat * 0.6);
        }

        private static double HueDelta(double a, double b)
        {
            double delta = Math.Abs(a - b) % 360;
            return delta > 180 ? 360 - delta : delta;
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double hue, out double sat, out double val)
        {
            double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
            double max = Math.Max(rr, Math.Max(gg, bb));
            double min = Math.Min(rr, Math.Min(gg, bb));
            val = max;
            sat = max <= 0 ? 0 : (max - min) / max;
            if (sat <= 0) { hue = 0; return; }

            double d = max - min;
            if (max == rr) hue = 60 * (((gg - bb) / d) % 6);
            else if (max == gg) hue = 60 * ((bb - rr) / d + 2);
            else hue = 60 * ((rr - gg) / d + 4);
            if (hue < 0) hue += 360;
        }
    }
}
