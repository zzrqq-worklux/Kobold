using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Kobold.Helpers
{
    /// <summary>
    /// Builds colored folder icons from the icon Windows itself shows for a
    /// folder, so the result matches the current OS look. Colors are cached as
    /// multi-size PNG-in-ICO files; desktop.ini points at the cached file.
    /// </summary>
    public static class FolderIconFactory
    {
        private static readonly int[] WantedSizes = { 16, 32, 48, 256 };

        /// <summary>
        /// Ensures a colored folder icon exists in outputDir and returns its path,
        /// or null when the color is unusable or the OS icon cannot be read.
        /// </summary>
        public static string EnsureIcon(string colorHex, string outputDir)
        {
            if (string.IsNullOrWhiteSpace(colorHex) || string.IsNullOrWhiteSpace(outputDir)) return null;

            try
            {
                Directory.CreateDirectory(outputDir);
                string path = Path.Combine(outputDir, FileNameFor(colorHex));
                if (File.Exists(path)) return path;

                byte[] bytes = BuildIconBytes(colorHex);
                if (bytes == null) return null;

                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The .ico bytes for one color, or null when it cannot be built.</summary>
        public static byte[] BuildIconBytes(string colorHex)
        {
            Color target;
            try { target = (Color)ColorConverter.ConvertFromString(colorHex); }
            catch (Exception) { return null; }
            if (target.A == 0) return null;

            var frames = LoadSystemFolderIcons();
            if (frames.Count == 0) return null;

            var recolored = new List<BitmapSource>();
            foreach (var frame in frames)
            {
                var result = Recolor(frame, target);
                if (result != null) recolored.Add(result);
            }
            if (recolored.Count == 0) return null;

            return EncodeIco(recolored);
        }

        private static string FileNameFor(string colorHex)
        {
            // The OS build is part of the name: a Windows update changes the folder
            // art, and an icon cached from the old art must not survive it.
            return colorHex.TrimStart('#').ToUpperInvariant() + "-" + OsBuild() + ".ico";
        }

        private static string OsBuild()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var build = key == null ? null : key.GetValue("CurrentBuildNumber") as string;
                    return string.IsNullOrEmpty(build) ? "0" : build;
                }
            }
            catch (Exception)
            {
                return "0";
            }
        }

        #region System icon extraction

        private const uint ShgfiIcon = 0x00000100;
        private const uint ShgfiSmallIcon = 0x00000001;
        private const uint ShgfiLargeIcon = 0x00000000;
        private const uint ShgfiUseFileAttributes = 0x00000010;
        private const uint ShgfiSysIconIndex = 0x00004000;
        private const uint FileAttributeDirectory = 0x00000010;
        private const int ShilExtralarge = 2;
        private const int ShilJumbo = 4;
        private const int IldTransparent = 0x00000001;

        /// <summary>
        /// The folder icon as Windows draws it, at every size we can get: 16/32
        /// from the shell and 48/256 from the system image list. Sizes the OS
        /// refuses are scaled up from the largest one we did read.
        /// </summary>
        private static List<BitmapSource> LoadSystemFolderIcons()
        {
            var found = new Dictionary<int, BitmapSource>();

            AddFromHIcon(found, 16, ShellFolderIcon(ShgfiSmallIcon));
            AddFromHIcon(found, 32, ShellFolderIcon(ShgfiLargeIcon));

            int index = ShellFolderIconIndex();
            if (index >= 0)
            {
                AddFromImageList(found, 48, ShilExtralarge, index);
                AddFromImageList(found, 256, ShilJumbo, index);
            }

            if (found.Count == 0) return new List<BitmapSource>();

            int largest = 0;
            foreach (var size in found.Keys)
            {
                if (size > largest) largest = size;
            }

            var result = new List<BitmapSource>();
            foreach (var size in WantedSizes)
            {
                BitmapSource exact;
                result.Add(found.TryGetValue(size, out exact) ? exact : Scale(found[largest], size));
            }
            return result;
        }

        private static void AddFromHIcon(Dictionary<int, BitmapSource> found, int size, IntPtr hicon)
        {
            if (hicon == IntPtr.Zero) return;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                if (source.PixelWidth > 0) found[size] = source;
            }
            catch (Exception)
            {
                // Keep whatever else we managed to read.
            }
            finally
            {
                DestroyIcon(hicon);
            }
        }

        private static void AddFromImageList(Dictionary<int, BitmapSource> found, int size, int listId, int index)
        {
            IImageList list = null;
            try
            {
                var iid = typeof(IImageList).GUID;
                if (SHGetImageList(listId, ref iid, out list) != 0 || list == null) return;

                IntPtr hicon;
                if (list.GetIcon(index, IldTransparent, out hicon) != 0) return;
                AddFromHIcon(found, size, hicon);
            }
            catch (Exception)
            {
                // A refused image list just means this size gets scaled instead.
            }
            finally
            {
                if (list != null) Marshal.ReleaseComObject(list);
            }
        }

        private static IntPtr ShellFolderIcon(uint flags)
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo("folder", FileAttributeDirectory, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                ShgfiIcon | flags | ShgfiUseFileAttributes);
            return result == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
        }

        private static int ShellFolderIconIndex()
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo("folder", FileAttributeDirectory, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                ShgfiSysIconIndex | ShgfiLargeIcon | ShgfiUseFileAttributes);
            return result == IntPtr.Zero ? -1 : info.iIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int index);
            [PreserveSig] int ReplaceIcon(int index, IntPtr hicon, out int newIndex);
            [PreserveSig] int SetOverlayImage(int imageIndex, int overlayIndex);
            [PreserveSig] int Replace(int index, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int index);
            [PreserveSig] int Draw(IntPtr drawParams);
            [PreserveSig] int Remove(int index);
            [PreserveSig] int GetIcon(int index, int flags, out IntPtr hicon);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes,
            ref SHFILEINFO info, uint infoSize, uint flags);

        // SHGetImageList is exported by ordinal only.
        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int imageListId, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IImageList imageList);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hicon);

        #endregion

        #region Recoloring

        private static BitmapSource Scale(BitmapSource source, int size)
        {
            if (source.PixelWidth <= 0) return source;

            var scaled = new TransformedBitmap(source,
                new ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var result = new WriteableBitmap(converted);
            result.Freeze();
            return result;
        }

        /// <summary>
        /// Replaces the hue of every saturated pixel with the target hue, keeping
        /// the icon's own shading (its value and relative saturation). Greys,
        /// highlights and shadows are left alone.
        /// </summary>
        private static BitmapSource Recolor(BitmapSource source, Color target)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            if (width == 0 || height == 0) return null;

            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            double targetHue, targetSat, targetVal;
            RgbToHsv(target.R, target.G, target.B, out targetHue, out targetSat, out targetVal);

            double baseSat = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;

                double hh, ss, vv;
                RgbToHsv(pixels[i + 2], pixels[i + 1], pixels[i], out hh, out ss, out vv);
                if (ss > baseSat) baseSat = ss;
            }
            if (baseSat <= 0.05) return converted; // nothing saturated to recolor

            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;

                double hh, ss, vv;
                RgbToHsv(pixels[i + 2], pixels[i + 1], pixels[i], out hh, out ss, out vv);
                if (ss < 0.12) continue; // outline, highlight, shadow

                double sat = Math.Max(0, Math.Min(1, ss * (targetSat / baseSat)));
                byte r, g, b;
                HsvToRgb(targetHue, sat, vv, out r, out g, out b);
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }

            var bitmap = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
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

        private static void HsvToRgb(double hue, double sat, double val, out byte r, out byte g, out byte b)
        {
            double c = val * sat;
            double x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
            double m = val - c;

            double rr, gg, bb;
            if (hue < 60) { rr = c; gg = x; bb = 0; }
            else if (hue < 120) { rr = x; gg = c; bb = 0; }
            else if (hue < 180) { rr = 0; gg = c; bb = x; }
            else if (hue < 240) { rr = 0; gg = x; bb = c; }
            else if (hue < 300) { rr = x; gg = 0; bb = c; }
            else { rr = c; gg = 0; bb = x; }

            r = ToByte(rr + m);
            g = ToByte(gg + m);
            b = ToByte(bb + m);
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, Math.Round(value * 255)));
        }

        #endregion

        #region ICO encoding

        /// <summary>
        /// Packs the frames into one .ico. Every entry is a PNG - Windows has
        /// accepted PNG-compressed icon entries since Vista.
        /// </summary>
        private static byte[] EncodeIco(List<BitmapSource> frames)
        {
            var blobs = new List<byte[]>();
            foreach (var frame in frames)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using (var buffer = new MemoryStream())
                {
                    encoder.Save(buffer);
                    blobs.Add(buffer.ToArray());
                }
            }

            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                writer.Write((ushort)0);              // reserved
                writer.Write((ushort)1);              // type: icon
                writer.Write((ushort)frames.Count);

                int offset = 6 + frames.Count * 16;
                for (int i = 0; i < frames.Count; i++)
                {
                    int size = frames[i].PixelWidth;
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)(size >= 256 ? 0 : frames[i].PixelHeight));
                    writer.Write((byte)0);            // palette size
                    writer.Write((byte)0);            // reserved
                    writer.Write((ushort)1);          // colour planes
                    writer.Write((ushort)32);         // bits per pixel
                    writer.Write(blobs[i].Length);
                    writer.Write(offset);
                    offset += blobs[i].Length;
                }

                foreach (var blob in blobs) writer.Write(blob);
                writer.Flush();
                return buffer.ToArray();
            }
        }

        #endregion
    }
}
