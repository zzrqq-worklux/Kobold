# Kobold 文件夹颜色（Folder Color）实现计划

> **状态（2026-09-23）**：待执行。设计已在会话中与用户确认：**图标来源 = 运行时从系统文件夹图标生成**（方案 A）；
> 入口 = 浏览态与根态条目右键菜单；调色板 = 9 色（复用 `UiTokens.FolderPalette` 的 8 个色相 + 新增 1 个灰）。
> **提交约定**：每步末尾给出 commit 命令，但**执行前必须获得用户明确许可**。
> **工作区**：`.worktrees/folder-color`（分支 `feat/folder-color`，从 `feat/hide-from-switcher` 叠出，部署时含全部改动）。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在组件面板里右键任意文件夹 → 「文件夹颜色」→ 选色 / 恢复默认；效果全局生效（Explorer、面板与所有显示 shell 图标的地方）。

**Architecture:** 文件夹"颜色"是**图标替换**：`desktop.ini` 的 `[.ShellClassInfo] IconResource` 指向一个彩色 .ico，文件夹必须带 **System 属性** Explorer 才会读它。彩色图标由 `FolderIconFactory` 在运行时从**系统自身的文件夹图标**取多尺寸位图、把饱和像素的色相替换成目标色、编码成 PNG-in-ICO 缓存在 `%AppData%\Kobold\FolderIcons`。`FolderColor` 封装 `SHGetSetFolderCustomSettings` 的写/读与 desktop.ini 的安全恢复（保留用户自定义段），并做特殊文件夹保护与 shell 刷新。

**Tech Stack:** WPF (net48)、C# 7.3、`SHGetSetFolderCustomSettings` / `PathMakeSystemFolder` / `SHChangeNotify`（shell32/shlwapi）、`SHGetFileInfo` + `SHGetImageList`（图标提取）、WPF `BitmapSource` + `PngBitmapEncoder`（重着色与 ICO 编码）、现有控制台自检（`tests/*Check`）。

**Spec:** 无独立 spec —— 设计在会话中逐条确认；Folcolor（kweatherman/Folcolor，MIT）的实现作为参考（`src/Controller/FolderColorize.cpp`）。

## Global Constraints

- 目标框架 net48，`LangVersion 7.3`（不要使用 C# 8+ 语法）。
- 所有颜色字面量只能出现在 `Core/UiTokens.cs`（`tests/UiTokensCheck` 会扫描 `.cs`/`.xaml`）；本功能不新增 XAML。
- 新增 UI 文案走 `Localization`，en/zh/ja 三语齐全（`tests/LangCheck` 守护 key 集合一致、无空值）。
- 不新增 `PackageReference`。
- 每完成一个 Task 必须：`dotnet build Kobold.csproj -c Debug` 0 错误 0 警告；与该 Task 相关的 `tests/*Check` 全绿。
- **不碰用户真实文件夹**：所有自检只在 `%TEMP%` 下建临时目录；图标工厂在自检里必须显式传输出目录（不写 `%AppData%`）。
- 手动验证运行部署后的 `%LocalAppData%\Programs\Kobold\Kobold.exe`（由用户执行，见 Task 4 Step 5）。
- 提交信息用 conventional commits（`feat(foldercolor): ...`）。**提交前必须获得用户许可。**

---

## 文件结构

| 文件 | 责任 | 动作 |
|---|---|---|
| `Core/UiTokens.cs` | 新增 `FolderIconPalette`（9 色，含新灰） | 修改 |
| `Core/FolderListing.cs` | 过滤规则：目录只按 Hidden 过滤，文件才按 Hidden\|System | 修改 |
| `Core/FolderColor.cs` | desktop.ini 写/读/恢复、System 位、特殊文件夹保护、shell 刷新 | 新建 |
| `Helpers/FolderIconFactory.cs` | 系统图标提取（16/32/48/256）→ 色相替换 → PNG-in-ICO 缓存 | 新建 |
| `Core/Utils.cs` | `GetFolderIconsPath()` | 修改 |
| `Controls/FolderWidget.MenuActions.cs` | 「文件夹颜色」子菜单（浏览态 + 根态）+ 应用/恢复 | 修改 |
| `Controls/FolderWidget.Dialogs.cs` | 图标缓存单条失效（`ForgetIcon`） | 修改 |
| `Core/Localization.cs` | 4 个新 key × 三语 | 修改 |
| `tests/FolderListingCheck/Program.cs` | System 目录可见 / Hidden 目录仍过滤 用例 | 修改 |
| `tests/FolderIconCheck/` | 图标工厂自检（结构 / 尺寸 / 色相） | 新建 |
| `tests/FolderColorCheck/` | desktop.ini 往返 / 恢复 / 保留用户段 / 拒绝特殊文件夹 | 新建 |
| `README.md` | 功能表补一行（en/zh） | 修改 |

---

### Task 1: 色板 + 目录过滤规则

**Files:**
- Modify: `Core/UiTokens.cs`（`FolderPalette` 之后）
- Modify: `Core/FolderListing.cs:104-105`（`ReadEntries` 内的属性过滤）
- Modify: `tests/FolderListingCheck/Program.cs`（新增用例 + Main 调用）

**Interfaces:**
- Produces: `Kobold.Core.UiTokens.FolderIconPalette : string[]`（9 个 `#RRGGBB`）
- 行为变更：`FolderListing.ListChildren` 现在会返回带 System 属性的**目录**（上色后的文件夹），文件仍按 Hidden\|System 过滤。

- [ ] **Step 1: 写失败的自检用例**

`tests/FolderListingCheck/Program.cs`：在 `Main` 的调用列表里 `SameEntriesComparison();` 之后加一行：

```csharp
                SystemDirectoriesAreListed();
```

在 `SameEntriesComparison()` 之后、`TryDelete` 之前插入：

```csharp
        private static void SystemDirectoriesAreListed()
        {
            string dir = NewDir("case-system-dir");

            // Folder colors are stored in desktop.ini, and Explorer only reads it
            // for folders carrying the System flag - so such folders must stay
            // visible here, or coloring one would hide it from our own browser.
            string colored = Path.Combine(dir, "colored");
            Directory.CreateDirectory(colored);
            File.SetAttributes(colored, FileAttributes.System);

            string plain = Path.Combine(dir, "plain");
            Directory.CreateDirectory(plain);

            var names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();
            Check(names.Contains("colored"), "system directory: a colored folder stays listed");
            Check(names.Contains("plain"), "system directory: a plain folder is unaffected");

            string hiddenDir = Path.Combine(dir, "hidden-dir");
            Directory.CreateDirectory(hiddenDir);
            File.SetAttributes(hiddenDir, FileAttributes.Hidden);

            names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();
            Check(!names.Contains("hidden-dir"), "system directory: hidden directories stay filtered");

            string systemFile = Path.Combine(dir, "system.txt");
            File.WriteAllText(systemFile, "");
            File.SetAttributes(systemFile, FileAttributes.System);

            names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();
            Check(!names.Contains("system.txt"), "system directory: system files stay filtered");
        }
```

- [ ] **Step 2: 运行自检，确认失败**

Run: `dotnet run --project tests/FolderListingCheck`
Expected: 退出码 1，失败项包含 `system directory: a colored folder stays listed`（其余新用例应已通过）。

- [ ] **Step 3: 改过滤规则**

`Core/FolderListing.cs`：把 `ReadEntries` 里的

```csharp
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
```

替换为：

```csharp
                    var attributes = File.GetAttributes(path);

                    // Hidden entries never show. System is different: a folder
                    // carries it once it has a desktop.ini (how Explorer knows to
                    // read a custom icon), so only files are filtered on it -
                    // desktop.ini and thumbs.db still stay out.
                    bool hidden = (attributes & FileAttributes.Hidden) != 0;
                    bool system = (attributes & FileAttributes.System) != 0;
                    if (hidden || (system && !isDirectory)) continue;
```

- [ ] **Step 4: 加色板**

`Core/UiTokens.cs`：在 `FolderPalette` 之后插入：

```csharp
        /// <summary>
        /// Folder icon colors offered by the folder-color menu. Hues reuse the
        /// widget palette so the two pickers read as one family; the grey is the
        /// one extra (a desaturated folder icon has no widget counterpart).
        /// </summary>
        public static readonly string[] FolderIconPalette =
        {
            "#EF4444", "#F97316", "#F59E0B", "#84CC16", "#22C55E",
            "#06B6D4", "#3B82F6", "#8B5CF6", "#94A3B8"
        };
```

- [ ] **Step 5: 构建 + 自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 错误 0 警告
Run: `dotnet run --project tests/FolderListingCheck` → Expected: 全绿
Run: `dotnet run --project tests/UiTokensCheck` → Expected: 全绿（新色板仍在 UiTokens.cs 内）

- [ ] **Step 6: 提交（需用户许可）**

```bash
git add Core/UiTokens.cs Core/FolderListing.cs tests/FolderListingCheck/Program.cs
git commit -m "feat(foldercolor): keep system-flagged folders visible and add the icon palette"
```

---

### Task 2: `FolderIconFactory`（图标提取 + 重着色 + ICO）

**Files:**
- Create: `Helpers/FolderIconFactory.cs`
- Create: `tests/FolderIconCheck/FolderIconCheck.csproj`
- Create: `tests/FolderIconCheck/Program.cs`

**Interfaces:**
- Produces:
  - `Kobold.Helpers.FolderIconFactory.EnsureIcon(string colorHex, string outputDir) -> string`（返回 .ico 路径；失败返回 null）
  - `Kobold.Helpers.FolderIconFactory.BuildIconBytes(string colorHex) -> byte[]`（供自检直接断言）
  - 缓存文件名 `<RRGGBB>-<osBuild>.ico`（系统升级后自动重建）

- [ ] **Step 1: 建自检工程**

`tests/FolderIconCheck/FolderIconCheck.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <UseWPF>true</UseWPF>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>Kobold.FolderIconCheck</AssemblyName>
    <RootNamespace>Kobold.FolderIconCheck</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Kobold.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写失败的自检**

`tests/FolderIconCheck/Program.cs`：

```csharp
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

                var largest = frames.OrderByDescending(f => f.PixelWidth).First();
                double wantHue, wantSat, wantVal;
                var target = (Color)ColorConverter.ConvertFromString(hex);
                RgbToHsv(target.R, target.G, target.B, out wantHue, out wantSat, out wantVal);

                double gotHue;
                Check(DominantHue(largest, out gotHue) &&
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
                "ensure: the cache file is named after the color and OS build");
        }

        private static void BadInputIsRejected()
        {
            Check(FolderIconFactory.EnsureIcon(null, Path.Combine(_root, "icons")) == null,
                "bad input: null color returns null");
            Check(FolderIconFactory.EnsureIcon("#EF4444", null) == null,
                "bad input: null output dir returns null");
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

        /// <summary>Hue of the most saturated pixel, weighted by saturation.</summary>
        private static bool DominantHue(BitmapSource source, out double hue)
        {
            hue = 0;
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            var pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);

            double bestSat = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 200) continue;
                double hh, ss, vv;
                RgbToHsv(pixels[i + 2], pixels[i + 1], pixels[i], out hh, out ss, out vv);
                if (ss > bestSat) { bestSat = ss; hue = hh; }
            }
            return bestSat > 0.2;
        }

        private static double HueDelta(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
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
```

- [ ] **Step 3: 写最小桩实现（能编译、必然失败）**

`Helpers/FolderIconFactory.cs`：

```csharp
using System;

namespace Kobold.Helpers
{
    /// <summary>Stub: the real factory lands in the next step.</summary>
    public static class FolderIconFactory
    {
        public static string EnsureIcon(string colorHex, string outputDir)
        {
            return null;
        }

        public static byte[] BuildIconBytes(string colorHex)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: 运行自检，确认失败**

Run: `dotnet run --project tests/FolderIconCheck`
Expected: 退出码 1；失败项为每个色板的 `BuildIconBytes returned null` / `EnsureIcon` 相关（≥ 11 条），无未捕获异常。

- [ ] **Step 5: 实现真实逻辑**

`Helpers/FolderIconFactory.cs`（整体替换）：

```csharp
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
            // The OS build is part of the name: a Windows update changes the
            // folder art, and the old cache must not survive it.
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

        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiSmallIcon = 0x000000001;
        private const uint ShgfiLargeIcon = 0x000000000;
        private const uint ShgfiUseFileAttributes = 0x000000010;
        private const uint FileAttributeDirectory = 0x00000010;
        private const int ShilExtralarge = 2;
        private const int ShilJumbo = 4;
        private const int IldTransparent = 0x00000001;

        /// <summary>
        /// The folder icon as Windows draws it, at every size we can get: 16/32
        /// from the shell, 48/256 from the system image list. Missing sizes are
        /// scaled up from the largest one we have, so the .ico is never thin.
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

            // Fill gaps by scaling the largest icon we managed to read.
            int largest = 0;
            foreach (var size in found.Keys) if (size > largest) largest = size;
            var result = new List<BitmapSource>();
            foreach (var size in WantedSizes)
            {
                if (found.TryGetValue(size, out var exact)) result.Add(exact);
                else result.Add(Scale(found[largest], size));
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
                // A wrong image list just means this size is scaled instead.
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

        private const uint ShgfiSysIconIndex = 0x000004000;

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
            var scaled = new TransformedBitmap(source,
                new ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
            var result = new WriteableBitmap(new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0));
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
                    writer.Write((ushort)1);          // color planes
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
```

- [ ] **Step 6: 构建 + 运行自检，确认全绿**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 错误 0 警告
Run: `dotnet run --project tests/FolderIconCheck` → Expected: `ALL FOLDER-ICON CHECKS PASSED`，退出码 0
Run: `dotnet run --project tests/UiTokensCheck` → Expected: 全绿

- [ ] **Step 7: 人工看一眼图标（可选但强烈建议）**

Run: `dotnet run --project tests/FolderIconCheck` 后，从 `%TEMP%` 里挑一个 `kobold-foldericon-*` 目录（自检结束会删掉；如需查看，临时把 `finally` 里的删除注释掉再跑），在资源管理器里看 `EF4444-*.ico` 是否为**红色文件夹**、形状与系统文件夹一致。

- [ ] **Step 8: 提交（需用户许可）**

```bash
git add Helpers/FolderIconFactory.cs tests/FolderIconCheck
git commit -m "feat(foldercolor): generate colored folder icons from the system icon"
```

---

### Task 3: `FolderColor`（desktop.ini 写 / 读 / 恢复 / 保护 / 刷新）

**Files:**
- Create: `Core/FolderColor.cs`
- Create: `tests/FolderColorCheck/FolderColorCheck.csproj`
- Create: `tests/FolderColorCheck/Program.cs`
- Modify: `Core/Utils.cs`（`GetStoragePath` 附近加 `GetFolderIconsPath`）

**Interfaces:**
- Consumes: `FolderIconFactory.EnsureIcon`（Task 2）
- Produces:
  - `Kobold.Core.FolderColorResult { Applied, Restored, RefusedSpecialFolder, Failed }`
  - `FolderColor.Apply(string folderPath, string iconPath, int iconIndex) -> FolderColorResult`
  - `FolderColor.Restore(string folderPath) -> FolderColorResult`
  - `FolderColor.GetIconResource(string folderPath) -> string`（形如 `<路径>,<索引>`，无则 null）
  - `FolderColor.IsSpecialFolderIcon(string iconResource) -> bool`
  - `FolderColor.RefreshShell()`
  - `Kobold.Core.Utils.GetFolderIconsPath() -> string`（`%AppData%\Kobold\FolderIcons`）

- [ ] **Step 1: 建自检工程**

`tests/FolderColorCheck/FolderColorCheck.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <UseWPF>true</UseWPF>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>Kobold.FolderColorCheck</AssemblyName>
    <RootNamespace>Kobold.FolderColorCheck</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Kobold.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写失败的自检**

`tests/FolderColorCheck/Program.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kobold.Core;
using Kobold.Helpers;

namespace Kobold.FolderColorCheck
{
    /// <summary>
    /// Checks FolderColor end to end on throwaway temp folders: coloring writes
    /// desktop.ini + the System flag, restoring cleans up without touching a
    /// user's own desktop.ini settings, and system-folder icons are refused.
    /// Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FolderColorCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;
        private static string _iconPath;

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            _root = Path.Combine(Path.GetTempPath(), "kobold-foldercolor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _iconPath = FolderIconFactory.EnsureIcon("#EF4444", Path.Combine(_root, "icons"));
            if (_iconPath == null)
            {
                Console.WriteLine("TOP FAIL: the icon factory could not build a test icon");
                return 1;
            }

            try
            {
                ColoringWritesTheDesktopIni();
                RestoringCleansUp();
                UserSettingsSurviveRestore();
                SpecialFolderIconsAreRefused();
                RestoreIsIdempotent();
                BadInputFailsSafely();
            }
            finally
            {
                TryDelete(_root);
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL FOLDER-COLOR CHECKS PASSED");
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

        private static string NewFolder(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void ColoringWritesTheDesktopIni()
        {
            string folder = NewFolder("case-color");

            var result = FolderColor.Apply(folder, _iconPath, 0);

            Check(result == FolderColorResult.Applied, "apply: reports Applied");
            Check(File.Exists(Path.Combine(folder, "desktop.ini")), "apply: writes desktop.ini");
            Check((File.GetAttributes(folder) & FileAttributes.System) != 0,
                "apply: marks the folder as a system folder (Explorer needs it)");

            string resource = FolderColor.GetIconResource(folder);
            Check(resource != null && resource.StartsWith(_iconPath, StringComparison.OrdinalIgnoreCase),
                "apply: the shell reads back our icon resource (" + resource + ")");
            Check(resource != null && resource.EndsWith(",0"),
                "apply: the icon index travels with the resource");
        }

        private static void RestoringCleansUp()
        {
            string folder = NewFolder("case-restore");
            FolderColor.Apply(folder, _iconPath, 0);

            var result = FolderColor.Restore(folder);

            Check(result == FolderColorResult.Restored, "restore: reports Restored");
            Check(!File.Exists(Path.Combine(folder, "desktop.ini")), "restore: removes the desktop.ini it created");
            Check((File.GetAttributes(folder) & FileAttributes.System) == 0,
                "restore: clears the system flag");
            Check(FolderColor.GetIconResource(folder) == null, "restore: the shell reports the default icon again");
        }

        private static void UserSettingsSurviveRestore()
        {
            string folder = NewFolder("case-keep-user-settings");
            File.WriteAllText(Path.Combine(folder, "desktop.ini"),
                "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\SHELL32.dll,3\r\n" +
                "InfoTip=keep me\r\n[ViewState]\r\nMode=\r\nVid=\r\nFolderType=Generic\r\n");
            File.SetAttributes(Path.Combine(folder, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);

            FolderColor.Restore(folder);

            string text = File.ReadAllText(Path.Combine(folder, "desktop.ini"));
            Check(File.Exists(Path.Combine(folder, "desktop.ini")),
                "user settings: the desktop.ini is kept");
            Check(text.IndexOf("InfoTip=keep me", StringComparison.Ordinal) >= 0 &&
                  text.IndexOf("[ViewState]", StringComparison.Ordinal) >= 0,
                "user settings: unrelated entries and sections survive");
            Check(text.IndexOf("IconResource", StringComparison.OrdinalIgnoreCase) < 0,
                "user settings: the icon entries are gone");
        }

        private static void SpecialFolderIconsAreRefused()
        {
            string folder = NewFolder("case-special");
            File.WriteAllText(Path.Combine(folder, "desktop.ini"),
                "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\SHELL32.dll,3\r\n");
            File.SetAttributes(Path.Combine(folder, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);

            var result = FolderColor.Apply(folder, _iconPath, 0);

            Check(result == FolderColorResult.RefusedSpecialFolder,
                "special folder: applying over a C:\\Windows icon is refused");
            Check(FolderColor.GetIconResource(folder).IndexOf("SHELL32", StringComparison.OrdinalIgnoreCase) >= 0,
                "special folder: the existing icon is untouched");
        }

        private static void RestoreIsIdempotent()
        {
            string folder = NewFolder("case-idempotent");

            Check(FolderColor.Restore(folder) == FolderColorResult.Restored,
                "idempotent: restoring a plain folder is a no-op that succeeds");
        }

        private static void BadInputFailsSafely()
        {
            Check(FolderColor.Apply(Path.Combine(_root, "missing"), _iconPath, 0) == FolderColorResult.Failed,
                "bad input: a missing folder fails");
            Check(FolderColor.Apply(null, _iconPath, 0) == FolderColorResult.Failed,
                "bad input: a null folder fails");
            Check(FolderColor.GetIconResource(Path.Combine(_root, "missing")) == null,
                "bad input: reading a missing folder returns null");
        }

        private static void TryDelete(string path)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
```

- [ ] **Step 3: 写最小桩实现（能编译、必然失败）**

`Core/FolderColor.cs`：

```csharp
namespace Kobold.Core
{
    /// <summary>Stub: the real implementation lands in the next step.</summary>
    public enum FolderColorResult
    {
        Applied,
        Restored,
        RefusedSpecialFolder,
        Failed
    }

    public static class FolderColor
    {
        public static FolderColorResult Apply(string folderPath, string iconPath, int iconIndex)
        {
            return FolderColorResult.Failed;
        }

        public static FolderColorResult Restore(string folderPath)
        {
            return FolderColorResult.Failed;
        }

        public static string GetIconResource(string folderPath)
        {
            return null;
        }

        public static bool IsSpecialFolderIcon(string iconResource)
        {
            return false;
        }

        public static void RefreshShell()
        {
        }
    }
}
```

- [ ] **Step 4: 运行自检，确认失败**

Run: `dotnet run --project tests/FolderColorCheck`
Expected: 退出码 1；失败集中在 `apply:` / `restore:` / `special folder:` 用例（≥ 8 条），`bad input:` 与 `idempotent:` 可能通过；无未捕获异常。

- [ ] **Step 5: 实现真实逻辑**

`Core/FolderColor.cs`（整体替换）：

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Kobold.Core
{
    /// <summary>Outcome of a folder color operation.</summary>
    public enum FolderColorResult
    {
        Applied,
        Restored,
        RefusedSpecialFolder,
        Failed
    }

    /// <summary>
    /// Folder "coloring" is an icon swap: Explorer shows the folder's
    /// [.ShellClassInfo] IconResource from desktop.ini, and it only reads that
    /// file for folders carrying the System flag. Everything here is Win32/IO -
    /// no UI - so tests/FolderColorCheck can drive it on temp folders.
    /// </summary>
    public static class FolderColor
    {
        private const int FCS_READ = 0x1;
        private const int FCS_FORCEWRITE = 0x2;
        private const int FCSM_ICONFILE = 0x10;

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        private static readonly string[] KeepMarkers =
        {
            "[extshellfolderviews]", "[viewstate]", "iconarea_image=",
            "iconarea_text=", "infotip=", "nosharing=", "logo="
        };

        /// <summary>
        /// Points the folder's icon at iconPath (index iconIndex). Existing
        /// desktop.ini settings are preserved; a folder whose icon comes from
        /// C:\Windows is refused because restoring it would be guesswork.
        /// </summary>
        public static FolderColorResult Apply(string folderPath, string iconPath, int iconIndex)
        {
            if (!IsFolder(folderPath) || string.IsNullOrWhiteSpace(iconPath)) return FolderColorResult.Failed;

            try
            {
                if (IsSpecialFolderIcon(GetIconResource(folderPath))) return FolderColorResult.RefusedSpecialFolder;

                string iniPath = Path.Combine(folderPath, "desktop.ini");
                string resource = iconPath + "," + iconIndex;

                if (File.Exists(iniPath))
                {
                    // The shell API rewrites the whole file and drops sections it
                    // does not know; editing the entry ourselves keeps the rest.
                    RemoveIconEntries(iniPath);
                    WritePrivateProfileStringW(".ShellClassInfo", "IconResource", resource, iniPath);
                    PathMakeSystemFolder(folderPath);
                }
                else
                {
                    var settings = new SHFOLDERCUSTOMSETTINGS
                    {
                        dwSize = Marshal.SizeOf(typeof(SHFOLDERCUSTOMSETTINGS)),
                        dwMask = FCSM_ICONFILE,
                        pszIconFile = iconPath,
                        cchIconFile = iconPath.Length,
                        iIconIndex = iconIndex
                    };
                    int hr = SHGetSetFolderCustomSettings(ref settings, folderPath, FCS_FORCEWRITE);
                    if (hr != 0) return FolderColorResult.Failed;
                }

                RefreshShell();
                return FolderColorResult.Applied;
            }
            catch (Exception)
            {
                return FolderColorResult.Failed;
            }
        }

        /// <summary>
        /// Removes our icon entries. A desktop.ini that still holds the user's own
        /// settings (ViewState, an InfoTip, a background image…) is kept - only
        /// the icon lines go; one we created ourselves is deleted outright.
        /// </summary>
        public static FolderColorResult Restore(string folderPath)
        {
            if (!IsFolder(folderPath)) return FolderColorResult.Failed;

            try
            {
                string iniPath = Path.Combine(folderPath, "desktop.ini");
                if (!File.Exists(iniPath))
                {
                    PathUnmakeSystemFolder(folderPath);
                    return FolderColorResult.Restored;
                }

                if (HasUserSettings(File.ReadAllText(iniPath)))
                {
                    RemoveIconEntries(iniPath);
                    if (SectionIsEmpty(iniPath)) WritePrivateProfileStringW(".ShellClassInfo", null, null, iniPath);
                    RefreshShell();
                    return FolderColorResult.Restored;
                }

                File.Delete(iniPath);
                PathUnmakeSystemFolder(folderPath);
                RefreshShell();
                return FolderColorResult.Restored;
            }
            catch (Exception)
            {
                return FolderColorResult.Failed;
            }
        }

        /// <summary>The icon resource the shell currently reads for the folder.</summary>
        public static string GetIconResource(string folderPath)
        {
            if (!IsFolder(folderPath)) return null;

            try
            {
                var settings = new SHFOLDERCUSTOMSETTINGS
                {
                    dwSize = Marshal.SizeOf(typeof(SHFOLDERCUSTOMSETTINGS)),
                    dwMask = FCSM_ICONFILE
                };
                int hr = SHGetSetFolderCustomSettings(ref settings, folderPath, FCS_READ);
                if (hr != 0) return null;

                return settings.pszIconFile;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True for icons that live in C:\Windows - those folders (Downloads,
        /// Documents, Music…) come from the OS and restoring them later would be
        /// guesswork, so they are left alone.
        /// </summary>
        public static bool IsSpecialFolderIcon(string iconResource)
        {
            if (string.IsNullOrWhiteSpace(iconResource)) return false;

            string path = iconResource;
            int comma = path.LastIndexOf(',');
            if (comma > 0) path = path.Substring(0, comma);
            path = path.Trim().Trim('"').ToLowerInvariant();

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                .ToLowerInvariant().TrimEnd('\\') + "\\";
            return path.StartsWith(windows, StringComparison.Ordinal);
        }

        /// <summary>Tells the shell its icon cache is stale.</summary>
        public static void RefreshShell()
        {
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                IntPtr result;
                SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero,
                    0x0002 /* SMTO_ABORTIFHUNG */, 5000, out result);
            }
            catch (Exception)
            {
                // A refresh we cannot deliver is not worth failing the operation.
            }
        }

        private static bool IsFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try { return Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        private static void RemoveIconEntries(string iniPath)
        {
            WritePrivateProfileStringW(".ShellClassInfo", "IconFile", null, iniPath);
            WritePrivateProfileStringW(".ShellClassInfo", "IconIndex", null, iniPath);
            WritePrivateProfileStringW(".ShellClassInfo", "IconResource", null, iniPath);
        }

        private static bool SectionIsEmpty(string iniPath)
        {
            var buffer = new char[2048];
            uint read = GetPrivateProfileSectionW(".ShellClassInfo", buffer, (uint)buffer.Length, iniPath);
            return read == 0;
        }

        private static bool HasUserSettings(string iniText)
        {
            if (string.IsNullOrEmpty(iniText)) return false;
            if (iniText.IndexOf('{') >= 0) return true; // GUID-style extended attributes

            string lower = iniText.ToLowerInvariant();
            foreach (var marker in KeepMarkers)
            {
                if (lower.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFOLDERCUSTOMSETTINGS
        {
            public int dwSize;
            public int dwMask;
            public IntPtr pvid;
            public IntPtr pszWebViewTemplate;
            public int cchWebViewTemplate;
            public IntPtr pszWebViewTemplateVersion;
            public IntPtr pszInfoTip;
            public int cchInfoTip;
            public IntPtr pclsid;
            public int dwFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszIconFile;
            public int cchIconFile;
            public int iIconIndex;
            public IntPtr pszLogo;
            public int cchLogo;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetSetFolderCustomSettings(ref SHFOLDERCUSTOMSETTINGS settings,
            string path, int readWrite);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool PathMakeSystemFolder(string path);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool PathUnmakeSystemFolder(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint WritePrivateProfileStringW(string section, string key, string value, string file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetPrivateProfileSectionW(string section, char[] buffer, uint size, string file);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
```

> 注意 `GetIconResource` 的读回：`SHGetSetFolderCustomSettings(FCS_READ)` 要求调用方预分配 `pszIconFile` 缓冲（MAX_PATH）。若 Step 6 的 `apply: the shell reads back our icon resource` 用例失败（返回 null 或空串），改成：分配 `Marshal.AllocHGlobal(260 * 2)`，把 `pszIconFile` 设为该指针、`cchIconFile = 260`，调用后用 `Marshal.PtrToStringUni` 读回，最后 `FreeHGlobal`。

- [ ] **Step 6: 构建 + 运行自检，确认全绿**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 错误 0 警告
Run: `dotnet run --project tests/FolderColorCheck` → Expected: `ALL FOLDER-COLOR CHECKS PASSED`，退出码 0
Run: `dotnet run --project tests/FolderIconCheck` → Expected: 全绿（回归）

- [ ] **Step 7: 加数据目录 helper**

`Core/Utils.cs`：在 `GetStoragePath()` 之后插入：

```csharp
        /// <summary>Where generated folder icons live (desktop.ini points here).</summary>
        public static string GetFolderIconsPath()
        {
            return System.IO.Path.Combine(GetAppDataPath(), "FolderIcons");
        }
```

- [ ] **Step 8: 提交（需用户许可）**

```bash
git add Core/FolderColor.cs Core/Utils.cs tests/FolderColorCheck
git commit -m "feat(foldercolor): read and write folder icons through desktop.ini"
```

---

### Task 4: 面板接线（菜单 + 文案 + 图标缓存）

**Files:**
- Modify: `Controls/FolderWidget.MenuActions.cs`（`ShowBrowseItemMenu`、`ShowRootItemMenu` 之后新增私有方法）
- Modify: `Controls/FolderWidget.Dialogs.cs`（`_iconCache` 附近加 `ForgetIcon`）
- Modify: `Core/Localization.cs`（4 个 key × 三语）
- Modify: `README.md`（en + zh 功能项）
- Test: `tests/LangCheck`、`tests/XamlLoadCheck`、`tests/UiTokensCheck`、手动

**Interfaces:**
- Consumes: `FolderColor`、`FolderIconFactory`、`Utils.GetFolderIconsPath()`、`UiTokens.FolderIconPalette`
- Produces: `private MenuItem CreateFolderColorMenu(DisplayItem item)`、`private void ApplyFolderColor(DisplayItem item, string colorHex)`、`private void RestoreFolderColor(DisplayItem item)`、`private static void ForgetIcon(string path)`

- [ ] **Step 1: 三语文案**

`Core/Localization.cs`：在**每个**语言块里 `["Menu_CopyPath"]` 之前插入下面四行（每个语言块最后一项不带逗号，新条目都带逗号）：

en：

```csharp
                ["Menu_FolderColor"] = "Folder color",
                ["Menu_FolderColorDefault"] = "Default color",
                ["Dialog_FolderColorRefused"] = "This folder uses a system icon (Downloads, Documents, ...). Kobold leaves it alone because restoring it later would be guesswork.",
                ["Dialog_FolderColorFailed"] = "The folder color could not be changed.",
```

zh：

```csharp
                ["Menu_FolderColor"] = "文件夹颜色",
                ["Menu_FolderColorDefault"] = "恢复默认颜色",
                ["Dialog_FolderColorRefused"] = "这个文件夹用的是系统自带图标（下载、文档等）。还原它们很麻烦，所以 Kobold 不动它。",
                ["Dialog_FolderColorFailed"] = "文件夹颜色修改失败。",
```

ja：

```csharp
                ["Menu_FolderColor"] = "フォルダーの色",
                ["Menu_FolderColorDefault"] = "既定の色に戻す",
                ["Dialog_FolderColorRefused"] = "このフォルダーはシステム標準のアイコン（ダウンロード、ドキュメントなど）を使っています。復元が困難なため、Kobold は変更しません。",
                ["Dialog_FolderColorFailed"] = "フォルダーの色を変更できませんでした。",
```

- [ ] **Step 2: 图标缓存单条失效**

`Controls/FolderWidget.Dialogs.cs`：先看 `GetFileIcon` 里 `cacheKey` 的拼法，在 `_iconCache` 声明之后加：

```csharp
        /// <summary>Drops one path's cached shell icon (a folder color just changed).</summary>
        private static void ForgetIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            lock (_cacheLock)
            {
                foreach (var key in _iconCache.Keys
                    .Where(k => k.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList())
                {
                    _iconCache.Remove(key);
                }
            }
        }
```

> `cacheKey` 的具体形态决定这里的匹配方式；若它正好等于 `path`，直接把上面的过滤换成 `_iconCache.Remove(path)` 即可（更精确）。

- [ ] **Step 3: 菜单项与动作**

`Controls/FolderWidget.MenuActions.cs`：在 `ShowRootItemMenu` 之后插入：

```csharp
        // ---------- folder color ----------

        /// <summary>Swatch submenu: one entry per palette color plus "default".</summary>
        private MenuItem CreateFolderColorMenu(DisplayItem item)
        {
            var menu = new MenuItem { Header = Localization.Get("Menu_FolderColor") };
            string current = FolderColor.GetIconResource(item.Path);

            foreach (var hex in UiTokens.FolderIconPalette)
            {
                string color = hex;
                var swatch = new System.Windows.Shapes.Rectangle
                {
                    Width = 14,
                    Height = 14,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = new SolidColorBrush(Utils.HexToColor(color))
                };

                var entry = new MenuItem
                {
                    Header = null,
                    Icon = swatch,
                    IsChecked = IconBelongsTo(current, color),
                    StaysOpenOnClick = false
                };
                entry.Click += (s, a) => ApplyFolderColor(item, color);
                menu.Items.Add(entry);
            }

            menu.Items.Add(new Separator());
            var reset = new MenuItem
            {
                Header = Localization.Get("Menu_FolderColorDefault"),
                IsEnabled = current != null
            };
            reset.Click += (s, a) => RestoreFolderColor(item);
            menu.Items.Add(reset);

            return menu;
        }

        /// <summary>True when the folder's current icon is the cached icon for this color.</summary>
        private static bool IconBelongsTo(string iconResource, string colorHex)
        {
            if (string.IsNullOrEmpty(iconResource)) return false;

            string expected = colorHex.TrimStart('#').ToUpperInvariant() + "-";
            return Path.GetFileName(iconResource).StartsWith(expected, StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyFolderColor(DisplayItem item, string colorHex)
        {
            string icon = FolderIconFactory.EnsureIcon(colorHex, Utils.GetFolderIconsPath());
            if (icon == null)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorFailed"));
                return;
            }

            var result = FolderColor.Apply(item.Path, icon, 0);
            if (result == FolderColorResult.RefusedSpecialFolder)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorRefused"));
                return;
            }
            if (result != FolderColorResult.Applied)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorFailed"));
                return;
            }

            RefreshAfterFolderColor(item);
        }

        private void RestoreFolderColor(DisplayItem item)
        {
            if (FolderColor.Restore(item.Path) != FolderColorResult.Restored)
            {
                ShowMessage(Localization.Get("Dialog_FolderColorFailed"));
                return;
            }

            RefreshAfterFolderColor(item);
        }

        /// <summary>The shell icon changed: drop the cached bitmap and redraw.</summary>
        private void RefreshAfterFolderColor(DisplayItem item)
        {
            ForgetIcon(item.Path);
            UpdateUI();
        }
```

同时在 `ShowBrowseItemMenu` 与 `ShowRootItemMenu` 里各加一行（放在 `Menu_CopyPath` 之后、分隔符之前，仅对真实文件夹显示）：

```csharp
            if (item.IsDirectory && !item.IsMissing)
            {
                menu.Items.Add(CreateFolderColorMenu(item));
            }
```

> `ShowBrowseItemMenu` 用的是 `MenuBuilder` 链式 API，请在 `.AddItem("Menu_CopyPath", ...)` 之后改成先 `.Build()` 拿不到菜单——**正确做法**：把 `ShowBrowseItemMenu` 里的链式调用结果存进变量再 `menu.Items.Insert(...)`，或改用与 `ShowRootItemMenu` 一致的 `new ContextMenu()` 写法。执行时按现有代码结构二选一，保证子菜单出现在「复制路径」之后。

- [ ] **Step 4: 构建 + 全部相关自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 错误 0 警告
Run（逐个，全部应为退出码 0）:
`dotnet run --project tests/FolderListingCheck`、`dotnet run --project tests/FolderIconCheck`、
`dotnet run --project tests/FolderColorCheck`、`dotnet run --project tests/LangCheck`、
`dotnet run --project tests/XamlLoadCheck`、`dotnet run --project tests/UiTokensCheck`、
`dotnet run --project tests/WidgetItemsCheck`、`dotnet run --project tests/WindowSwitcherCheck`

- [ ] **Step 5: 手动验证（需要用户执行；先部署）**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy.ps1`（会重启安装版）

1. 打开一个组件面板 → 右键一个**文件夹**条目 → 菜单里有「文件夹颜色」，展开是 9 个色块 + 「恢复默认颜色」。
2. 选红色 → 面板里该文件夹图标立刻变红；打开资源管理器看同一文件夹 → 也是红色。
3. 再换绿色 → 立刻变色；重新打开菜单 → 绿色那一项打勾。
4. 「恢复默认颜色」→ 变回系统黄色；资源管理器里也恢复。
5. 上色后的文件夹**仍然出现在面板里**（Task 1 的过滤修正）。
6. 右键「下载」这类系统文件夹 → 选色 → 弹出拒绝提示，文件夹外观不变。
7. 右键一个**文件** → 菜单里没有「文件夹颜色」。
8. 重启应用 → 颜色仍在（desktop.ini 持久）。
9. 回归：F5、浏览刷新（在资源管理器里给某文件夹上色 → 面板自动更新）、拖拽、右键菜单其他项。

- [ ] **Step 6: README**

`README.md` 英文功能项（`File actions in panels` 之后）加：

```markdown
- **Folder colors** — right-click a folder in a panel and give it one of nine
  colors: the icon changes everywhere (Explorer included), and "Default color"
  puts the system icon back.
```

中文功能项（`面板内文件操作` 之后）加：

```markdown
- **文件夹颜色** — 右键面板里的文件夹，可给它换 9 种颜色中的一种：图标会在所有地方
  （包括资源管理器）生效；「恢复默认颜色」可还原系统图标。
```

- [ ] **Step 7: 提交（需用户许可）**

```bash
git add Controls/FolderWidget.MenuActions.cs Controls/FolderWidget.Dialogs.cs Core/Localization.cs README.md
git commit -m "feat(foldercolor): pick a folder color from the item menu"
```

---

## 执行记录（2026-09-23）

四个 Task 全部完成，构建 0 警告 0 错误，11 项自检全绿（新增 `FolderIconCheck` 29 条、`FolderColorCheck` 19 条）。
执行中与计划的偏差（均为实现细节，行为不变）：

1. **`FolderColor` 不调用 `SHGetSetFolderCustomSettings`**：改为自己解析 desktop.ini（读）+ `WritePrivateProfileStringW`（写，保留用户其他段）。原因：读回需要手动管理 `pszIconFile` 缓冲、且 shell 侧有缓存与"重写整个文件"的已知缺陷；自己解析/写入完全可控且可断言。
2. **目录的 System 位显式补写**：本机（Win11 26200）`PathMakeSystemFolder` 只给目录加了 `ReadOnly`，没有 System；Explorer 判断"自定义文件夹"看的是这两个位，因此在调用后显式 `| FileAttributes.System`。红灯用例 `apply: marks the folder as a system folder` 捕获了这一点。
3. **`FolderWidget.Dialogs.cs` 的图标查询**：面板原本对所有目录统一用 `"::folder::"` 通用图标（`SHGFI_USEFILEATTRIBUTES`，绕过 desktop.ini）——上色后面板里不会变色。改为：带 System 位的目录按**真实路径**查询（shell 会读 desktop.ini），其余目录仍共用通用图标（长列表内存护栏）；同时修掉 `!byAttributes && !File.Exists(path)` 对目录的误判。`ForgetIcon(path)` 按路径失效缓存。
4. **`FolderIconCheck` 的色相门槛随目标饱和度缩放**：灰目标 `#94A3B8` 饱和度只有 0.19，固定 0.2 的门槛会把正确结果判失败（红灯暴露了这个过严的测试假设）。

5. **色块从 `MenuItem.Icon` 改为放进 `Header`（用户实测"菜单全是空白"后的修复）**：`App.xaml:44-87` 的全局 MenuItem 模板是完全覆盖的，只渲染 `Header` 与子菜单箭头，**没有 `Icon` 呈现器**（全仓库此前无人用过 `Icon`）。修复后新增 `tests/MenuRenderCheck`：把菜单项**离屏渲染成位图**，断言 9 个色块画出的像素颜色与色板一致、恢复项有文本且默认禁用；把产品代码改回 `Icon` 时该自检报 18 条失败（"renders nothing"），证明它真能抓住这类回归。

另注：图标帧尺寸随系统 DPI 缩放（本机 200% → `32,64,96,256`），正好覆盖 Explorer 各图标档位。

---

## 自审记录（写计划时已核对）

- **设计覆盖**：运行时图标生成（Task 2）、desktop.ini 写/读/恢复 + 特殊文件夹保护（Task 3）、9 色色板（Task 1）、浏览/根态菜单入口（Task 4）、过滤规则修正（Task 1）、图标缓存失效（Task 4）。
- **类型一致性**：`FolderIconFactory.EnsureIcon/BuildIconBytes`、`FolderColor.Apply/Restore/GetIconResource/IsSpecialFolderIcon/RefreshShell`、`FolderColorResult`、`UiTokens.FolderIconPalette`、`Utils.GetFolderIconsPath()` 在各 Task 之间名字一致。
- **无占位符**：每个代码步骤都给了可直接粘贴的代码；两处标注了「按现有代码结构二选一」的接线细节（`GetIconResource` 的缓冲分配、`ShowBrowseItemMenu` 的链式结构），执行时以自检红灯为准。
- **明确不做**：不改文件真实颜色、不支持特殊文件夹、不做 shell 右键菜单注册（只用面板自己的菜单）、不递归给子文件夹上色。
