# Kobold 面板内文件夹浏览（Folder Browse）实现计划

> **状态（2026-09-21）**：待执行。设计见 `docs/superpowers/specs/2026-09-21-folder-browse-design.md`。
> **提交约定**：本计划按仓库习惯在每步末尾给出 commit 命令，但**执行时必须先获得用户明确许可**才可提交。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 双击组件面板里的文件夹条目后，面板内就地显示该文件夹的子文件夹与文件，可逐级下钻与返回；浏览是只读视图，不改动 `_data.Items`、不提供任何文件写操作。

**Architecture:** 新增纯逻辑 `Core/FolderListing.cs`（枚举 + 排序 + 过滤 + 截断，可被控制台自检覆盖）；`FolderWidget` 增加一条浏览路径栈（`_browseStack`，空 = 根态）与新的 partial 文件 `Controls/FolderWidget.Browse.cs`，`UpdateUI()` 按当前模式在 `_data.Items` 与目录列表之间切换数据源；面板标题栏加返回按钮，内容区加 `ScrollViewer` 与高度上限，只读的条目右键菜单与拖拽闸门保证浏览态无写操作。

**Tech Stack:** WPF (net48)、C# 7.3、`shlwapi!StrCmpLogicalW`（自然排序）、现有 `Localization` / `ThemeManager` / `MenuBuilder` / 控制台自检（`tests/*Check`）。

**Spec:** `docs/superpowers/specs/2026-09-21-folder-browse-design.md`

## Global Constraints

- 目标框架 net48，`LangVersion 7.3`（不要使用 C# 8+ 语法）。
- 所有 UI 文案走 `Localization`；新增 key 必须 en/zh/ja 三语齐全（`tests/LangCheck` 守护 key 集合一致、无空值、`{0}` 占位符在每种语言都能 `Format`）。
- 新增 XAML 不得出现十六进制颜色字面量（`tests/UiTokensCheck` 会扫描 `.xaml`/`.cs`，只允许 `Core/UiTokens.cs` 里有）；一律用 `{DynamicResource Kobold.Brush.*}` 或 `ThemeManager`。
- 浏览态**只读**：不写 `_data.Items`、不改磁盘文件、不起拖拽、不接受拖入；浏览位置不持久化到 `config.json`。
- 面板高度上限 = `SystemParameters.WorkArea.Height × 0.6`；目录条目上限 200；浏览态图标加载上限 60。
- 每完成一个 Task 必须：`dotnet build Kobold.csproj -c Debug` 0 错误；与该 Task 相关的 `tests/*Check` 全绿。
- 手动验证运行 `bin\Debug\net48\Kobold.exe`；调试期可用 `%TEMP%\opencode\kobold-windows.ps1` 观察窗口/内存。
- 提交信息用 conventional commits（`feat(browse): ...`）。**提交前必须获得用户许可。**

---

## 文件结构

| 文件 | 责任 | 动作 |
|---|---|---|
| `Core/FolderListing.cs` | 目录枚举 + 过滤 + 自然排序 + 截断（纯逻辑，无 UI） | 新建 |
| `Core/WidgetConstants.cs` | 新增浏览相关常量 | 修改 |
| `Controls/DisplayItem.cs` | 增加 `IsDirectory` | 修改 |
| `Controls/FolderWidget.Browse.cs` | 浏览路径栈、导航、只读菜单、shell 打开辅助 | 新建 |
| `Controls/FolderWidget.xaml.cs` | `UpdateUI()` 数据源切换 / 空态 / 页脚 / 图标加载 | 修改 |
| `Controls/FolderWidget.xaml` | 返回按钮、`ScrollViewer`、覆盖层、页脚 | 修改 |
| `Controls/FolderWidget.Interactions.cs` | 标题栏返回按钮、`Backspace`、`HidePanel` 重置、删除死代码 | 修改 |
| `Controls/FolderWidget.DragDrop.cs` | 双击分流、浏览态拖拽闸门 | 修改 |
| `Controls/FolderWidget.Dialogs.cs` | 通用文件图标（内存护栏的回退） | 修改 |
| `Controls/FolderWidget.Theme.cs` | 高度上限 + 滚动时宽度补偿 | 修改 |
| `tests/FolderListingCheck/` | `FolderListing` 的控制台自检 | 新建 |
| `tests/XamlLoadCheck/Program.cs` | 把 `FolderWidget` 纳入 XAML 加载自检 | 修改 |
| `Core/Localization.cs` | 4 个新 key × 三语 | 修改 |
| `README.md` | 功能表补充"面板内浏览" | 修改 |

---

### Task 1: `FolderListing` 纯逻辑 + 自检

**Files:**
- Create: `Core/FolderListing.cs`
- Create: `tests/FolderListingCheck/FolderListingCheck.csproj`
- Create: `tests/FolderListingCheck/Program.cs`

**Interfaces:**
- Produces:
  - `Kobold.Core.BrowseEntry { string Name; string Path; bool IsDirectory; }`
  - `Kobold.Core.ListingResult { List<BrowseEntry> Entries; int TotalCount; bool Truncated; bool Failed; }`
  - `Kobold.Core.FolderListing.ListChildren(string directory, int maxEntries) -> ListingResult`

- [ ] **Step 1: 建测试工程文件**

`tests/FolderListingCheck/FolderListingCheck.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>Kobold.FolderListingCheck</AssemblyName>
    <RootNamespace>Kobold.FolderListingCheck</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Kobold.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写失败的自检**

`tests/FolderListingCheck/Program.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kobold.Core;

namespace Kobold.FolderListingCheck
{
    /// <summary>
    /// Checks for FolderListing (ordering, filtering, truncation, error tolerance).
    /// Pure logic, no UI. Exit code 0 = all green; 1 = failures.
    /// Run: dotnet run --project tests/FolderListingCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            _root = Path.Combine(Path.GetTempPath(), "kobold-folderlisting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            try
            {
                DirectoriesComeFirstRegardlessOfName();
                NaturalSortOrdersNumbers();
                HiddenEntriesAreFiltered();
                SystemEntriesAreFiltered();
                TruncationKeepsDirectoriesAndCountsAll();
                EmptyDirectoryIsNotAFailure();
                MissingDirectoryFails();
                NullOrEmptyInputFails();
                NameIsTheRawFileName();
            }
            finally
            {
                TryDelete(_root);
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL FOLDER-LISTING CHECKS PASSED");
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

        private static string NewDir(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string NewFile(string name)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, "");
            return path;
        }

        private static void DirectoriesComeFirstRegardlessOfName()
        {
            string dir = NewDir("case-dirs-first");
            Directory.CreateDirectory(Path.Combine(dir, "zfolder"));
            File.WriteAllText(Path.Combine(dir, "afile.txt"), "");

            var result = FolderListing.ListChildren(dir, 100);

            Check(!result.Failed && result.Entries.Count == 2, "dirs-first: two entries returned");
            if (result.Entries.Count < 2)
            {
                Errors.Add("dirs-first: not enough entries to check the ordering");
                return;
            }

            Check(result.Entries[0].Name == "zfolder" && result.Entries[0].IsDirectory,
                "dirs-first: directory sorts before a file that precedes it alphabetically");
            Check(result.Entries[1].Name == "afile.txt" && !result.Entries[1].IsDirectory,
                "dirs-first: file follows");
        }

        private static void NaturalSortOrdersNumbers()
        {
            string dir = NewDir("case-natural-sort");
            File.WriteAllText(Path.Combine(dir, "file10.txt"), "");
            File.WriteAllText(Path.Combine(dir, "file2.txt"), "");
            File.WriteAllText(Path.Combine(dir, "file1.txt"), "");

            var names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();

            Check(names.SequenceEqual(new[] { "file1.txt", "file2.txt", "file10.txt" }),
                "natural sort: file1 < file2 < file10 (got " + string.Join(",", names) + ")");
        }

        private static void HiddenEntriesAreFiltered()
        {
            string dir = NewDir("case-hidden");
            string visible = Path.Combine(dir, "visible.txt");
            string hidden = Path.Combine(dir, "hidden.txt");
            File.WriteAllText(visible, "");
            File.WriteAllText(hidden, "");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();

            Check(names.Count == 1 && names[0] == "visible.txt", "hidden entries are filtered out");
        }

        private static void SystemEntriesAreFiltered()
        {
            string dir = NewDir("case-system");
            string system = Path.Combine(dir, "system.txt");
            File.WriteAllText(Path.Combine(dir, "normal.txt"), "");
            File.WriteAllText(system, "");
            File.SetAttributes(system, FileAttributes.System);

            var names = FolderListing.ListChildren(dir, 100).Entries.Select(e => e.Name).ToList();

            Check(names.Count == 1 && names[0] == "normal.txt", "system entries are filtered out");
        }

        private static void TruncationKeepsDirectoriesAndCountsAll()
        {
            string dir = NewDir("case-truncation");
            for (int i = 0; i < 4; i++) Directory.CreateDirectory(Path.Combine(dir, "dir" + i));
            for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(dir, "file" + i + ".txt"), "");

            var result = FolderListing.ListChildren(dir, 5);

            Check(result.Entries.Count == 5, "truncation: returns exactly maxEntries entries");
            Check(result.TotalCount == 8, "truncation: TotalCount is the real total (got " + result.TotalCount + ")");
            Check(result.Truncated, "truncation: Truncated is true");
            if (result.Entries.Count < 5)
            {
                Errors.Add("truncation: not enough entries to check which ones survived the cut");
                return;
            }

            Check(result.Entries.Take(4).All(e => e.IsDirectory),
                "truncation: all four directories survive the cut");
            Check(!result.Entries[4].IsDirectory, "truncation: the fifth slot is a file");
        }

        private static void EmptyDirectoryIsNotAFailure()
        {
            string dir = NewDir("case-empty");

            var result = FolderListing.ListChildren(dir, 100);

            Check(!result.Failed && result.Entries.Count == 0 && result.TotalCount == 0 && !result.Truncated,
                "empty directory: no entries, no failure, no truncation");
        }

        private static void MissingDirectoryFails()
        {
            var result = FolderListing.ListChildren(Path.Combine(_root, "does-not-exist"), 100);

            Check(result.Failed && result.Entries.Count == 0, "missing directory: Failed = true");
        }

        private static void NullOrEmptyInputFails()
        {
            Check(FolderListing.ListChildren(null, 100).Failed, "null directory: Failed = true");
            Check(FolderListing.ListChildren("   ", 100).Failed, "blank directory: Failed = true");
        }

        private static void NameIsTheRawFileName()
        {
            string dir = NewDir("case-raw-name");
            File.WriteAllText(Path.Combine(dir, "shortcut.lnk"), "");

            var result = FolderListing.ListChildren(dir, 100);
            if (result.Entries.Count != 1)
            {
                Errors.Add("raw name: expected exactly one entry, got " + result.Entries.Count);
                return;
            }

            Check(result.Entries[0].Name == "shortcut.lnk", "Name keeps the raw file name (.lnk not stripped here)");
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

- [ ] **Step 3: 写最小桩实现，让自检能编译并失败**

`Core/FolderListing.cs`（桩：只满足接口，全部返回空结果）：

```csharp
using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// One child of a browsed directory.
    /// </summary>
    public sealed class BrowseEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDirectory { get; set; }
    }

    /// <summary>
    /// Result of reading a directory for the in-panel browser.
    /// </summary>
    public sealed class ListingResult
    {
        public List<BrowseEntry> Entries { get; set; }
        public int TotalCount { get; set; }
        public bool Truncated { get; set; }
        public bool Failed { get; set; }

        public ListingResult()
        {
            Entries = new List<BrowseEntry>();
        }
    }

    /// <summary>
    /// Pure helpers for the in-panel folder browser. Kept free of UI and storage
    /// dependencies so tests/FolderListingCheck can cover the rules.
    /// </summary>
    public static class FolderListing
    {
        public static ListingResult ListChildren(string directory, int maxEntries)
        {
            return new ListingResult();
        }
    }
}
```

- [ ] **Step 4: 运行自检，确认失败**

Run: `dotnet run --project tests/FolderListingCheck`
Expected: 退出码 1，输出末尾是干净的 `FAILURES (n):` 列表（n ≥ 8），**没有未捕获异常、没有崩溃**——四处索引访问都已加护栏（`dirs-first`、`truncation`、`raw name`），所以桩实现的空结果会被报告成失败而不是让进程死掉。

- [ ] **Step 5: 实现真实逻辑**

`Core/FolderListing.cs`（替换 `ListChildren`，并补上排序辅助；模型类保持不变）：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Kobold.Core
{
    public static class FolderListing
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string a, string b);

        private static bool? _naturalSortAvailable;

        /// <summary>
        /// Reads one directory for the browser. Directories come first, each
        /// group sorted with the same natural order Explorer uses, hidden and
        /// system entries are skipped, and the result is cut at maxEntries
        /// (directories survive the cut because they sort first).
        /// </summary>
        public static ListingResult ListChildren(string directory, int maxEntries)
        {
            var result = new ListingResult();

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                result.Failed = true;
                return result;
            }

            List<BrowseEntry> dirs;
            List<BrowseEntry> files;
            try
            {
                dirs = ReadEntries(Directory.EnumerateDirectories(directory), true);
                files = ReadEntries(Directory.EnumerateFiles(directory), false);
            }
            catch (Exception)
            {
                // UnauthorizedAccessException / IOException / DirectoryNotFoundException:
                // the caller shows the "cannot open" empty state.
                result.Failed = true;
                return result;
            }

            dirs.Sort(CompareEntries);
            files.Sort(CompareEntries);

            result.TotalCount = dirs.Count + files.Count;

            int limit = Math.Max(0, maxEntries);
            foreach (var entry in dirs)
            {
                if (result.Entries.Count >= limit) break;
                result.Entries.Add(entry);
            }
            foreach (var entry in files)
            {
                if (result.Entries.Count >= limit) break;
                result.Entries.Add(entry);
            }

            result.Truncated = result.TotalCount > result.Entries.Count;
            return result;
        }

        private static List<BrowseEntry> ReadEntries(IEnumerable<string> paths, bool isDirectory)
        {
            var entries = new List<BrowseEntry>();
            foreach (var path in paths)
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                    string name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name)) continue;

                    entries.Add(new BrowseEntry
                    {
                        Name = name,
                        Path = path,
                        IsDirectory = isDirectory
                    });
                }
                catch (Exception)
                {
                    // Unreadable entry: skip it, keep the rest of the listing.
                }
            }
            return entries;
        }

        private static int CompareEntries(BrowseEntry a, BrowseEntry b)
        {
            int cmp = CompareNames(a.Name, b.Name);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        }

        private static int CompareNames(string a, string b)
        {
            if (_naturalSortAvailable == null)
            {
                try
                {
                    StrCmpLogicalW("a", "b");
                    _naturalSortAvailable = true;
                }
                catch (Exception)
                {
                    _naturalSortAvailable = false;
                }
            }

            return _naturalSortAvailable.Value
                ? StrCmpLogicalW(a, b)
                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

- [ ] **Step 6: 构建 + 运行自检，确认全绿**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: `dotnet run --project tests/FolderListingCheck` → Expected: `ALL FOLDER-LISTING CHECKS PASSED`，退出码 0

- [ ] **Step 7: 提交（需用户许可）**

```bash
git add Core/FolderListing.cs tests/FolderListingCheck
git commit -m "feat(browse): add FolderListing with natural-sorted, filtered directory reads"
```

---

### Task 2: 浏览模式核心（导航 + 返回按钮 + 只读菜单 + 拖拽闸门）

**Files:**
- Create: `Controls/FolderWidget.Browse.cs`
- Modify: `Core/WidgetConstants.cs`
- Modify: `Controls/DisplayItem.cs`
- Modify: `Controls/FolderWidget.xaml.cs:128-207`（`UpdateUI`）
- Modify: `Controls/FolderWidget.xaml:55-145`（标题栏）
- Modify: `Controls/FolderWidget.Interactions.cs:62-77`（`HidePanel`）、`:177-193`（`Window_KeyDown`）、`:289-303`（删死代码）、`:305-375`（条目右键菜单）
- Modify: `Controls/FolderWidget.DragDrop.cs:83-131`（双击分流）、`:133-136`（拖拽闸门）、`:344-347`、`:409-413`（拒绝拖入）
- Modify: `Core/Localization.cs`（三语 +4 key）
- Test: `tests/LangCheck`、`tests/XamlLoadCheck`、`tests/FolderListingCheck`、`tests/UiTokensCheck`

**Interfaces:**
- Consumes: `FolderListing.ListChildren`、`ListingResult`、`BrowseEntry`（Task 1）
- Produces:
  - `FolderWidget.IsBrowsing : bool`
  - `FolderWidget.EnterFolder(string path)` / `FolderWidget.GoBack()`
  - `private List<DisplayItem> BuildRootItems()` / `private List<DisplayItem> BuildBrowseItems()`
  - `private static void OpenWithShell(string path)` / `private static void OpenContainingFolder(string path)` / `private static void CopyPathToClipboard(string path)`
  - `DisplayItem.IsDirectory : bool`
  - 常量 `WidgetConstants.MAX_BROWSE_ENTRIES = 200`

- [ ] **Step 1: 加常量**

`Core/WidgetConstants.cs`，在 `#region Drag-Drop` 之前插入：

```csharp
        #region Folder Browse

        /// <summary>Max entries a browse listing returns (the rest is reported as a footer hint)</summary>
        public const int MAX_BROWSE_ENTRIES = 200;

        #endregion
```

- [ ] **Step 2: `DisplayItem.IsDirectory`**

`Controls/DisplayItem.cs`，在 `Index` 之后插入：

```csharp
        /// <summary>True when the entry points at a directory (double-click drills in instead of shell-open)</summary>
        public bool IsDirectory { get; set; }
```

- [ ] **Step 3: 新建浏览 partial**

`Controls/FolderWidget.Browse.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Kobold.Core;
using Kobold.Helpers;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - In-panel folder browsing. The browser is a read-only view
    /// over the file system: it never touches _data.Items and never writes files.
    /// </summary>
    public partial class FolderWidget
    {
        #region Browse State

        // Empty = the widget's own items (root). Last element = the folder on screen.
        private readonly List<string> _browseStack = new List<string>();
        private ListingResult _browseListing;

        private string CurrentBrowsePath =>
            _browseStack.Count == 0 ? null : _browseStack[_browseStack.Count - 1];

        /// <summary>True while the panel shows a folder instead of the widget's items.</summary>
        public bool IsBrowsing => _browseStack.Count > 0;

        public void EnterFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            _browseStack.Add(path);
            ClearAllSelections();
            UpdateUI();
        }

        public void GoBack()
        {
            if (_browseStack.Count == 0) return;

            _browseStack.RemoveAt(_browseStack.Count - 1);
            ClearAllSelections();
            UpdateUI();
        }

        /// <summary>Browsing is transient - the panel always reopens at the root.</summary>
        private void ResetBrowse()
        {
            _browseStack.Clear();
            _browseListing = null;
        }

        private void BackButton_Click(object sender, MouseButtonEventArgs e)
        {
            GoBack();
            e.Handled = true; // must not start a panel drag through the header
        }

        #endregion

        #region Item Building

        private List<DisplayItem> BuildRootItems()
        {
            var textBrush = ThemeManager.TextBrush;

            return _data.Items.Select((item, index) =>
            {
                bool isDirectory = System.IO.Directory.Exists(item.Path);
                bool isFile = !isDirectory && System.IO.File.Exists(item.Path);

                return new DisplayItem
                {
                    Name = GetDisplayName(string.IsNullOrEmpty(item.Name) ? item.Path : item.Name),
                    Path = item.Path,
                    Icon = null,
                    Index = index,
                    TextColor = textBrush,
                    IsStored = !item.IsReference,
                    IsDirectory = isDirectory,
                    IsMissing = !isDirectory && !isFile
                };
            }).ToList();
        }

        private List<DisplayItem> BuildBrowseItems()
        {
            var textBrush = ThemeManager.TextBrush;
            _browseListing = FolderListing.ListChildren(CurrentBrowsePath, WidgetConstants.MAX_BROWSE_ENTRIES);

            var items = new List<DisplayItem>();
            foreach (var entry in _browseListing.Entries)
            {
                items.Add(new DisplayItem
                {
                    Name = GetDisplayName(entry.Path),
                    Path = entry.Path,
                    Icon = null,
                    Index = items.Count,
                    TextColor = textBrush,
                    IsStored = false,
                    IsMissing = false,
                    IsDirectory = entry.IsDirectory
                });
            }
            return items;
        }

        #endregion

        #region Read-Only Item Menu

        private void ShowBrowseItemMenu(DisplayItem item)
        {
            new MenuBuilder(_data.Color)
                .AddItem("Menu_Open", () => OpenWithShell(item.Path))
                .AddItem("Menu_OpenLocation", () => OpenContainingFolder(item.Path))
                .AddItem("Menu_CopyPath", () => CopyPathToClipboard(item.Path))
                .Show();
        }

        #endregion

        #region Shell Helpers

        private static void OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Open failed: {ex.Message}");
            }
        }

        private static void OpenContainingFolder(string path)
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Open location failed: {ex.Message}");
            }
        }

        private static void CopyPathToClipboard(string path)
        {
            try
            {
                Clipboard.SetText(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Clipboard failed: {ex.Message}");
            }
        }

        #endregion
    }
}
```

注意：文件顶部需要 `using System.Linq;`（`BuildRootItems` 用到 `Select`/`ToList`），请把 `using System.Linq;` 一并加上。

- [ ] **Step 4: `UpdateUI()` 切换数据源**

`Controls/FolderWidget.xaml.cs`：把 `UpdateUI()` 里"标题 + 构建 items + 设置 ItemsSource + 异步图标 + 空态"这一段替换为下面的实现，并在字段区新增 `private int _lastItemCount;`：

```csharp
        public void UpdateUI()
        {
            PanelHeaderText.Text = IsBrowsing ? GetDisplayName(CurrentBrowsePath) : _data.Name;
            PanelHeaderText.ToolTip = IsBrowsing ? CurrentBrowsePath : null;
            BackButton.Visibility = IsBrowsing ? Visibility.Visible : Visibility.Collapsed;
            UpdatePinButtonVisual();
            BadgeHelpTitle.Text = Localization.Get("UI_BadgeHelpTitle");
            BadgeHelpStored.Text = Localization.Get("UI_BadgeHelpStored");
            BadgeHelpMissing.Text = Localization.Get("UI_BadgeHelpMissing");

            // Lock indicator (header button)
            UpdateLockButtonVisual();

            // Apply item scale transform BEFORE setting items
            double scale = GetItemScale();
            ItemsContainer.LayoutTransform = new ScaleTransform(scale, scale);

            var items = IsBrowsing ? BuildBrowseItems() : BuildRootItems();
            _lastItemCount = items.Count;

            // Set ItemsSource - BindableUniformGrid.BindableColumns is bound to GridColumns property
            ItemsContainer.ItemsSource = items;
            LoadItemIcons(items);

            // Panel-only: keep the window sized to the panel while open
            if (_isExpanded)
            {
                UpdateLayout();

                var (panelWidth, panelHeight) = CalculatePanelSize();

                ExpandedPanel.Width = panelWidth;
                ExpandedPanel.Height = panelHeight;

                Width = panelWidth;
                Height = panelHeight;

                System.Windows.Controls.Canvas.SetLeft(ExpandedPanel, 0);
                System.Windows.Controls.Canvas.SetTop(ExpandedPanel, 0);
            }

            UpdateEmptyState(items.Count);

            // Update panel colors
            UpdatePanelColor();

            // Apply item text colors after items are rendered
            Dispatcher.BeginInvoke(new Action(() => ApplyItemTextColors()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Loads shell icons off the UI thread. Task 4 narrows this for browse
        /// listings; at this point it mirrors the original behaviour.
        /// </summary>
        private void LoadItemIcons(List<DisplayItem> items)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in items)
                {
                    ApplyIcon(item, GetFileIcon(item.Path));
                }
            });
        }

        private void ApplyIcon(DisplayItem item, ImageSource icon)
        {
            if (icon == null) return;

            try
            {
                if (!icon.IsFrozen) icon.Freeze();
                Dispatcher.BeginInvoke(new Action(() => item.Icon = icon),
                    System.Windows.Threading.DispatcherPriority.Normal);
            }
            catch { /* Ignore icon load errors */ }
        }

        private void UpdateEmptyState(int itemCount)
        {
            if (itemCount > 0)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyState.Visibility = Visibility.Visible;
            if (!IsBrowsing)
            {
                EmptyText.Text = Localization.Get("UI_DropHere");
                return;
            }

            EmptyText.Text = Localization.Get(_browseListing != null && _browseListing.Failed
                ? "UI_FolderAccessDenied"
                : "UI_FolderEmpty");
        }
```

同时把原来的 `GetDisplayName` 方法保留不动；`_browseListing` 由 `BuildBrowseItems()` 赋值，供 `UpdateEmptyState` 使用。

- [ ] **Step 5: 双击分流 + 拖拽闸门**

`Controls/FolderWidget.DragDrop.cs`：

1. `Item_MouseDown` 的 `ClickCount == 2` 分支改为（替换原来内联的 `Process.Start`）：

```csharp
                // Double-click: folders are browsed in place, files keep opening in the shell
                if (e.ClickCount == 2)
                {
                    if (item.IsDirectory) EnterFolder(item.Path);
                    else OpenWithShell(item.Path);

                    e.Handled = true;
                    return;
                }
```

2. `Item_MouseMove` 开头（`if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null) return;` 之后）插入：

```csharp
            // Browsing is a read-only view: dragging entries out could move real files.
            if (IsBrowsing) return;
```

3. `ItemsContainer_DragOver` 开头插入（**必须同时标记"目标拒绝"**：只设 `e.Effects = None` 是不够的，
   拖拽源在 `Panel_QueryContinueDrag` 里看到 `PendingMove`/`TargetRefused` 双 false 时，会在松手时把
   被拖条目当作"拖出面板"执行 `EjectDraggedItemsOnRelease`——已收纳文件会被真移回原位置并从源组件移除。
   锁定组件的分支就是这么处理拒绝的，浏览态必须一致）：

```csharp
            if (IsBrowsing)
            {
                e.Effects = DragDropEffects.None;
                DropIndicator.Visibility = Visibility.Collapsed;
                DragDropSession.Current?.MarkTargetRefused();
                e.Handled = true;
                return;
            }
```

4. `ItemsContainer_Drop` 开头（`DropIndicator.Visibility = Visibility.Collapsed;` 之后）插入（同样镜像"拒绝"标记）：

```csharp
            if (IsBrowsing)
            {
                e.Effects = DragDropEffects.None;
                DragDropSession.Current?.MarkTargetRefused();
                e.Handled = true;
                return;
            }
```

- [ ] **Step 6: 条目右键菜单分流 + 面板空白菜单闸门 + 删除死代码**

`Controls/FolderWidget.Interactions.cs`：

1. **在 `Item_RightClick` 的 `if (sender is Border b && b.DataContext is DisplayItem item)` 块内**
   （即 `var menu = new ContextMenu();` 之前）插入浏览态分支——`item` 是模式匹配变量，只有块内可见，
   写在方法开头会编译不过：

```csharp
            if (IsBrowsing)
            {
                ShowBrowseItemMenu(item);
                e.Handled = true;
                return;
            }
```

2. **面板空白处右键在浏览态不弹菜单**：`ExpandedPanel_MouseRightButtonDown` 在确认"点的是空白处"
   之后、调用 `ShowPanelContextMenu()` 之前加闸门。为什么：面板菜单的"新建文件/新建文件夹"会往
   Kobold 存储写文件并往 `_data.Items` 追加条目——浏览态说好是只读视图，而且新建出来的条目在当前
   视图里根本看不见，容易误导。

```csharp
            // Browsing is a read-only view: the panel menu's New File / New Folder
            // would write to storage and mutate the widget's items.
            if (IsBrowsing)
            {
                e.Handled = true;
                return;
            }

            ShowPanelContextMenu();
            e.Handled = true;
```

2. 根态菜单里三处内联的 `Process.Start` / `Clipboard.SetText` 换成辅助方法，保持行为不变：

```csharp
                var openItem = new MenuItem { Header = Localization.Get("Menu_Open") };
                openItem.Click += (s, a) => OpenWithShell(item.Path);

                var locItem = new MenuItem { Header = Localization.Get("Menu_OpenLocation") };
                locItem.Click += (s, a) => OpenContainingFolder(item.Path);
```

```csharp
                var copyPathItem = new MenuItem { Header = Localization.Get("Menu_CopyPath") };
                copyPathItem.Click += (s, a) => CopyPathToClipboard(item.Path);
```

3. 删除死代码方法 `Item_DoubleClick`（整个方法，含 XML 注释）——它从未接线，双击已由 `Item_MouseDown` 处理。

- [ ] **Step 7: 面板隐藏时重置浏览 + `Backspace` 返回**

`Controls/FolderWidget.Interactions.cs`：

1. `HidePanel()` 里 `ClearAllSelections();` 之后加一行：

```csharp
            ResetBrowse(); // browsing is transient - the panel reopens at the widget's items
```

2. `Window_KeyDown` 开头加：

```csharp
            if (e.Key == Key.Back && IsBrowsing)
            {
                GoBack();
                e.Handled = true;
                return;
            }
```

- [ ] **Step 8: 标题栏返回按钮（XAML）**

`Controls/FolderWidget.xaml`：把标题栏里原来的

```xml
                            <TextBlock x:Name="PanelHeaderText" Grid.Column="0" Text="Files"
                                       FontSize="13" FontWeight="SemiBold" FontFamily="Segoe UI"
                                       VerticalAlignment="Center"/>
```

替换为：

```xml
                            <Grid Grid.Column="0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>

                                <!-- Back: only visible while browsing a folder -->
                                <Border x:Name="BackButton" Grid.Column="0" Width="24" Height="24" CornerRadius="6"
                                        Background="Transparent" Cursor="Hand"
                                        Margin="0,0,6,0" Visibility="Collapsed"
                                        MouseLeftButtonDown="BackButton_Click">
                                    <Path Stroke="{DynamicResource Kobold.Brush.IconFill}" StrokeThickness="2"
                                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                                          Data="M7,1 L1,7 L7,13" Stretch="Uniform" Width="8" Height="14"
                                          HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                </Border>
                                <TextBlock x:Name="PanelHeaderText" Grid.Column="1" Text="Files"
                                           FontSize="13" FontWeight="SemiBold" FontFamily="Segoe UI"
                                           VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                            </Grid>
```

**为什么是两列 `Grid` 而不是水平 `StackPanel`**：`StackPanel` 以无限宽度测量子元素，`TextTrimming` 永远不会生效，
长文件夹名会直接压到右侧的帮助/锁/钉按钮上；`Auto` + `*` 的 `Grid` 才能让标题按剩余宽度截断。

- [ ] **Step 9: 三语文案**

`Core/Localization.cs`：在**每个**语言块里 `["Menu_CopyPath"] = ...` 那一行之前插入下面四行（注意每个语言块的最后一项不带逗号，四行新条目都带逗号）：

```csharp
                ["UI_BrowseBack"] = "Back to the widget",
                ["UI_FolderEmpty"] = "This folder is empty",
                ["UI_FolderAccessDenied"] = "This folder cannot be opened",
                ["UI_BrowseMoreItems"] = "{0} more items - click to open in Explorer",
```

zh：

```csharp
                ["UI_BrowseBack"] = "返回组件",
                ["UI_FolderEmpty"] = "此文件夹为空",
                ["UI_FolderAccessDenied"] = "无法访问此文件夹",
                ["UI_BrowseMoreItems"] = "还有 {0} 项未显示 - 点击在资源管理器中打开",
```

ja：

```csharp
                ["UI_BrowseBack"] = "ウィジェットに戻る",
                ["UI_FolderEmpty"] = "このフォルダーは空です",
                ["UI_FolderAccessDenied"] = "このフォルダーを開けません",
                ["UI_BrowseMoreItems"] = "他に {0} 件あります - クリックでエクスプローラーで開く",
```

- [ ] **Step 10: 把 `FolderWidget` 纳入 XAML 加载自检**

`tests/XamlLoadCheck/Program.cs`：在 `IslandWindow` 之后加一行（`FolderData` 在 `Kobold.Core`，已 using）：

```csharp
                failures += TryLoad("FolderWidget", () => new FolderWidget(new FolderData()));
```

- [ ] **Step 11: 构建 + 全部自检**

Run: `dotnet build Kobold.csproj -c Debug`
Expected: 0 errors、0 warnings（新增代码不引入未使用变量）

Run（逐个，全部应为退出码 0）:
`dotnet run --project tests/FolderListingCheck`、`dotnet run --project tests/LangCheck`、
`dotnet run --project tests/XamlLoadCheck`、`dotnet run --project tests/UiTokensCheck`、
`dotnet run --project tests/WidgetItemsCheck`

- [ ] **Step 12: 手动验证**

Run: `bin\Debug\net48\Kobold.exe`
1. 双击组件里的文件夹条目 → 面板内显示其内容，**没有**新的资源管理器窗口。
2. 面板标题变为该文件夹名，左上出现返回箭头；双击子文件夹可继续下钻。
3. 点返回箭头 / 按 `Backspace` → 逐级退回；回到根态后返回箭头消失、组件条目与角标完好。
4. 浏览态右键一个条目 → 只有 打开 / 打开文件位置 / 复制路径 三项（**没有**重命名/移出）。
5. 浏览态拖动条目 → 无拖动幽灵、无插入指示线；从资源管理器拖文件到面板 → 光标显示禁止。
6. 按 `Esc` → 面板收起（行为未变）；重新打开 → 回到根态。
7. 关闭并重开面板后，`config.json` 内容不变（浏览位置未持久化）。

- [ ] **Step 13: 提交（需用户许可）**

```bash
git add Core/WidgetConstants.cs Core/Localization.cs Controls/DisplayItem.cs Controls/FolderWidget.Browse.cs Controls/FolderWidget.xaml Controls/FolderWidget.xaml.cs Controls/FolderWidget.Interactions.cs Controls/FolderWidget.DragDrop.cs tests/XamlLoadCheck
git commit -m "feat(browse): navigate folders inside the panel with a back button"
```

---

### Task 3: 高度上限 + 内部滚动 + 截断页脚

**Files:**
- Modify: `Core/WidgetConstants.cs`
- Modify: `Controls/FolderWidget.Theme.cs:145-159`（`CalculatePanelSize`）
- Modify: `Controls/FolderWidget.xaml:147-254`（内容区包 `ScrollViewer`、覆盖层下移、加页脚行）
- Modify: `Controls/FolderWidget.Browse.cs`（页脚点击）
- Modify: `Controls/FolderWidget.xaml.cs`（页脚显隐 + `_lastFooterVisible`）
- Test: `tests/FolderListingCheck`、`tests/XamlLoadCheck`、`tests/UiTokensCheck`、手动（拖拽/套索回归）

**Interfaces:**
- Consumes: `_browseListing`、`_lastItemCount`（Task 2）
- Produces: `WidgetConstants.MAX_BROWSE_ENTRIES`（已存在）、`WidgetConstants.FOOTER_HEIGHT`、`FolderWidget.MoreItemsText`、`UpdateMoreItemsFooter(int itemCount)`

- [ ] **Step 1: 加常量**

`Core/WidgetConstants.cs`，在 `#region Folder Browse` 内追加：

```csharp
        /// <summary>Height of the "more items" footer row when a listing is truncated</summary>
        public const int FOOTER_HEIGHT = 20;

        /// <summary>Panel height cap as a share of the work area</summary>
        public const double PANEL_MAX_HEIGHT_RATIO = 0.6;
```

- [ ] **Step 2: 高度上限 + 滚动时的宽度补偿**

`Controls/FolderWidget.Theme.cs`：把 `CalculatePanelSize()` 整体替换为：

```csharp
        private (int width, int height) CalculatePanelSize()
        {
            int cols = Math.Max(1, _data.GridColumns);
            int itemCount = Math.Max(1, _lastItemCount); // At least 1 for empty state
            int rows = (int)Math.Ceiling((double)itemCount / cols);

            int naturalHeight = HEADER_HEIGHT + rows * GetScaledItemHeight() + PADDING;
            if (_lastFooterVisible) naturalHeight += WidgetConstants.FOOTER_HEIGHT;

            int maxHeight = Math.Max(120,
                (int)(SystemParameters.WorkArea.Height * WidgetConstants.PANEL_MAX_HEIGHT_RATIO));

            // A long listing scrolls instead of growing past the cap; the in-flow
            // scrollbar needs its width added so items are not clipped.
            bool scrolls = naturalHeight > maxHeight;

            int width = cols * GetScaledItemWidth() + PADDING;
            if (scrolls) width += (int)SystemParameters.VerticalScrollBarWidth;

            int height = scrolls ? maxHeight : naturalHeight;

            // Minimum dimensions
            width = Math.Max(width, 180);
            height = Math.Max(height, 120);

            return (width, height);
        }
```

- [ ] **Step 3: 内容区包 `ScrollViewer` + 页脚行（XAML）**

`Controls/FolderWidget.xaml`：

1. `Grid.RowDefinitions` 加一行（放在 `Height="*"` 之后）：

```xml
                        <RowDefinition Height="Auto"/>
```

2. 把 `ItemsControl x:Name="ItemsContainer"` 与它后面的 `DropIndicator`、`SelectionRect` 三个元素一起包进 `ScrollViewer + Grid`（`Grid.Row="1"` 仍在 ScrollViewer 上；两个覆盖层从 `Grid.Row="1"` 的兄弟变成 `ItemsHost` 的子元素，坐标因此自动跟随滚动）：

```xml
                    <!-- Items - scrolls when the listing is taller than the panel cap -->
                    <ScrollViewer x:Name="ItemsScroller" Grid.Row="1"
                                  VerticalScrollBarVisibility="Auto"
                                  HorizontalScrollBarVisibility="Disabled"
                                  PanningMode="VerticalOnly"
                                  Background="Transparent">
                        <Grid x:Name="ItemsHost" Background="Transparent">
                            <!-- 原来的 ItemsControl（保持原样，含 Margin="8,4,8,8" 与全部事件） -->
                            <ItemsControl x:Name="ItemsContainer" Margin="8,4,8,8" ...>
                                ...
                            </ItemsControl>

                            <!-- 原来的 DropIndicator（去掉 Grid.Row="1"） -->
                            <Rectangle x:Name="DropIndicator" ... />

                            <!-- 原来的 SelectionRect（去掉 Grid.Row="1"） -->
                            <Rectangle x:Name="SelectionRect" ... />
                        </Grid>
                    </ScrollViewer>
```

注意：`ItemsControl` 内部整段模板内容保持原样不动；只调整外层层级、去掉两个 `Rectangle` 的 `Grid.Row="1"`。

4. **必须同时修正插入指示线的坐标基准**（预检发现的缺陷）：`DropIndicator.Parent` 现在是
   `ItemsHost`，它的原点已经在内容区顶部（头行之下），所以 `ItemsContainer_DragOver` 里
   原来那次"减去头行高度"的换算必须去掉——否则指示线会被上移一个头行高度。把

```csharp
                    // Mapped points start at the panel top, but the indicator's
                    // margin starts below the header row.
                    double rowTop = PanelHeader.ActualHeight;
```

改为：

```csharp
                    // The indicator's parent (ItemsHost) already starts below the
                    // header row, so no header-height correction is needed.
                    double rowTop = 0;
```

（`indicatorY`、`indicatorX` 的表达式中其余部分不动；`SelectionRect` 的坐标基准不变：
`ItemsContainer` 的 `Margin="8,4,8,8"` 造成的 8/4 偏移在改动前后完全一致。）

3. 在 `EmptyState` 之后（Grid.Row 3 不存在，页脚用 `Grid.Row="2"`）加：

```xml
                    <!-- Truncated listing hint (browse mode only) -->
                    <TextBlock x:Name="MoreItemsText" Grid.Row="2" Margin="12,0,12,8"
                               FontSize="11" FontFamily="Segoe UI"
                               Foreground="{DynamicResource Kobold.Brush.TextFaint}"
                               TextTrimming="CharacterEllipsis" HorizontalAlignment="Left"
                               Cursor="Hand" Visibility="Collapsed"
                               MouseLeftButtonUp="MoreItemsText_MouseLeftButtonUp"/>
```

- [ ] **Step 4: 页脚显隐、滚动归位与点击**

`Controls/FolderWidget.xaml.cs`：

1. 字段区新增 `private bool _lastFooterVisible;`。
2. **把 `UpdateEmptyState(items.Count);` 与新的 `UpdateMoreItemsFooter(items.Count);` 一起移到 `if (_isExpanded) { … CalculatePanelSize() … }` 之前**。为什么：`CalculatePanelSize()` 会读 `_lastFooterVisible` 来决定要不要 +`FOOTER_HEIGHT`；如果页脚状态在尺寸算完之后才更新，面板高度会慢一拍——从"截断的目录"退回根态时，面板会白白高出 20px 直到下一次 `UpdateUI()`。
3. 新增方法：

```csharp
        private void UpdateMoreItemsFooter(int itemCount)
        {
            bool show = IsBrowsing && _browseListing != null && _browseListing.Truncated;

            _lastFooterVisible = show;
            MoreItemsText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (show)
            {
                MoreItemsText.Text = Localization.Format("UI_BrowseMoreItems",
                    _browseListing.TotalCount - itemCount);
            }
        }
```

`Controls/FolderWidget.Browse.cs`：

4. 页脚点击 + 浏览切换时滚动归位（否则从深层目录返回会停在半截，且新目录可能直接显示在中段）：

```csharp
        private void MoreItemsText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!IsBrowsing) return;

            OpenWithShell(CurrentBrowsePath); // a directory opens in Explorer
            e.Handled = true;
        }
```

5. `EnterFolder` 与 `GoBack` 在 `UpdateUI()` 之后各加一行 `ItemsScroller.ScrollToTop();`。
   只在浏览切换时归位——根态的 `UpdateUI()`（改名、换色、拖拽后刷新）不应把列表滚回顶部。

- [ ] **Step 5: 构建 + 自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: `dotnet run --project tests/XamlLoadCheck` → Expected: `ALL XAML LOAD CHECKS PASSED`
Run: `dotnet run --project tests/UiTokensCheck` → Expected: 全绿（新 XAML 无十六进制颜色）
Run: `dotnet run --project tests/FolderListingCheck` → Expected: 全绿

- [ ] **Step 6: 手动验证（含拖拽/套索回归）**

Run: `bin\Debug\net48\Kobold.exe`
1. 找一个 500+ 条目的目录 → 面板高度不超过工作区约 60%，内部可滚动，页脚显示"还有 N 项未显示"；点页脚 → 资源管理器打开该目录。
2. 找一个条目数超出一个屏幕的**组件**（根态）→ 面板同样出现滚动而不是超出屏幕（**回归项**：这是有意改变的行为）。
3. 滚动到中部后，从资源管理器拖一个文件到面板 → 插入指示线落在正确位置；拖拽排序后顺序正确（**回归项**）。
4. 在面板空白处按住左键框选 → 套索矩形与鼠标位置一致（**回归项**）。
5. 缩放到 125%/150% 再重复 1 与 3（`LayoutTransform` 与滚动条的相对位置）。

- [ ] **Step 7: 提交（需用户许可）**

```bash
git add Core/WidgetConstants.cs Controls/FolderWidget.Theme.cs Controls/FolderWidget.xaml Controls/FolderWidget.xaml.cs Controls/FolderWidget.Browse.cs
git commit -m "feat(browse): cap panel height and scroll long listings"
```

---

### Task 4: 图标加载上限 + 通用文件图标

**Files:**
- Modify: `Core/WidgetConstants.cs`
- Modify: `Controls/FolderWidget.xaml.cs`（`LoadItemIcons`）
- Modify: `Controls/FolderWidget.Dialogs.cs:114-218`（shell 图标：抽取通用图标）
- Test: `tests/XamlLoadCheck`、手动（大目录图标表现）

**Interfaces:**
- Consumes: `FolderWidget.IsBrowsing`、`DisplayItem.IsDirectory`（Task 2）
- Produces: `WidgetConstants.MAX_BROWSE_ICON_LOADS`、`private ImageSource GetGenericFileIcon()`

- [ ] **Step 1: 加常量**

`Core/WidgetConstants.cs`，在 `#region Folder Browse` 内追加：

```csharp
        /// <summary>Max real shell icons loaded for one browse listing (the rest fall back)</summary>
        public const int MAX_BROWSE_ICON_LOADS = 60;
```

- [ ] **Step 2: `GetFileIcon` 支持通用文件图标**

`Controls/FolderWidget.Dialogs.cs`：在 `_iconCache` 旁加常量：

```csharp
        // Sentinel key: the generic "file" icon used for browse entries whose
        // real icon was not loaded (memory guard for very long listings).
        private const string GENERIC_FILE_ICON_KEY = "::file::";
```

把 `GetFileIcon(string path)` 的前半段（缓存 key 计算 + 存在性判断 + `SHGetFileInfo` 调用）替换为：

```csharp
        private ImageSource GetFileIcon(string path)
        {
            try
            {
                string cacheKey;
                uint attributes = 0;
                bool byAttributes = false;

                if (string.Equals(path, GENERIC_FILE_ICON_KEY, StringComparison.Ordinal))
                {
                    // Attribute-based lookup on a name without extension -> the
                    // shell's generic "file" icon.
                    cacheKey = GENERIC_FILE_ICON_KEY;
                    byAttributes = true;
                }
                else if (System.IO.Directory.Exists(path))
                {
                    cacheKey = "::folder::";
                    attributes = FILE_ATTRIBUTE_DIRECTORY;
                    byAttributes = true;
                }
                else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    cacheKey = path.ToLowerInvariant(); // Full path for shortcuts
                }
                else
                {
                    cacheKey = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? "::noext::";
                }

                if (!byAttributes && !System.IO.File.Exists(path)) return null;

                // Check cache first
                lock (_cacheLock)
                {
                    if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
                    {
                        return cachedIcon;
                    }
                }

                // Load icon from Shell
                var shinfo = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON | (byAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
                string lookupPath = byAttributes && cacheKey != "::folder::" ? "file" : path;

                SHGetFileInfo(lookupPath, byAttributes ? attributes : 0, ref shinfo,
                    (uint)Marshal.SizeOf(shinfo), flags);
```

方法其余部分（`if (shinfo.hIcon != IntPtr.Zero)` 到结尾的缓存与 `catch`）保持不变。

在 `GetFileIcon` 之后新增：

```csharp
        /// <summary>Generic file icon for browse entries whose real icon was skipped.</summary>
        private ImageSource GetGenericFileIcon() => GetFileIcon(GENERIC_FILE_ICON_KEY);
```

- [ ] **Step 3: 浏览态限制图标加载**

`Controls/FolderWidget.xaml.cs`：把 `LoadItemIcons` 替换为：

```csharp
        private void LoadItemIcons(List<DisplayItem> items)
        {
            // Root mode keeps loading every icon. Browse mode caps the real shell
            // lookups (long listings x SHGetFileInfo x BitmapSource is the memory
            // risk): directories share one cached icon, the rest get the generic
            // file icon.
            int realLimit = IsBrowsing
                ? Math.Min(items.Count, WidgetConstants.MAX_BROWSE_ICON_LOADS)
                : items.Count;

            var folders = new List<DisplayItem>();
            var others = new List<DisplayItem>();
            if (IsBrowsing)
            {
                for (int i = realLimit; i < items.Count; i++)
                {
                    if (items[i].IsDirectory) folders.Add(items[i]);
                    else others.Add(items[i]);
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < realLimit; i++)
                {
                    ApplyIcon(items[i], GetFileIcon(items[i].Path));
                }

                foreach (var folder in folders)
                {
                    ApplyIcon(folder, GetFileIcon(folder.Path));
                }

                if (others.Count > 0)
                {
                    var generic = GetGenericFileIcon();
                    foreach (var item in others)
                    {
                        ApplyIcon(item, generic);
                    }
                }
            });
        }
```

- [ ] **Step 4: 构建 + 自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 errors
Run: `dotnet run --project tests/XamlLoadCheck` → Expected: 全绿

- [ ] **Step 5: 手动验证**

Run: `bin\Debug\net48\Kobold.exe`
1. 打开一个 500+ 条目的目录（例如 `C:\Windows\System32` 下的一个中等目录，或自己造一个 300 文件的临时目录）→ 面板立即出现，滚动流畅；前 60 个条目显示真实图标。
2. 第 61 个之后的**文件**显示通用文件图标（不是空白）；**文件夹**仍显示文件夹图标。
3. 反复进出该目录 5 次 → 用 `%TEMP%\opencode\kobold-windows.ps1` 观察私有内存在 ±3 MB 内波动，且不持续上升（图标缓存有上限）。
4. 根态（组件自己的条目）图标行为与改动前一致。

- [ ] **Step 6: 提交（需用户许可）**

```bash
git add Core/WidgetConstants.cs Controls/FolderWidget.Dialogs.cs Controls/FolderWidget.xaml.cs
git commit -m "perf(browse): cap browse icon loads and fall back to a generic file icon"
```

---

### Task 5: 全量验证 + 文档

**Files:**
- Modify: `README.md`（中英功能表）
- Test: 全部 `tests/*Check` + 手动验收清单

**Interfaces:**
- Consumes: 前四个 Task 的全部产出
- Produces: 无新接口

- [ ] **Step 1: 跑全部自检**

Run（逐个，全部应为退出码 0）：

```powershell
dotnet run --project tests/LangCheck
dotnet run --project tests/WidgetItemsCheck
dotnet run --project tests/ScreenGeometryCheck
dotnet run --project tests/IslandLayoutCheck
dotnet run --project tests/StorageOpsCheck
dotnet run --project tests/DesktopIconsCheck
dotnet run --project tests/UiTokensCheck
dotnet run --project tests/XamlLoadCheck
dotnet run --project tests/FolderListingCheck
```

- [ ] **Step 2: 手动验收（完整走一遍 spec 的清单）**

Run: `bin\Debug\net48\Kobold.exe`
1. 双击组件里的文件夹 → 面板内显示内容，无资源管理器窗口。
2. 连下钻 3 层并逐级返回 → 回到根态，组件条目与角标完好。
3. 500+ 条目目录 → 面板不超高、内部滚动、页脚出现。
4. 无权限目录（如 `C:\System Volume Information`）→ "无法访问此文件夹"空态，不崩溃。
5. 浏览态右键 → 只读三项；拖拽无反应。
6. 收起面板再打开 → 回到根态。
7. 组件锁定（锁图标点亮）时浏览仍可用；根态的收纳/移出/重命名不受影响。
8. 浅色/深色主题切换后浏览态配色正常（页脚、返回箭头跟随主题）。

- [ ] **Step 3: README 补功能说明**

`README.md`：英文功能列表在 "Folder panels" 一条后追加：

```markdown
- **Browse folders in place** — double-click a folder entry to list its
  contents inside the panel: drill in, go back (button or Backspace), open
  files with the shell. Browsing is read-only — it never touches the
  widget's items or your files.
```

中文功能列表在 "文件夹面板" 一条后追加：

```markdown
- **面板内浏览文件夹** — 双击组件里的文件夹条目即可在面板内就地查看其内容：
  逐级下钻、返回（按钮或 Backspace）、文件交给系统默认程序打开。浏览是只读的，
  不会改动组件条目，也不会改动磁盘上的文件。
```

同时在"用法"表中补一行（中英各一处）：

```markdown
| Browse a folder inside a panel | Double-click a folder entry (Backspace goes back) |
```

```markdown
| 在面板内浏览文件夹 | 双击文件夹条目（Backspace 返回上一级） |
```

- [ ] **Step 4: 提交（需用户许可）**

```bash
git add README.md
git commit -m "docs(browse): document in-panel folder browsing"
```

---

## 自检（写完后逐条核对，已通过）

**1. Spec 覆盖**

| Spec 要求 | 对应 Task |
|---|---|
| `Core/FolderListing.cs`（枚举/排序/过滤/截断/容错） | Task 1 |
| `DisplayItem.IsDirectory` | Task 2 Step 2 |
| 浏览栈 + `EnterFolder`/`GoBack`/`ResetBrowse` | Task 2 Step 3 |
| `UpdateUI()` 数据源切换 | Task 2 Step 4 |
| 标题栏返回按钮（含 `e.Handled`） | Task 2 Step 8 |
| 双击分流（文件夹进入 / 文件 shell） | Task 2 Step 5 |
| `Backspace` 返回、`Esc` 保持收起 | Task 2 Step 7 |
| 浏览态只读右键菜单 | Task 2 Step 6 |
| 禁用拖出与拖入 | Task 2 Step 5 |
| 面板隐藏时清浏览栈 | Task 2 Step 7 |
| 高度上限 0.6 + 内部滚动 + 宽度补偿 | Task 3 Steps 2-3 |
| 目录条目上限 200 + "还有 N 项"页脚 | Task 1（截断）+ Task 3 Steps 3-4 |
| `ScrollViewer` 层级与覆盖层坐标 | Task 3 Step 3 |
| 图标加载上限 60 + 通用文件图标 | Task 4 |
| 死代码 `Item_DoubleClick` 删除 | Task 2 Step 6 |
| 三语文案 4 个 key | Task 2 Step 9 |
| `XamlLoadCheck` 覆盖 `FolderWidget` | Task 2 Step 10 |
| 手动验收清单 | Task 2 Step 12 / Task 3 Step 6 / Task 4 Step 5 / Task 5 Step 2 |
| README | Task 5 Step 3 |

**2. 占位符扫描**：无 TBD/TODO；每个代码步骤都给了可直接粘贴的完整片段。

**3. 类型一致性核对**：`FolderListing.ListChildren(string, int)` / `ListingResult.{Entries,TotalCount,Truncated,Failed}` / `BrowseEntry.{Name,Path,IsDirectory}`（Task 1 定义，Task 2 消费）；`IsBrowsing`、`CurrentBrowsePath`、`_browseListing`、`_lastItemCount`、`_lastFooterVisible`（Task 2 定义，Task 3/4 消费）；`OpenWithShell` / `OpenContainingFolder` / `CopyPathToClipboard`（Task 2 定义，Task 3 页脚复用）；常量名 `MAX_BROWSE_ENTRIES` / `MAX_BROWSE_ICON_LOADS` / `FOOTER_HEIGHT` / `PANEL_MAX_HEIGHT_RATIO` 前后一致。

**4. 已知风险（执行时留意）**：`ScrollViewer` 引入后 `DropIndicator`/`SelectionRect` 的坐标语义变化（Task 3 Step 6 有回归验证）；`_lastItemCount` 在一次 `UpdateUI()` 内被读取，`ShowPanel()` 先 `UpdateUI()` 再 `CalculatePanelSize()`，顺序不能颠倒。
