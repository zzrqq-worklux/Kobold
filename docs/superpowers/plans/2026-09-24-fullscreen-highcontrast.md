# Kobold 全屏避让与高对比度（Fullscreen & High Contrast）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 岛屿在全屏应用（游戏/视频）前面不再悬浮；系统开启高对比度或切换系统偏好时，主题立即以系统色回退并刷新，无需重启。

**Architecture:** 新增纯策略 `Core/FullscreenPolicy.cs` + WinEvent 观察者 `Services/FullscreenWatcher.cs`（前台变化时在 Dispatcher 去抖评估，命中全屏的外部窗口就把岛降为非 Topmost，退出全屏恢复）。新增纯映射 `Core/HighContrastPalette.cs`；`ThemeManager` 在高对比度下用系统色覆盖语义画刷，并订阅 `SystemEvents.UserPreferenceChanged` 实时刷新。

**Tech Stack:** net48 / C# 7.3 / WPF / user32（SetWinEventHook、DwmGetWindowAttribute）/ Microsoft.Win32.SystemEvents。

**Spec:** `docs/paper-todo-learnings.md` 第 6.2、6.4 节（PaperTodo 侧参考：`src/FullscreenForegroundWindowDetector.cs`、`src/FullscreenForegroundWindowWatcher.cs`、`src/Theme.cs:132-144`、`src/AppController.cs:219-223`）。

## Global Constraints

- net48 / C# 7.3；无新 XAML、无十六进制颜色字面量（`UiTokensCheck` 守护：系统色是运行时值，不落入静态扫描）。
- `SetWinEventHook` 回调线程不是 UI 线程：所有 UI 变更必须 marshal 回 `Dispatcher`。
- 观察者必须在退出时 `UnhookWinEvent`；`ThemeManager` 的系统偏好订阅在 `App.OnExit` 退订。
- 全屏避让的失败方向：**宁可保持现状**（不做猜测性隐藏/降级）；只处理"外部进程前台窗口覆盖其所在监视器"这一确定情形。
- 相关新自检追加进 `tools/Run-Checks.ps1`（`Ci = $true`）。

## File Structure

| 文件 | 责任 | 动作 |
| --- | --- | --- |
| `Core/FullscreenPolicy.cs` | 纯判定：窗口是否覆盖监视器 / 是否视为全屏 | 新建 |
| `tests/FullscreenPolicyCheck/*` | 策略自检 | 新建 |
| `Services/FullscreenWatcher.cs` | WinEvent 观察 + 去抖 + 判定 + 回调 | 新建 |
| `Controls/IslandWindow.xaml.cs` | `SetTopmost(bool)` | 修改 |
| `Core/WidgetManager.cs` | 创建/释放 watcher，回调驱动岛 | 修改 |
| `Core/HighContrastPalette.cs` | 系统色 → 语义画刷键的纯映射 | 新建 |
| `tests/HighContrastCheck/*` | 映射自检 | 新建 |
| `Core/ThemeManager.cs` | 高对比度覆盖 + `UserPreferenceChanged` 刷新 | 修改 |
| `App.xaml.cs` | 退出时退订系统偏好 | 修改 |
| `tools/Run-Checks.ps1` | 矩阵追加两个自检 | 修改 |

---

### Task 1: `Core/FullscreenPolicy.cs`（纯策略）+ 自检

**Files:**
- Create: `Core/FullscreenPolicy.cs`
- Test: `tests/FullscreenPolicyCheck/FullscreenPolicyCheck.csproj`、`Program.cs`

**Interfaces:**
- Produces:
  - `public static bool CoversMonitor(Rect windowDevice, Rect monitorDevice, double tolerancePx = 2)`
  - `public static bool IsShellWindow(string className)`
  - `public static bool ShouldAvoidTopmost(bool isOwnProcess, bool isVisible, bool isMinimized, string className, Rect windowDevice, Rect monitorDevice)`

- [ ] **Step 1: 写失败的自检**

```csharp
var monitor = new Rect(0, 0, 1920, 1080);

Check(FullscreenPolicy.CoversMonitor(monitor, monitor), "exact cover");
Check(FullscreenPolicy.CoversMonitor(new Rect(-1, -1, 1922, 1082), monitor), "slightly larger cover");
Check(FullscreenPolicy.CoversMonitor(new Rect(2, 2, 1916, 1076), monitor), "2px tolerance cover");
Check(!FullscreenPolicy.CoversMonitor(new Rect(0, 0, 1920, 1032), monitor), "taskbar-height window is not fullscreen");
Check(!FullscreenPolicy.CoversMonitor(new Rect(100, 100, 800, 600), monitor), "a normal window is not fullscreen");

Check(FullscreenPolicy.IsShellWindow("Progman") && FullscreenPolicy.IsShellWindow("WorkerW") &&
      FullscreenPolicy.IsShellWindow("Shell_TrayWnd"), "shell host windows are recognised");
Check(!FullscreenPolicy.IsShellWindow("Chrome_WidgetWin_1"), "a browser window is not a shell window");

Check(!FullscreenPolicy.ShouldAvoidTopmost(true, true, false, "Chrome_WidgetWin_1", monitor, monitor),
    "our own fullscreen window does not push the island down");
Check(!FullscreenPolicy.ShouldAvoidTopmost(false, true, false, "Progman", monitor, monitor),
    "the shell desktop does not count as fullscreen");
Check(!FullscreenPolicy.ShouldAvoidTopmost(false, false, false, "Game", monitor, monitor),
    "invisible windows do not count");
Check(!FullscreenPolicy.ShouldAvoidTopmost(false, true, true, "Game", monitor, monitor),
    "minimised windows do not count");
Check(!FullscreenPolicy.ShouldAvoidTopmost(false, true, false, "Game", new Rect(0, 0, 1280, 720), monitor),
    "a 720p window on a 1080p monitor does not count");
Check(FullscreenPolicy.ShouldAvoidTopmost(false, true, false, "Game", monitor, monitor),
    "an external fullscreen window counts");
```

- [ ] **Step 2: 运行并确认失败** → `dotnet run --project tests/FullscreenPolicyCheck` 编译失败。

- [ ] **Step 3: 实现**

```csharp
using System;
using System.Windows;

namespace Kobold.Core
{
    /// <summary>
    /// Decides when the always-on-top island should step aside for a fullscreen
    /// foreground window. Pure so the rules are testable without real windows.
    /// </summary>
    public static class FullscreenPolicy
    {
        public const double TolerancePx = 2;

        public static bool CoversMonitor(Rect windowDevice, Rect monitorDevice, double tolerancePx = TolerancePx)
        {
            return windowDevice.Left  <= monitorDevice.Left + tolerancePx
                && windowDevice.Top   <= monitorDevice.Top + tolerancePx
                && windowDevice.Right >= monitorDevice.Right - tolerancePx
                && windowDevice.Bottom >= monitorDevice.Bottom - tolerancePx;
        }

        public static bool IsShellWindow(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            return className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd";
        }

        public static bool ShouldAvoidTopmost(bool isOwnProcess, bool isVisible, bool isMinimized,
            string className, Rect windowDevice, Rect monitorDevice)
        {
            if (isOwnProcess || !isVisible || isMinimized) return false;
            if (IsShellWindow(className)) return false;
            return CoversMonitor(windowDevice, monitorDevice);
        }
    }
}
```

- [ ] **Step 4: 运行并确认通过** → 全 PASS。

---

### Task 2: `Services/FullscreenWatcher.cs` + 岛接线

**Files:**
- Create: `Services/FullscreenWatcher.cs`
- Modify: `Controls/IslandWindow.xaml.cs`（`SetTopmost`）
- Modify: `Core/WidgetManager.cs`（Initialize 创建、Shutdown 释放）

**Interfaces:**
- Produces:
  - `public sealed class FullscreenWatcher : IDisposable { public FullscreenWatcher(Action<bool> onAvoidChanged); public void Start(); public void Dispose(); }`
  - 回调参数 `avoid=true` 表示"当前有外部全屏，岛应降级"；只在状态变化时触发，且在 UI Dispatcher 上触发。
  - `IslandWindow.SetTopmost(bool topmost)`。

- [ ] **Step 1: 实现 watcher**

要点（全部必须）：

- 钩子：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)`。
- 回调：不做事，只 `_dispatcher.BeginInvoke` 重启一个 300ms `DispatcherTimer`（去抖）。
- 评估：

```csharp
IntPtr fg = GetForegroundWindow();
bool avoid = false;
if (fg != IntPtr.Zero)
{
    GetWindowThreadProcessId(fg, out uint pid);
    bool own = pid == (uint)Process.GetCurrentProcess().Id;
    bool visible = IsWindowVisible(fg);
    bool minimized = IsIconic(fg);
    string cls = GetClassNameSafe(fg);
    Rect windowDevice = GetExtendedFrameBoundsOrWindowRect(fg);
    var monitor = MonitorInterop.GetMonitorForWindow(fg);
    if (monitor != null)
        avoid = FullscreenPolicy.ShouldAvoidTopmost(own, visible, minimized, cls,
            windowDevice, monitor.BoundsDevice);
}
if (avoid != _lastAvoid) { _lastAvoid = avoid; _onAvoidChanged(avoid); }
```

- 窗口矩形优先 `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS = 9, ...)`，失败回退 `GetWindowRect`。
- `Start()` 保存钩子句柄；`Dispose()` `UnhookWinEvent` + 停表 + 幂等。全部互操作异常捕获并 `Debug.WriteLine`（失败方向 = 保持现状）。
- 类名用 `GetClassName`（`StringBuilder(256)`）。

- [ ] **Step 2: 接线**

- `IslandWindow`：

```csharp
/// <summary>Steps the island out of the way of a fullscreen app (and back).</summary>
public void SetTopmost(bool topmost)
{
    if (Topmost == topmost) return;
    Topmost = topmost;
}
```

- `WidgetManager`：字段 `private FullscreenWatcher _fullscreenWatcher;`
  - `Initialize()` 末尾（`_island.Show()` 之后）：

```csharp
// Keep the always-on-top island out of the way of fullscreen games/videos.
_fullscreenWatcher = new FullscreenWatcher(avoid => _island?.SetTopmost(!avoid));
_fullscreenWatcher.Start();
```

  - `Shutdown()` 开头：`_fullscreenWatcher?.Dispose(); _fullscreenWatcher = null;`

- [ ] **Step 3: 构建 + 手动验证**

Run: `dotnet build Kobold.csproj -c Debug` → 0 警告 0 错误。
手动（必须）：启动 Kobold → 用浏览器 F11 全屏 → 岛应被压在浏览器后面（移到屏幕顶部看是否被遮挡）；退出 F11 → 岛恢复置顶。视频全屏窗口同理。

> 自动化边界：WinEvent 观察需要真实前台窗口，无法在自检里确定性复现；策略层已由 Task 1 全覆盖，观察者由手动验证兜底。

---

### Task 3: `Core/HighContrastPalette.cs`（纯映射）+ 自检

**Files:**
- Create: `Core/HighContrastPalette.cs`
- Test: `tests/HighContrastCheck/HighContrastCheck.csproj`、`Program.cs`

**Interfaces:**
- Produces: `public static Dictionary<string, string> Build(string window, string windowText, string highlight, string highlightText, string grayText, string controlDark, string controlText)`
  - 键 = `ThemeManager.Themed` 使用的画刷键（如 `"TextPrimary"`、`"IslandHover"`）；值 = `#RRGGBB`。

- [ ] **Step 1: 写失败的自检**

```csharp
string W = "#101010", WT = "#F0F0F0", H = "#FFD700", HT = "#000000", G = "#808080", CD = "#404040", CT = "#E0E0E0";
var map = HighContrastPalette.Build(W, WT, H, HT, G, CD, CT);

Check(map["Background"] == W && map["Surface"] == W && map["IslandBackground"] == W,
    "window surfaces use the system window color");
Check(map["TextPrimary"] == WT && map["IslandForeground"] == WT && map["OverlayText"] == WT,
    "primary text uses the system window text color");
Check(map["TextSecondary"] == WT && map["TextMuted"] == G && map["OverlayMuted"] == G,
    "secondary/muted text maps to system text/gray");
Check(map["Border"] == CD && map["ControlBorder"] == CD && map["TooltipBorder"] == CD,
    "borders use the system control dark color");
Check(map["SurfaceHover"] == H && map["OverlayHover"] == H && map["IslandHover"] == H &&
      map["SelectionBorder"] == H && map["DropIndicator"] == H,
    "hover/selection states use the system highlight color");
Check(map["ScrollThumbHover"] == CT && map["SliderThumb"] == CT && map["ControlBorderHover"] == CT,
    "active states use the system control text color");
Check(map.Count >= 35, "covers the full semantic brush set");
Check(map.Values.All(v => v.Length == 7 && v[0] == '#'), "all values are #RRGGBB");
```

- [ ] **Step 2: 运行并确认失败** → 编译失败。

- [ ] **Step 3: 实现**

按上表逐键实现（`Build` 内用一个 `Dictionary<string,string>` 初始化器；所有系统色直接透传）。映射清单（37 键）：
`Background=window, Surface=window, SurfaceHover=highlight, Border=controlDark, TextPrimary=windowText, TextSecondary=windowText, TextMuted=grayText, TextFaint=grayText, ControlBackground=window, ControlBorder=controlDark, ControlBorderHover=controlText, ButtonSecondary=window, ButtonSecondaryHover=highlight, IconFill=windowText, IconStroke=controlDark, MissingBadge=grayText, PanelBorder=controlDark, DropIndicator=highlight, SelectionBorder=highlight, LassoFill=highlight, LassoStroke=highlight, ScrollThumb=controlDark, ScrollThumbHover=controlText, SliderTrack=controlDark, SliderThumb=controlText, IslandBackground=window, IslandBackgroundExpanded=window, IslandBorder=controlDark, IslandHover=highlight, IslandDivider=controlDark, IslandForeground=windowText, DragGhost=window, OverlayBackground=window, OverlayBorder=controlDark, OverlayHover=highlight, OverlayChecked=highlight, OverlayText=windowText, OverlayMuted=grayText, OverlayDivider=controlDark, TooltipBackground=window, TooltipBorder=controlDark`。

- [ ] **Step 4: 运行并确认通过** → 全 PASS。

> 注：Task 3 的接口层不含 `ThemeManager`（Task 4 接入），因此本任务可独立验证。

---

### Task 4: `ThemeManager` 高对比度回退 + 系统偏好订阅

**Files:**
- Modify: `Core/ThemeManager.cs`
- Modify: `App.xaml.cs`（退出退订）

**Interfaces:**
- Produces:
  - `public static bool IsHighContrast { get; private set; }`
  - `ThemeManager.Initialize/SetTheme` 后：高对比度开启时全部语义画刷来自 `HighContrastPalette.Build(...)`（用 `SystemColors`）；否则行为不变。
  - `ThemeManager.StopSystemPreferenceWatch()`（`App.OnExit` 调用）。

- [ ] **Step 1: 实现**

- 私有状态：`private static readonly Dictionary<string,string> _hcOverride = ...;`（在 `UpdateBrushes` 开头重建）。
- `UpdateBrushes()` 开头：

```csharp
IsHighContrast = SystemParameters.HighContrast;
_hcOverride.Clear();
if (IsHighContrast)
{
    foreach (var pair in HighContrastPalette.Build(
        Utils.ColorToHex(SystemColors.WindowColor),
        Utils.ColorToHex(SystemColors.WindowTextColor),
        Utils.ColorToHex(SystemColors.HighlightColor),
        Utils.ColorToHex(SystemColors.HighlightTextColor),
        Utils.ColorToHex(SystemColors.GrayTextColor),
        Utils.ColorToHex(SystemColors.ControlDarkColor),
        Utils.ColorToHex(SystemColors.ControlTextColor)))
        _hcOverride[pair.Key] = pair.Value;
}
```

- `Themed(key, darkHex, lightHex)`：

```csharp
string hex = _hcOverride.Count > 0 && _hcOverride.TryGetValue(key, out var hc) ? hc
    : (_isDarkTheme ? darkHex : lightHex);
```

- 品牌/强调色（`InstallOverlayResources`）：高对比度时 `Kobold.Brush.Accent` 用 `SystemColors.HighlightColor` 的冻结画刷、`OnAccent` 用 `HighlightTextColor`（否则保持现状）。
- 系统偏好订阅：

```csharp
private static bool _watchingPreferences;

public static void StartSystemPreferenceWatch()
{
    if (_watchingPreferences) return;
    _watchingPreferences = true;
    Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
}

public static void StopSystemPreferenceWatch()
{
    if (!_watchingPreferences) return;
    _watchingPreferences = false;
    try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
}

private static void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
{
    // Delivered on a system thread; only the dispatcher may touch brushes/resources.
    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
    {
        bool hc = SystemParameters.HighContrast;
        if (hc == IsHighContrast) return; // 非对比度变化不重建（主题仍由用户设置决定）
        IsHighContrast = hc;
        UpdateBrushes();
        ThemeChanged?.Invoke();
    }));
}
```

- `Initialize()` 中调用 `StartSystemPreferenceWatch()`。
- `App.OnExit`（primary 分支内）调用 `ThemeManager.StopSystemPreferenceWatch();`（包 try/catch）。

- [ ] **Step 2: 验证**

Run: `dotnet build Kobold.csproj -c Debug` → 0 警告 0 错误。
Run: `dotnet run --project tests/HighContrastCheck` → PASS（Task 3 用例）。
Run: `dotnet run --project tests/UiTokensCheck` → PASS（无新增颜色字面量）。
Run: `dotnet run --project tests/XamlLoadCheck` → PASS（XAML 资源仍完整）。
手动（建议）：系统设置 → 辅助功能 → 对比度主题切换 "None → 高对比黑色" → 岛/面板/菜单应立即换色刷新。

---

### Task 5: 接入 Run-Checks 矩阵 + 全量回归

- [ ] **Step 1:** `tools/Run-Checks.ps1` 矩阵追加 `FullscreenPolicyCheck`、`HighContrastCheck`（均 `Ci = $true`）。
- [ ] **Step 2:** `powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all`
  Expected: 全绿。
- [ ] **Step 3:** 手动验收清单（记录到执行记录）：
  1. F11 全屏浏览器：岛避让；退出后恢复。
  2. 高对比度主题切换：立即生效。
  3. 单屏回归：岛居中、面板恢复、托盘菜单齐全。

---

## Self-Review

- **Spec 覆盖**：全屏判定=Task 1；观察者与接线=Task 2；高对比度映射=Task 3；主题接入=Task 4；回归=Task 5；明确不做的重型设施（会话跟踪、全局扫描预算、cloak 事务、多皮肤回退）未引入。
- **风险**：`SetWinEventHook` 在无桌面会话时返回 0 → watcher 静默不工作（符合"失败保持现状"）；`ThemeChanged` 订阅方已按 Dispatcher 处理（既有代码 `Dispatcher.Invoke`）。
- **回滚**：新文件为主，`ThemeManager` 改动集中在 `Themed`/`UpdateBrushes`。

---

## 执行记录（2026-09-24）

**状态：Task 1-5 全部完成；自动化全绿；手动验收受环境限制，见下。**

- Task 1：`Core/FullscreenPolicy.cs` + `tests/FullscreenPolicyCheck`（13 例，先红后绿）。
- Task 2：`Services/FullscreenWatcher.cs`（`SetWinEventHook` 前台钩子 + 300ms 去抖 + `DwmGetWindowAttribute` 失败回退 `GetWindowRect`；`Dispose` 幂等，另有 `_disposed` 守卫防止排队中的评估在释放后回调）；`IslandWindow.SetTopmost(bool)`；`WidgetManager.Initialize` 创建 / `Shutdown` 释放。
- Task 3：`Core/HighContrastPalette.cs` + `tests/HighContrastCheck`（41 键映射，先红后绿）。
- Task 4：`ThemeManager.IsHighContrast` + `_hcOverride`；`Themed` 优先取系统色；`Accent`/`OnAccent` 高对比度时用 `SystemColors.HighlightColor`/`HighlightTextColor`；`Initialize` 订阅 `UserPreferenceChanged`，`App.OnExit`（primary 分支）退订。
- Task 5：`tools/Run-Checks.ps1` 矩阵加入 `FullscreenPolicyCheck`、`HighContrastCheck`（均 Ci=$true）；`-Group all` 24 项全绿（含 installer 60 例）。
- **额外自动化冒烟**（临时工程，位于 %TEMP%，未入库）：双进程真机验证 watcher——外部无边框窗口按物理边界覆盖所在监视器并取前台后，回调 `avoid=True`；窗口关闭后回调 `avoid=False`。
- **未验证（环境限制，如实声明）**：
  - F11 浏览器全屏避让人工步骤：不便驱动交互式浏览器全屏，未执行；判定策略由 `FullscreenPolicyCheck` 全覆盖，观察者已由上述双进程冒烟覆盖。
  - 高对比度主题切换人工步骤：为避免改动用户会话的系统辅助功能设置未执行；映射由 `HighContrastCheck` 覆盖，`ThemeManager` 接入经构建 + `XamlLoadCheck` 验证。
  - 新 PS 脚本含中文须以 UTF-8 带 BOM 保存的注意事项：本计划未新增 PS 脚本，`Run-Checks.ps1` 追加行均为 ASCII。
