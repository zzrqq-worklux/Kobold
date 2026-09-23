# Kobold 浏览文件夹实时刷新（Browse Refresh）实现计划

> **状态（2026-09-23）**：待执行。需求与设计已在会话中与用户逐条确认（bounded 路径）：
> 范围**仅浏览视图**；浏览中的文件夹被外部删除/改名时显示"无法访问此文件夹"空态并停止监听。
> **提交约定**：本计划每步末尾给出 commit 命令，但**执行时必须先获得用户明确许可**才可提交。
> **工作区**：`.worktrees/browse-refresh`（分支 `feat/browse-refresh`）。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 面板停留在某个文件夹视图时，外部对该文件夹的新增 / 删除 / 改名 / 属性变化在约 0.4s 内自动反映到面板上；刷新不打断拖拽与框选，滚动位置与仍然存在的选中项保持不变。

**Architecture:** 新增纯 Core 的 `FolderWatcher`（`FileSystemWatcher` + 防抖，无 WPF 依赖，可被控制台自检覆盖）；新增 `Controls/FolderWidget.Watch.cs` partial 负责生命周期与刷新决策——只有面板展开且处于浏览态时监听当前文件夹，变化后重读目录并用新增的 `FolderListing.SameEntries` 与当前列表比对，确有不同才保留滚动 / 选中并重绘。

**Tech Stack:** WPF (net48)、C# 7.3、`System.IO.FileSystemWatcher`、`System.Threading.Timer`、`System.Windows.Threading.DispatcherTimer`、现有控制台自检（`tests/*Check`）。

**Spec:** 无独立 spec 文档——需求与设计在会话中确认（bounded）：仅浏览视图；文件夹消失 → 空态 + 停止监听；不监听子目录、不做轮询兜底、不动根视图。

## Global Constraints

- 目标框架 net48，`LangVersion 7.3`（不要使用 C# 8+ 语法）。
- 不新增 UI 文案，因此不需要改 `Core/Localization.cs`；`tests/LangCheck` 必须保持全绿。
- 不新增 XAML、不出现十六进制颜色字面量；`tests/UiTokensCheck`、`tests/XamlLoadCheck` 必须保持全绿。
- 浏览态仍然**只读**：本功能只读目录、不写文件、不改 `_data.Items`。
- 每完成一个 Task 必须：`dotnet build Kobold.csproj -c Debug` 0 错误 0 警告；与该 Task 相关的 `tests/*Check` 全绿。
- 手动验证运行 `bin\Debug\net48\Kobold.exe`（面板交互需要用户手动执行，见 Task 3 Step 5）。
- 提交信息用 conventional commits（`feat(browse): ...`）。**提交前必须获得用户许可。**

---

## 文件结构

| 文件 | 责任 | 动作 |
|---|---|---|
| `Core/FolderListing.cs` | 新增纯函数 `SameEntries`：两次目录列表是否等价（供跳过无意义重绘） | 修改 |
| `Core/FolderWatcher.cs` | 单目录监听 + 防抖，纯 Core、无 WPF 依赖 | 新建 |
| `Core/WidgetConstants.cs` | 新增防抖常量 | 修改 |
| `Controls/FolderWidget.Watch.cs` | 监听生命周期、刷新决策、滚动/选中保持 | 新建 |
| `Controls/FolderWidget.xaml.cs` | 订阅/释放 watcher；`UpdateUI()` 末尾 reconcile | 修改 |
| `Controls/FolderWidget.Interactions.cs` | `ShowPanel`/`HidePanel` 启停监听 | 修改 |
| `tests/FolderListingCheck/Program.cs` | `SameEntries` 用例 | 修改 |
| `tests/FolderWatchCheck/` | `FolderWatcher` 控制台自检（真实文件系统 + 条件等待） | 新建 |
| `README.md` | 功能表补充"实时跟随文件夹变化" | 修改 |

---

### Task 1: `FolderListing.SameEntries` 纯逻辑 + 自检

> 执行状态（2026-09-23）：代码与自检已完成并全绿；提交待用户许可。

**Files:**
- Modify: `Core/FolderListing.cs`（在 `ListChildren` 之后、`ReadEntries` 之前插入）
- Modify: `tests/FolderListingCheck/Program.cs:30-42`（Main 调用列表）、`:292`（`TryDelete` 之前插入新方法）

**Interfaces:**
- Produces: `Kobold.Core.FolderListing.SameEntries(ListingResult a, ListingResult b) -> bool`
  - 语义：两者描述同一批条目（顺序、路径[忽略大小写]、目录标志、`Failed`、`TotalCount` 全部一致）时返回 true；`null` 只与 `null` 相等。

- [ ] **Step 1: 写失败的自检用例**

`tests/FolderListingCheck/Program.cs`：在 `Main` 的调用列表里，`BrowseMoveFilters();`（第 41 行）之后加一行：

```csharp
                SameEntriesComparison();
```

在 `BrowseMoveFilters()` 方法之后、`TryDelete`（第 292 行）之前插入：

```csharp
        private static void SameEntriesComparison()
        {
            string a = NewDir("case-same-a");
            string b = NewDir("case-same-b");
            File.WriteAllText(Path.Combine(a, "one.txt"), "");
            Directory.CreateDirectory(Path.Combine(a, "sub"));
            File.WriteAllText(Path.Combine(b, "one.txt"), "");
            Directory.CreateDirectory(Path.Combine(b, "sub"));

            var left = FolderListing.ListChildren(a, 100);
            var right = FolderListing.ListChildren(b, 100);

            // The browser only ever compares two reads of the same folder, so the
            // full path is part of the identity: same names in another folder are
            // not the same listing.
            Check(FolderListing.SameEntries(left, FolderListing.ListChildren(a, 100)),
                "same-entries: a re-read of the same folder matches");
            Check(!FolderListing.SameEntries(left, right),
                "same-entries: listings of different folders never match");
            Check(FolderListing.SameEntries(left, left), "same-entries: a listing matches itself");
            Check(!FolderListing.SameEntries(null, left) && !FolderListing.SameEntries(left, null),
                "same-entries: null never matches a listing");
            Check(FolderListing.SameEntries(null, null), "same-entries: two nulls match (nothing to redraw)");

            File.WriteAllText(Path.Combine(b, "two.txt"), "");
            Check(!FolderListing.SameEntries(left, FolderListing.ListChildren(b, 100)),
                "same-entries: an added file is a difference");

            var reordered = FolderListing.ListChildren(a, 100);
            reordered.Entries.Reverse();
            Check(!FolderListing.SameEntries(left, reordered), "same-entries: order matters");

            var caseChanged = FolderListing.ListChildren(a, 100);
            caseChanged.Entries[0].Path = caseChanged.Entries[0].Path.ToUpperInvariant();
            Check(FolderListing.SameEntries(left, caseChanged),
                "same-entries: paths compare case-insensitively (Windows)");

            var kindChanged = FolderListing.ListChildren(a, 100);
            kindChanged.Entries[0].IsDirectory = !kindChanged.Entries[0].IsDirectory;
            Check(!FolderListing.SameEntries(left, kindChanged), "same-entries: a changed kind is a difference");

            var unreadable = FolderListing.ListChildren(Path.Combine(_root, "does-not-exist"), 100);
            Check(!FolderListing.SameEntries(left, unreadable),
                "same-entries: an unreadable folder never matches a listing");

            Check(!FolderListing.SameEntries(left, FolderListing.ListChildren(a, 1)),
                "same-entries: a different total count is a difference");
        }
```

- [ ] **Step 2: 写最小桩实现（能编译、必然失败）**

`Core/FolderListing.cs`：在 `ListChildren` 之后插入：

```csharp
        /// <summary>
        /// True when both listings describe the same entries in the same order.
        /// The browser uses it to skip a re-render when a watched folder did not
        /// change in a way the listing shows.
        /// </summary>
        public static bool SameEntries(ListingResult a, ListingResult b)
        {
            return true;
        }
```

- [ ] **Step 3: 运行自检，确认失败**

Run: `dotnet run --project tests/FolderListingCheck`
Expected: 退出码 1；6 条失败全部是"应当判定为不同"的用例（`null` 合并为一条断言：null vs 列表 + 新增文件 + 顺序 + 种类 + 不可读 + 总数），"应当相同"的用例全部通过。

> 执行记录（2026-09-23）：桩实现下 `same-entries: identical listings from different folders match` 曾用**错误断言**（不同文件夹同名列表应相同）蒙混通过；实现真实逻辑后该断言正确失败。已改为 `a re-read of the same folder matches` + `listings of different folders never match`，与"按完整路径比较"的语义一致。

- [ ] **Step 4: 实现真实逻辑**

`Core/FolderListing.cs`：把 Step 2 的方法体替换为：

```csharp
        public static bool SameEntries(ListingResult a, ListingResult b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            // Failed drives the empty-state text: an inaccessible folder and an
            // empty folder must never look the same to the browser.
            if (a.Failed != b.Failed) return false;
            if (a.TotalCount != b.TotalCount) return false;
            if (a.Entries.Count != b.Entries.Count) return false;

            for (int i = 0; i < a.Entries.Count; i++)
            {
                var left = a.Entries[i];
                var right = b.Entries[i];

                if (left.IsDirectory != right.IsDirectory) return false;
                if (!string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }
```

- [ ] **Step 5: 构建 + 运行自检，确认全绿**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 错误 0 警告
Run: `dotnet run --project tests/FolderListingCheck` → Expected: `ALL FOLDER-LISTING CHECKS PASSED`，退出码 0

- [ ] **Step 6: 提交（需用户许可）**

```bash
git add Core/FolderListing.cs tests/FolderListingCheck/Program.cs
git commit -m "feat(browse): add FolderListing.SameEntries for change detection"
```

---

### Task 2: `FolderWatcher`（Core）+ `tests/FolderWatchCheck`

> 执行状态（2026-09-23）：代码与自检已完成并全绿（20/20）；桩缺少带参构造函数的计划疏漏已在执行中修正；提交待用户许可。

**Files:**
- Create: `Core/FolderWatcher.cs`
- Create: `tests/FolderWatchCheck/FolderWatchCheck.csproj`
- Create: `tests/FolderWatchCheck/Program.cs`
- Modify: `Core/WidgetConstants.cs`（`#region Folder Browse` 内，`:91` 之后）

**Interfaces:**
- Consumes: `WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS`（本 Task 新增）
- Produces:
  - `Kobold.Core.FolderWatcher(int debounceMs = WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS)`
  - `event Action Changed`（每次变化风暴只触发一次，在池线程上）
  - `bool Start(string path)`（不可监听时返回 false，不抛异常）
  - `void Stop()` / `void Dispose()` / `bool IsWatching` / `string Path`

- [ ] **Step 1: 加常量**

`Core/WidgetConstants.cs`，`#region Folder Browse` 内（`PANEL_MAX_HEIGHT_RATIO` 之后）追加：

```csharp
        /// <summary>Quiet time after the last folder change before the browser re-reads it</summary>
        public const int BROWSE_WATCH_DEBOUNCE_MS = 400;
```

- [ ] **Step 2: 建自检工程**

`tests/FolderWatchCheck/FolderWatchCheck.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>Kobold.FolderWatchCheck</AssemblyName>
    <RootNamespace>Kobold.FolderWatchCheck</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Kobold.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 写失败的自检**

`tests/FolderWatchCheck/Program.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Kobold.Core;

namespace Kobold.FolderWatchCheck
{
    /// <summary>
    /// Checks for FolderWatcher (debounced change events, start/stop semantics).
    /// Touches the real filesystem - Windows only. Exit code 0 = all green.
    /// Run: dotnet run --project tests/FolderWatchCheck
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;
        private static int _events;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            _root = Path.Combine(Path.GetTempPath(), "kobold-folderwatch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            try
            {
                MissingDirectoryCannotBeWatched();
                DisposedWatcherRefusesStart();
                ChangesRaiseOneDebouncedEvent();
                BurstsCollapseIntoOneEvent();
                HiddenFlagChangesAreReported();
                StoppedWatcherGoesQuiet();
                RestartFollowsTheNewFolder();
            }
            finally
            {
                TryDelete(_root);
            }

            Console.WriteLine();
            if (Errors.Count == 0)
            {
                Console.WriteLine("ALL FOLDER-WATCH CHECKS PASSED");
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

        private static int EventCount()
        {
            return Volatile.Read(ref _events);
        }

        private static void ResetEvents()
        {
            Interlocked.Exchange(ref _events, 0);
        }

        /// <summary>Waits for a condition instead of guessing a fixed delay.</summary>
        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }

        private static void MissingDirectoryCannotBeWatched()
        {
            using (var watcher = new FolderWatcher(100))
            {
                Check(!watcher.Start(Path.Combine(_root, "does-not-exist")),
                    "missing directory: Start returns false");
                Check(!watcher.IsWatching, "missing directory: IsWatching is false");
                Check(!watcher.Start(null) && !watcher.Start("   "),
                    "blank path: Start returns false");
            }
        }

        private static void DisposedWatcherRefusesStart()
        {
            var watcher = new FolderWatcher(100);
            watcher.Dispose();

            Check(!watcher.Start(NewDir("case-disposed")), "dispose: Start after Dispose returns false");
        }

        private static void ChangesRaiseOneDebouncedEvent()
        {
            string dir = NewDir("case-create");
            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);

                Check(watcher.Start(dir), "create: Start returns true for a real folder");
                Check(watcher.IsWatching, "create: IsWatching is true");
                Check(watcher.Path == dir, "create: Path reports the watched folder");

                ResetEvents();
                File.WriteAllText(Path.Combine(dir, "new.txt"), "");

                Check(WaitFor(() => EventCount() >= 1, 3000), "create: a new file raises Changed");
                Thread.Sleep(300);
                Check(EventCount() == 1, "create: the burst stays one event (got " + EventCount() + ")");
            }
        }

        private static void BurstsCollapseIntoOneEvent()
        {
            string dir = NewDir("case-burst");
            using (var watcher = new FolderWatcher(150))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(dir);

                ResetEvents();
                for (int i = 0; i < 5; i++)
                {
                    File.WriteAllText(Path.Combine(dir, "burst" + i + ".txt"), "");
                }

                Check(WaitFor(() => EventCount() >= 1, 3000), "burst: five files raise a Changed");
                Thread.Sleep(400);
                Check(EventCount() == 1,
                    "burst: five quick files collapse into one event (got " + EventCount() + ")");
            }
        }

        private static void HiddenFlagChangesAreReported()
        {
            string dir = NewDir("case-attributes");
            string file = Path.Combine(dir, "toggle.txt");
            File.WriteAllText(file, "");

            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(dir);

                ResetEvents();
                File.SetAttributes(file, FileAttributes.Hidden); // the listing filters this file

                Check(WaitFor(() => EventCount() >= 1, 3000),
                    "attributes: flipping the hidden flag raises Changed");
            }
        }

        private static void StoppedWatcherGoesQuiet()
        {
            string dir = NewDir("case-stop");
            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(dir);

                ResetEvents();
                File.WriteAllText(Path.Combine(dir, "before.txt"), "");
                Check(WaitFor(() => EventCount() >= 1, 3000), "stop: watching works before Stop");

                watcher.Stop();
                Check(!watcher.IsWatching, "stop: IsWatching is false after Stop");

                ResetEvents();
                File.WriteAllText(Path.Combine(dir, "after.txt"), "");
                Thread.Sleep(700);
                Check(EventCount() == 0, "stop: no events after Stop (got " + EventCount() + ")");
            }
        }

        private static void RestartFollowsTheNewFolder()
        {
            string first = NewDir("case-restart-a");
            string second = NewDir("case-restart-b");

            using (var watcher = new FolderWatcher(100))
            {
                watcher.Changed += () => Interlocked.Increment(ref _events);
                watcher.Start(first);

                ResetEvents();
                File.WriteAllText(Path.Combine(first, "a.txt"), "");
                Check(WaitFor(() => EventCount() >= 1, 3000), "restart: the first folder is followed");

                Check(watcher.Start(second), "restart: Start on the second folder returns true");
                Check(watcher.Path == second, "restart: Path follows the new folder");

                ResetEvents();
                File.WriteAllText(Path.Combine(first, "ignored.txt"), "");
                Thread.Sleep(500);
                Check(EventCount() == 0, "restart: the old folder is no longer followed");

                File.WriteAllText(Path.Combine(second, "b.txt"), "");
                Check(WaitFor(() => EventCount() >= 1, 3000), "restart: the new folder raises events");
            }
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

- [ ] **Step 4: 写最小桩实现（能编译、必然失败）**

`Core/FolderWatcher.cs`（桩：`Start` 恒 false、事件永不触发）：

```csharp
using System;

namespace Kobold.Core
{
    /// <summary>Stub: the real watcher lands in the next step.</summary>
    public sealed class FolderWatcher : IDisposable
    {
        public event Action Changed;

        public string Path { get { return null; } }
        public bool IsWatching { get { return false; } }

        public bool Start(string path) { return false; }
        public void Stop() { }
        public void Dispose() { }

        // Keeps the event "used" so the stub builds warning-free (CS0067).
        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
```

- [ ] **Step 5: 运行自检，确认失败**

Run: `dotnet run --project tests/FolderWatchCheck`
Expected: 退出码 1；`FAILURES (n)` 且 n ≥ 10；所有失败都是"Start 应当返回 true / 应当收到事件"类，**没有未捕获异常、没有卡死**（每个等待都有 3s 超时）；`missing directory` / `dispose` / `stop 后安静` 这类用例应已通过。

- [ ] **Step 6: 实现真实逻辑**

`Core/FolderWatcher.cs`（整体替换）：

```csharp
using System;
using System.IO;
using System.Threading;

namespace Kobold.Core
{
    /// <summary>
    /// Follows one directory and reports changes through a debounced Changed
    /// event. Core-only (no UI, no WPF): the owner marshals to its own thread and
    /// decides what a change means. Some network shares refuse a watcher - Start
    /// reports that instead of throwing, so the caller can keep its manual
    /// refresh.
    /// </summary>
    public sealed class FolderWatcher : IDisposable
    {
        private readonly object _gate = new object();
        private readonly int _debounceMs;
        private readonly Timer _debounce;

        private FileSystemWatcher _watcher;
        private string _path;
        private bool _disposed;

        public FolderWatcher(int debounceMs = WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS)
        {
            _debounceMs = Math.Max(50, debounceMs);
            _debounce = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Raised once per burst of changes, on a pool thread.</summary>
        public event Action Changed;

        /// <summary>The folder currently being watched, or null.</summary>
        public string Path
        {
            get { lock (_gate) { return _path; } }
        }

        public bool IsWatching
        {
            get { lock (_gate) { return _watcher != null; } }
        }

        /// <summary>
        /// Watches the directory; false when it cannot be watched (missing path,
        /// or the filesystem refuses a watcher). Watching the same folder twice
        /// is a no-op that returns true.
        /// </summary>
        public bool Start(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            lock (_gate)
            {
                if (_disposed) return false;
                if (_watcher != null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                StopLocked();
                return StartLocked(path);
            }
        }

        public void Stop()
        {
            lock (_gate) { StopLocked(); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                StopLocked();
            }
            _debounce.Dispose();
        }

        private bool StartLocked(string path)
        {
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    // Only what the listing shows: names and the hidden/system
                    // flags. Content writes and timestamps must not refresh.
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.Attributes
                };

                watcher.Created += OnFolderEvent;
                watcher.Deleted += OnFolderEvent;
                watcher.Renamed += OnFolderEvent;
                watcher.Changed += OnFolderEvent;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;
                _path = path;
                return true;
            }
            catch (Exception)
            {
                // ArgumentException / FileNotFoundException / IOException / ...:
                // no watcher here - the caller keeps its manual refresh.
                StopLocked();
                return false;
            }
        }

        private void StopLocked()
        {
            _debounce.Change(Timeout.Infinite, Timeout.Infinite); // drop a pending tick

            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnFolderEvent;
                    _watcher.Deleted -= OnFolderEvent;
                    _watcher.Renamed -= OnFolderEvent;
                    _watcher.Changed -= OnFolderEvent;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                }
                catch (Exception)
                {
                    // The directory is already gone; nothing left to release.
                }
                _watcher = null;
            }

            _path = null;
        }

        private void OnFolderEvent(object sender, FileSystemEventArgs e)
        {
            Schedule();
        }

        private void Schedule()
        {
            lock (_gate)
            {
                if (_disposed || _watcher == null) return;
                _debounce.Change(_debounceMs, Timeout.Infinite);
            }
        }

        private void OnDebounceElapsed(object state)
        {
            bool watching;
            lock (_gate) { watching = !_disposed && _watcher != null; }

            if (watching) RaiseChanged();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // Buffer overflow or the folder itself vanished: report once so the
            // view shows the final state, then try to keep following the folder
            // (a deleted folder simply fails to restart).
            lock (_gate)
            {
                if (_disposed || _watcher == null) return;

                string path = _path;
                StopLocked();
                StartLocked(path);
            }

            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
```

- [ ] **Step 7: 构建 + 运行自检，确认全绿**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 错误 0 警告
Run: `dotnet run --project tests/FolderWatchCheck` → Expected: `ALL FOLDER-WATCH CHECKS PASSED`，退出码 0
Run: `dotnet run --project tests/FolderListingCheck` → Expected: 全绿（回归）

- [ ] **Step 8: 提交（需用户许可）**

```bash
git add Core/FolderWatcher.cs Core/WidgetConstants.cs tests/FolderWatchCheck
git commit -m "feat(browse): add a debounced FolderWatcher with a console check"
```

---

### Task 3: 面板接线（`FolderWidget.Watch.cs`）+ README + 手动验证

> 执行状态（2026-09-23）：代码与 README 已完成；构建 0 警告 0 错误、6 项自检全绿。
> **手动验证（Step 5）待用户执行**；提交待用户许可。

**Files:**
- Create: `Controls/FolderWidget.Watch.cs`
- Modify: `Controls/FolderWidget.xaml.cs:120`（订阅）、`:133-137`（`Closing` 释放）、`:200-202`（`UpdateUI` 末尾 reconcile）
- Modify: `Controls/FolderWidget.Interactions.cs:47`（`ShowPanel` 启动）、`:72`（`HidePanel` 停止）
- Modify: `README.md:30-33`（en 功能项）、`:186-188`（zh 功能项）

**Interfaces:**
- Consumes: `FolderWatcher`、`WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS`（Task 2）、`FolderListing.SameEntries`（Task 1）、现有 `_browseListing` / `_isDraggingItem` / `_isLassoSelecting` / `_modalDepth` / `GetSelectedItems()` / `ItemsScroller` / `UpdateUI()`
- Produces: `private void UpdateBrowseWatch()`、`private void OnBrowseFolderChanged()`、`private void RefreshBrowseIfChanged()`、`private void RetryBrowseRefresh()`

- [ ] **Step 1: 新建监听 partial**

`Controls/FolderWidget.Watch.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Kobold.Core;

namespace Kobold.Controls
{
    /// <summary>
    /// FolderWidget - live refresh of the browsed folder. While the panel is open
    /// on a folder a FolderWatcher follows it; changes are debounced, then the
    /// listing is re-read and re-rendered only when it really differs, so scroll
    /// position and surviving selections are kept.
    /// </summary>
    public partial class FolderWidget
    {
        private readonly FolderWatcher _browseWatcher =
            new FolderWatcher(WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS);

        private DispatcherTimer _browseRetryTimer;

        /// <summary>
        /// Brings the watcher in line with the panel: it follows the folder on
        /// screen exactly while the panel is expanded and browsing it.
        /// </summary>
        private void UpdateBrowseWatch()
        {
            string path = _isExpanded && IsBrowsing ? CurrentBrowsePath : null;
            if (path != null && !Directory.Exists(path)) path = null;

            if (path == null) _browseWatcher.Stop();
            else _browseWatcher.Start(path);
        }

        /// <summary>The watcher reports on a pool thread; the panel lives on the UI thread.</summary>
        private void OnBrowseFolderChanged()
        {
            Dispatcher.BeginInvoke(new Action(RefreshBrowseIfChanged));
        }

        private void RefreshBrowseIfChanged()
        {
            if (!_isExpanded || !IsBrowsing) return;

            // Never re-render under an active gesture or dialog: retry shortly.
            if (_isDraggingItem || _isLassoSelecting || _modalDepth > 0)
            {
                RetryBrowseRefresh();
                return;
            }

            string path = CurrentBrowsePath;
            if (path == null || !Directory.Exists(path))
            {
                // The folder itself is gone: show the "cannot open" state, then
                // stop following it (UpdateBrowseWatch disposes the watcher).
                UpdateUI();
                UpdateBrowseWatch();
                return;
            }

            var fresh = FolderListing.ListChildren(path, WidgetConstants.MAX_BROWSE_ENTRIES);
            if (FolderListing.SameEntries(_browseListing, fresh)) return;

            var selected = new HashSet<string>(
                GetSelectedItems().Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            double scrollOffset = ItemsScroller.VerticalOffset;

            UpdateUI();

            if (selected.Count > 0 && ItemsContainer.ItemsSource is List<DisplayItem> items)
            {
                foreach (var item in items)
                {
                    if (selected.Contains(item.Path)) item.IsSelected = true;
                }
            }

            ItemsScroller.ScrollToVerticalOffset(scrollOffset);
        }

        /// <summary>Retries the refresh while the user is mid-gesture.</summary>
        private void RetryBrowseRefresh()
        {
            if (_browseRetryTimer == null)
            {
                _browseRetryTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS)
                };
                _browseRetryTimer.Tick += (s, e) =>
                {
                    _browseRetryTimer.Stop();
                    RefreshBrowseIfChanged();
                };
            }

            _browseRetryTimer.Stop();
            _browseRetryTimer.Start();
        }
    }
}
```

- [ ] **Step 2: 订阅与释放（`FolderWidget.xaml.cs`）**

1. 构造函数里，`ThemeManager.ThemeChanged += OnThemeChanged;`（第 120 行）之后加：

```csharp
            // Folder changes on disk refresh the browse view (see Watch.cs).
            _browseWatcher.Changed += OnBrowseFolderChanged;
```

2. `Closing` 处理块（第 133-137 行）里，`ThemeManager.ThemeChanged -= OnThemeChanged;` 之后加：

```csharp
                _browseWatcher.Dispose();
```

3. `UpdateUI()` 末尾，`Dispatcher.BeginInvoke(new Action(() => ApplyItemTextColors()), ...)` 之后加：

```csharp
            // Enter/leave folders and theme changes all land here; keep the
            // watcher following the folder that is on screen now.
            UpdateBrowseWatch();
```

- [ ] **Step 3: 面板开合时启停（`FolderWidget.Interactions.cs`）**

1. `ShowPanel` 里 `_isExpanded = true;`（第 47 行）之后加：

```csharp
            UpdateBrowseWatch(); // the panel is open now: follow the folder on screen
```

2. `HidePanel` 里 `_isExpanded = false;`（第 72 行）之后加：

```csharp
            UpdateBrowseWatch(); // closing: stop following the folder
```

- [ ] **Step 4: 构建 + 全部相关自检**

Run: `dotnet build Kobold.csproj -c Debug` → Expected: 0 错误 0 警告
Run（逐个，全部应为退出码 0）:
`dotnet run --project tests/FolderListingCheck`、`dotnet run --project tests/FolderWatchCheck`、
`dotnet run --project tests/LangCheck`、`dotnet run --project tests/XamlLoadCheck`、
`dotnet run --project tests/UiTokensCheck`、`dotnet run --project tests/WidgetItemsCheck`

- [ ] **Step 5: 手动验证（需要用户在真实桌面上执行）**

Run: `bin\Debug\net48\Kobold.exe`

1. 悬停岛 → 点开一个组件面板 → 双击一个文件夹条目进入浏览态。
2. 在资源管理器里向该文件夹新建一个文件 → 面板约 0.5s 内出现该条目（不点任何按钮）。
3. 在资源管理器里删除它 → 面板里消失；改名 → 面板里名字跟着变。
4. 切换某个文件的"隐藏"属性 → 面板里相应出现/消失。
5. 滚动到长列表中部，再在资源管理器新建文件 → 刷新后滚动位置基本不变。
6. 选中一个条目，再新建文件 → 该条目保持选中。
7. 按住面板里的条目开始拖动（不松手），同时在资源管理器新建文件 → 拖动过程中面板不重绘、指示线正常；松手后变化出现。
8. 在资源管理器里删除正在浏览的文件夹 → 面板显示"无法访问此文件夹"；点返回箭头可退回上一级且恢复正常。
9. 回归：F5 刷新、Backspace 返回、右键菜单、根态拖拽/框选、钉住面板、隐藏再打开面板——行为与之前一致。
10. 组件不在浏览态（根视图）时无监听：改存储目录内容不会自动刷新（符合范围约定）。

- [ ] **Step 6: README 功能表**

`README.md` 英文功能项（第 30-33 行）改为：

```markdown
- **Browse folders in place** — double-click a folder entry to list its
  contents inside the panel: drill in, go back (button or Backspace), open
  files with the shell. While the panel is open the listing follows the
  folder live, and the panel remembers the folder you left it in, across
  hides and restarts.
```

中文功能项（第 186-188 行）改为：

```markdown
- **面板内浏览文件夹** — 双击组件里的文件夹条目即可在面板内就地查看其内容：
  逐级下钻、返回（按钮或 Backspace）、文件交给系统默认程序打开。
  面板打开时列表会实时跟随该文件夹的变化；面板会记住你离开时所在的文件夹，
  收起再打开、重启之后都还在。
```

- [ ] **Step 7: 提交（需用户许可）**

```bash
git add Controls/FolderWidget.Watch.cs Controls/FolderWidget.xaml.cs Controls/FolderWidget.Interactions.cs README.md
git commit -m "feat(browse): live-refresh the browsed folder while the panel is open"
```

---

## 自审记录（写计划时已核对）

- **Spec 覆盖**：仅浏览视图（Task 3 的 `UpdateBrowseWatch` 条件）、防抖（Task 2）、文件夹消失 → 空态 + 停止监听（Task 3 `RefreshBrowseIfChanged`）、不打断手势（Task 3 retry）、滚动/选中保持（Task 3）、根视图不动（无对应改动）。
- **类型一致性**：`FolderWatcher.Start/Stop/IsWatching/Path/Changed`、`FolderListing.SameEntries(ListingResult, ListingResult)`、`WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS` 在 Task 1/2/3 之间名字一致。
- **无占位符**：每个代码步骤都给了可直接粘贴的完整代码。
- **未覆盖但有意为之**：子目录递归监听、轮询兜底、根视图刷新、`LangCheck` 新增文案（本功能不新增 UI 文案）。
