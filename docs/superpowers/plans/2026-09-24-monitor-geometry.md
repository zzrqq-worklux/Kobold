# Kobold 多屏几何与面板恢复（Monitor Geometry）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 岛屿与面板在多显示器 / 混合 DPI 下位置正确：按所在监视器工作区居中与夹取；面板恢复使用保存时的物理坐标 + DPI，先选目标屏再按目标屏换算回 DIP。

**Architecture:** 新增 `Core/MonitorInterop.cs`（Win32 监视器枚举 + 工作区 + DPI，带缓存与显示设置变化失效）与纯函数 `Core/PanelPlacement.cs`（DIP↔物理换算、选屏、夹取）。`FolderWidget` 拖动时记录 `PanelDpiScale`，`ShowPanel` 走"设备矩形 → 选屏 → 夹取 → 目标屏 DIP"链路（互操作失败回退现有虚拟屏夹取）。`IslandWindow` 居中/夹取改用所在监视器工作区；`WidgetManager.OpenPanel` 默认居中于岛。

**Tech Stack:** net48 / C# 7.3 / WPF PerMonitorV2 / user32 + shcore P/Invoke。

**Spec:** `docs/paper-todo-learnings.md` 第 6.3 节（PaperTodo 侧参考：`src/PaperRestoreGeometry.cs`、`src/WindowWorkAreaHelper.cs`）。

## Global Constraints

- net48 / C# 7.3；不动 `app.manifest` 的 PerMonitorV2 声明。
- 新增纯逻辑必须可在无真实多屏的机器上被 console 自检覆盖（构造虚拟 `MonitorInfo` 列表）。
- 互操作失败（DLL 缺失、句柄未就绪）必须回退到现有行为，不得抛到 UI。
- `SystemEvents.DisplaySettingsChanged` 订阅只在静态构造时建立一次；订阅为进程生命周期（单实例应用，可接受）。
- `IslandLayout` 的既有自检（`IslandLayoutCheck`）不得改动其数学；只改传入的屏幕宽度来源。

## File Structure

| 文件 | 责任 | 动作 |
| --- | --- | --- |
| `Core/MonitorInterop.cs` | 监视器枚举/工作区/DPI + 缓存失效 | 新建 |
| `Core/PanelPlacement.cs` | 纯换算与放置策略（无 Win32） | 新建 |
| `tests/MonitorPlacementCheck/*` | 放置策略自检 | 新建 |
| `Core/FolderData.cs` | 新增 `PanelDpiScale` | 修改 |
| `Controls/FolderWidget.Interactions.cs` | 拖动记录 DPI；恢复走放置策略 | 修改 |
| `Controls/IslandWindow.xaml.cs` | 工作区居中/夹取；暴露 `CenterX`/工作区宽 | 修改 |
| `Core/WidgetManager.cs` | OpenPanel 默认居中于岛 | 修改 |
| `tools/Run-Checks.ps1` | 矩阵追加 `MonitorPlacementCheck`（Ci=$true） | 修改（B 已存在） |

---

### Task 1: `Core/PanelPlacement.cs`（纯策略）+ 自检

**Files:**
- Create: `Core/PanelPlacement.cs`
- Test: `tests/MonitorPlacementCheck/MonitorPlacementCheck.csproj`、`Program.cs`

**Interfaces:**
- Produces:
  - `public static Rect ToDeviceRect(double dipX, double dipY, double dipWidth, double dipHeight, double savedScale, double fallbackScale)`
  - `public static Rect ClampToMonitors(Rect panelDevice, IList<MonitorInfo> monitors)`（先选屏再夹取）
  - `public static Rect ToDipRect(Rect deviceRect, MonitorInfo target)`
- `MonitorInfo` 由 Task 2 的 `MonitorInterop.cs` 提供；本任务先建一个最小定义（`BoundsDevice`、`WorkAreaDevice`、`DpiScale`、`IsPrimary`、`DeviceName`），Task 2 只扩展互操作。

- [ ] **Step 1: 写失败的自检**

用例（全部用虚拟监视器，不依赖真实屏幕）：

```csharp
// 虚构：主屏 1920x1080@1.0 在 (0,0)；副屏 2560x1440@1.5 在 (1920,0)，工作区上方留 100 任务栏
var primary = new MonitorInfo { DeviceName = @"\\.\DISPLAY1", IsPrimary = true,
    BoundsDevice = new Rect(0, 0, 1920, 1080), WorkAreaDevice = new Rect(0, 0, 1920, 1040), DpiScale = 1.0 };
var secondary = new MonitorInfo { DeviceName = @"\\.\DISPLAY2", IsPrimary = false,
    BoundsDevice = new Rect(1920, 0, 2560, 1440), WorkAreaDevice = new Rect(1920, 0, 2560, 1400), DpiScale = 1.5 };
var monitors = new List<MonitorInfo> { primary, secondary };

// 1. legacy（无保存 DPI）按回退 DPI 换算
var r = PanelPlacement.ToDeviceRect(100, 100, 300, 200, 0, 1.5);
Check(r == new Rect(150, 150, 450, 300), "legacy save falls back to the current DPI");

// 2. 保存过 DPI：按保存时 DPI 换算
r = PanelPlacement.ToDeviceRect(100, 100, 300, 200, 1.0, 1.5);
Check(r == new Rect(100, 100, 300, 200), "saved scale is used verbatim");

// 3. 面板在副屏内：选副屏、夹进副屏工作区
var panel = new Rect(2000, 1300, 400, 200); // 底边超出工作区
var clamped = PanelPlacement.ClampToMonitors(panel, monitors);
Check(clamped.X == 2000 && clamped.Bottom <= 1400, "clamps into the overlapping monitor work area");

// 4. 完全落在主屏的选主屏
Check(PanelPlacement.ClampToMonitors(new Rect(100, 100, 200, 200), monitors).X == 100,
    "monitor with the larger intersection wins");

// 5. 跨屏（大半在副屏）：选副屏
var across = new Rect(1800, 300, 600, 200); // 120 在主屏、480 在副屏
var acrossClamped = PanelPlacement.ClampToMonitors(across, monitors);
Check(acrossClamped.X >= 1920, "the monitor with the larger overlap wins for straddling panels");

// 6. 全部离屏：选最近的屏并夹进去
var offscreen = new Rect(9000, 9000, 300, 200);
var offClamped = PanelPlacement.ClampToMonitors(offscreen, monitors);
Check(offClamped.Right <= 1920 + 2560 && offClamped.Bottom <= 1400,
    "an off-screen panel is pulled into the nearest work area");

// 7. 面板比工作区还大：锚在工作区左上
var huge = new Rect(2500, 100, 2600, 2000);
var hugeClamped = PanelPlacement.ClampToMonitors(huge, monitors);
Check(hugeClamped.X == 1920 && hugeClamped.Y == 0, "an oversized panel anchors at the work-area origin");

// 8. 回换算：150% 屏上的 450 物理 = 300 DIP
Check(PanelPlacement.ToDipRect(new Rect(1950, 150, 450, 300), secondary) == new Rect(1300, 100, 300, 200),
    "device-to-DIP uses the target monitor scale");

// 9. 无监视器列表（互操作失败）：原样返回
Check(PanelPlacement.ClampToMonitors(new Rect(1, 2, 3, 4), new List<MonitorInfo>()) == new Rect(1, 2, 3, 4),
    "empty monitor list leaves the rect untouched");

// 10. 大 DPI 边界：savedScale<=0 且 fallbackScale<=0 时按 1.0 处理
Check(PanelPlacement.ToDeviceRect(10, 10, 10, 10, 0, 0) == new Rect(10, 10, 10, 10),
    "invalid scales fall back to 1.0");
```

- [ ] **Step 2: 运行并确认失败**

Run: `dotnet run --project tests/MonitorPlacementCheck`
Expected: 编译失败（类型不存在）→ 非 0。

- [ ] **Step 3: 实现**

```csharp
using System;
using System.Collections.Generic;
using System.Windows;

namespace Kobold.Core
{
    /// <summary>A monitor's bounds and work area in physical (device) pixels.</summary>
    public sealed class MonitorInfo
    {
        public string DeviceName { get; set; }
        public bool IsPrimary { get; set; }
        public Rect BoundsDevice { get; set; }
        public Rect WorkAreaDevice { get; set; }
        public double DpiScale { get; set; }
    }

    /// <summary>
    /// Pure placement maths for widget panels. Window coordinates are saved as
    /// DIPs plus the DPI scale they were captured at - restoring multiplies back
    /// to physical pixels, picks the monitor the panel belongs to, clamps it into
    /// that monitor's work area, and converts back to DIPs using the destination
    /// monitor's scale. All inputs are injectable so tests need no real screens.
    /// </summary>
    public static class PanelPlacement
    {
        public static Rect ToDeviceRect(double dipX, double dipY, double dipWidth, double dipHeight,
            double savedScale, double fallbackScale)
        {
            double scale = savedScale > 0 ? savedScale : (fallbackScale > 0 ? fallbackScale : 1.0);
            return new Rect(dipX * scale, dipY * scale, dipWidth * scale, dipHeight * scale);
        }

        public static Rect ClampToMonitors(Rect panelDevice, IList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return panelDevice;
            MonitorInfo target = FindBestMonitor(panelDevice, monitors);
            return ClampToWorkArea(panelDevice, target.WorkAreaDevice);
        }

        public static Rect ToDipRect(Rect deviceRect, MonitorInfo target)
        {
            double scale = target != null && target.DpiScale > 0 ? target.DpiScale : 1.0;
            return new Rect(deviceRect.X / scale, deviceRect.Y / scale,
                deviceRect.Width / scale, deviceRect.Height / scale);
        }

        private static MonitorInfo FindBestMonitor(Rect rect, IList<MonitorInfo> monitors)
        {
            MonitorInfo best = null;
            double bestArea = 0;
            foreach (var m in monitors)
            {
                double area = IntersectionArea(rect, m.BoundsDevice);
                if (area > bestArea) { bestArea = area; best = m; }
            }
            if (best != null) return best;

            // Off-screen: nearest monitor by centre distance.
            Point c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            double bestDistance = double.MaxValue;
            foreach (var m in monitors)
            {
                Point mc = new Point(m.BoundsDevice.X + m.BoundsDevice.Width / 2,
                    m.BoundsDevice.Y + m.BoundsDevice.Height / 2);
                double d = (mc.X - c.X) * (mc.X - c.X) + (mc.Y - c.Y) * (mc.Y - c.Y);
                if (d < bestDistance) { bestDistance = d; best = m; }
            }
            return best;
        }

        private static double IntersectionArea(Rect a, Rect b)
        {
            double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
            double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            return (w > 0 && h > 0) ? w * h : 0;
        }

        private static Rect ClampToWorkArea(Rect panel, Rect work)
        {
            // An oversized panel anchors at the work-area origin instead of
            // centring, so its header (the drag handle) stays reachable.
            double x = panel.Width >= work.Width
                ? work.X
                : Math.Max(work.X, Math.Min(panel.X, work.Right - panel.Width));
            double y = panel.Height >= work.Height
                ? work.Y
                : Math.Max(work.Y, Math.Min(panel.Y, work.Bottom - panel.Height));
            return new Rect(x, y, panel.Width, panel.Height);
        }
    }
}
```

- [ ] **Step 4: 运行并确认通过**

Run: `dotnet run --project tests/MonitorPlacementCheck` → 全 PASS。

---

### Task 2: `Core/MonitorInterop.cs`（Win32 互操作 + 缓存）

**Files:**
- Create: `Core/MonitorInterop.cs`

**Interfaces:**
- Produces:
  - `public static IList<MonitorInfo> GetMonitors()`（缓存；失败返回空列表）
  - `public static MonitorInfo GetMonitorForWindow(IntPtr hwnd)`
  - `public static MonitorInfo GetMonitorForDevicePoint(Point devicePoint)`
  - `public static Rect WorkAreaDipForWindow(Window window)`（无句柄回退 `SystemParameters.WorkArea`）
  - `public static void Invalidate()`

- [ ] **Step 1: 实现**

要点（避免堆砌：只列必须正确的细节）：

- `EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)`；回调里 `GetMonitorInfo` 用 `MONITORINFOEX` 取 `szDevice` 与 `rcWork`/`rcMonitor`；`MONITORINFOF_PRIMARY` 判定主屏。
- DPI：`Shcore.dll GetDpiForMonitor(hMonitor, 0 /*MDT_EFFECTIVE_DPI*/, out x, out y)`，异常（DLL 不存在）时 `DpiScale = 1.0`。
- 缓存：`private static IList<MonitorInfo> _cache;` + `lock`；`Invalidate()` 置 null。静态构造订阅 `Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => Invalidate();`（整个 lambda 包 try/catch）。
- `GetMonitorForWindow`：`MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)` → 在后端把 HMONITOR 映射到缓存的 `MonitorInfo`（缓存时保留 `IntPtr Handle` 字段，供匹配）。
- `WorkAreaDipForWindow(Window w)`：

```csharp
IntPtr hwnd = w == null ? IntPtr.Zero : new WindowInteropHelper(w).Handle;
if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea; // primary, DIPs - used before the handle exists
MonitorInfo m = GetMonitorForWindow(hwnd);
if (m == null) return SystemParameters.WorkArea;
var source = HwndSource.FromHwnd(hwnd);
if (source == null || source.CompositionTarget == null) return SystemParameters.WorkArea;
var toDip = source.CompositionTarget.TransformFromDevice;
Point tl = toDip.Transform(new Point(m.WorkAreaDevice.X, m.WorkAreaDevice.Y));
Point br = toDip.Transform(new Point(m.WorkAreaDevice.Right, m.WorkAreaDevice.Bottom));
return new Rect(tl, br);
```

- [ ] **Step 2: 冒烟验证（写进 `MonitorPlacementCheck`）**

追加用例（在本机执行，不追求确定性，只验证不合理值）：

```csharp
var live = MonitorInterop.GetMonitors();
Check(live.Count >= 1, "enumerates at least one monitor");
Check(live.All(m => m.BoundsDevice.Width > 0 && m.BoundsDevice.Height > 0 && m.DpiScale > 0),
    "monitor bounds and DPI are sane");
Check(live.Any(m => m.IsPrimary), "one monitor is primary");
```

Run: `dotnet run --project tests/MonitorPlacementCheck` → 全 PASS。

---

### Task 3: 面板恢复接入（`FolderData` + `FolderWidget`）

**Files:**
- Modify: `Core/FolderData.cs:59-60`（新增 `PanelDpiScale`）
- Modify: `Controls/FolderWidget.Interactions.cs:28-61, 160-175`
- Test: `tests/MonitorPlacementCheck`（追加纯逻辑用例）+ 手动验证

**Interfaces:**
- `FolderData.PanelDpiScale`：`public double? PanelDpiScale { get; set; }`（null = 旧数据，按当前窗口 DPI 解释）。
- `FolderWidget` 私有 `ResolvePanelPosition(x, y, w, h)`：返回 `(double x, double y)`；任何互操作失败回退现有 `ClampToVirtualScreen`。

- [ ] **Step 1: 实现**

- `PanelHeader_MouseLeftButtonUp`：写 `_data.PanelX/PanelY` 的同时写 `_data.PanelDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;`。
- `ShowPanel`：

```csharp
double x = _data.PanelX ?? defaultLeft;
double y = _data.PanelY ?? defaultTop;
(x, y) = ResolvePanelPosition(x, y, panelWidth, panelHeight);
Left = x; Top = y;
```

```csharp
private (double x, double y) ResolvePanelPosition(double x, double y, double width, double height)
{
    try
    {
        double currentScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var device = PanelPlacement.ToDeviceRect(x, y, width, height, _data.PanelDpiScale ?? 0, currentScale);
        var monitors = MonitorInterop.GetMonitors();
        if (monitors.Count == 0) return ClampToVirtualScreen(x, y, width, height);
        var clamped = PanelPlacement.ClampToMonitors(device, monitors);
        var target = MonitorInterop.GetMonitorForDevicePoint(
            new Point(clamped.X + clamped.Width / 2, clamped.Y + clamped.Height / 2)) ?? monitors[0];
        var dip = PanelPlacement.ToDipRect(clamped, target);
        return (dip.X, dip.Y);
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[Kobold] panel placement fallback: {ex.Message}");
        return ClampToVirtualScreen(x, y, width, height);
    }
}

private static (double x, double y) ClampToVirtualScreen(double x, double y, double width, double height)
{
    // existing ClampToScreen body, renamed and returning the tuple
}
```

- [ ] **Step 2: 追加纯逻辑用例（DPI 在保存/恢复间变化的路径）**

```csharp
// 11. 面板从 100% 屏移到 150% 屏后恢复：物理位置按保存 DPI 解释
var saved = PanelPlacement.ToDeviceRect(1930, 200, 300, 200, 1.0, 1.5);
var clampedSaved = PanelPlacement.ClampToMonitors(saved, monitors);
Check(clampedSaved.X == 1930 && clampedSaved.Y == 200,
    "a panel saved at 100% restores to the same physical spot at 150%");
Check(PanelPlacement.ToDipRect(clampedSaved, secondary) == new Rect(1930.0 / 1.5, 200.0 / 1.5, 200, 400.0 / 3),
    "and converts back with the destination scale");
```

- [ ] **Step 3: 构建 + 自检 + 冒烟**

Run: `dotnet build Kobold.csproj -c Debug` → 0 警告 0 错误。
Run: `dotnet run --project tests/MonitorPlacementCheck` → 全 PASS。
手动：拖动一个面板到任意位置 → 关闭 → 重启 → 面板回到原位置（单屏回归，必须做）。

---

### Task 4: 岛与面板默认位置改用所在监视器工作区

**Files:**
- Modify: `Controls/IslandWindow.xaml.cs:161-162, 457-474, 566-604`
- Modify: `Core/WidgetManager.cs:157-163`

**Interfaces:**
- `IslandWindow` 新增：`public double CenterX => Left + Width / 2;`
- `IslandWindow` 私有 `GetWorkArea()`：`MonitorInterop.WorkAreaDipForWindow(this)`（含回退）。

- [ ] **Step 1: 实现**

- `UpdateWindowGeometry`：

```csharp
double widest = Math.Max(WidgetConstants.ISLAND_HOVER_WIDTH, GetExpandedWidth());
Width = widest + 2 * ShadowPadding;
Height = WidgetConstants.ISLAND_EXPANDED_HEIGHT + ShadowPadding;

Rect work = GetWorkArea();
double fallbackCenter = work.Left + work.Width / 2;
double centerX = ClampCenterX(GetSavedCenterX() ?? fallbackCenter, Width);
Left = centerX - Width / 2;
Top = 0;  // glued to the top of the WPF virtual desktop, as before
```

- `ClampCenterX(double centerX, double windowWidth)`：改为实例方法（需要 `this` 取工作区）——用 `GetWorkArea()` 的 `Left/Right`；窗口比工作区宽时返回工作区中心。
- `Pill_MouseMove`：拖动过程中用虚拟屏边界（`SystemParameters.VirtualScreenLeft/Width`）作为即时夹取（避免拖动中反复重算监视器）；松手时调用 `UpdateWindowGeometry()`（会按岛所在监视器工作区再夹一次）并保存 `IslandX`。
- `RebuildTiles`：`TileScroller.Width = IslandLayout.MiddleViewWidth(_folders.Count, GetWorkArea().Width);`
- `WidgetManager.OpenPanel`：

```csharp
var (panelWidth, _) = widget.GetPanelSize();
double left = _island.CenterX - panelWidth / 2; // clamped per-monitor inside ShowPanel
widget.ShowPanel(left, _island.PanelTop, activate);
```

- [ ] **Step 2: 验证**

Run: `dotnet build Kobold.csproj -c Debug` → 0 警告 0 错误。
Run（全部必须绿）：`IslandLayoutCheck`、`StartupPanelsCheck`、`XamlLoadCheck`、`MenuRenderCheck`。
手动：启动后岛仍在顶部居中；点 tile 面板仍在岛下方居中；拖动岛左右 → 重启回原位。

---

### Task 5: 接入 Run-Checks 矩阵 + 全量回归

- [ ] **Step 1:** `tools/Run-Checks.ps1` 矩阵追加 `MonitorPlacementCheck`（`Ci = $true`）。
- [ ] **Step 2:** `powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all`
  Expected: 全绿（window/桌面组如因当前会话不可用而失败，单独报告，不掩盖）。
- [ ] **Step 3:** 手动多屏/混合 DPI 验证（若设备可用；记录在计划执行记录里；不可用时明确声明未验证）。

---

## Self-Review

- **Spec 覆盖**：纯换算与选屏=Task 1；互操作=Task 2；面板恢复=Task 3；岛与默认位置=Task 4；回归=Task 5。
- **风险**：WPF 在混合 DPI 下 DIP 原点的精确语义无法离线验证，任务以 PaperTodo 已验证的"物理优先"算法为准，并以手动多屏验证兜底。
- **明确不做**：完整 Edge 队列几何、按设备名维护队列、SetWindowPos 直接改窗口（继续用 WPF Left/Top）。

---

## 执行记录（2026-09-24）

**状态：Task 1-5 全部完成；自动化全绿；手动验证受环境限制，见下。**

- Task 1-2（上次会话）：`Core/PanelPlacement.cs`（含 `MonitorInfo`）、`Core/MonitorInterop.cs`（枚举 + 缓存 + `DisplaySettingsChanged` 失效 + `WorkAreaDipForWindow`）、`tests/MonitorPlacementCheck`（15 例，含真实监视器冒烟）。
- Task 3：`FolderData.PanelDpiScale`；拖动松手记录 `VisualTreeHelper.GetDpi`；`ShowPanel` 走 `ResolvePanelPosition`（互操作失败回退 `ClampToVirtualScreen`）。
- Task 4：`IslandWindow` 新增 `CenterX` / `GetWorkArea()`；`UpdateWindowGeometry` / `ClampCenterX` / `RebuildTiles` 改用所在监视器工作区。**偏差**：`GetExpandedWidth` 也改用 `GetWorkArea().Width`（计划未列；它与 `RebuildTiles` 共用同一上限，不一起改会在换屏后让 tile 滚动区超出行宽度）。拖动中用虚拟屏边界即时夹取，松手先 `UpdateWindowGeometry()` 再保存 `IslandX`。`WidgetManager.OpenPanel` 以 `_island.CenterX` 居中。
- Task 5：`tools/Run-Checks.ps1` 矩阵加入 `MonitorPlacementCheck`（Ci=$true）；`-Group all` 22→24 项全绿（含 installer 60 例）。
- **额外自动化冒烟**（临时工程，位于 %TEMP%，未入库）：真实 200% DPI 单屏上驱动 `FolderWidget.ShowPanel`，同 DPI 恢复 / 旧数据（无 DPI）恢复 / 离屏拉回三组用例的 WPF DIP 与原生物理矩形（GetWindowRect）均与期望一致。
- **未验证（环境限制，如实声明）**：
  - 多屏 / 混合 DPI：本机仅 1 块显示器（3120x2080@200%，工作区 3120x1984），无法执行；算法以 PaperTodo 已验证的物理优先算法为准。
  - “拖动面板→关闭→重启→回原位”人工步骤：为避免打断正在运行的 Kobold 实例（单实例，未能安全重启）未执行；其恢复路径已由上述自动化冒烟等价覆盖（拖动落点仅一行 `PanelDpiScale` 记录，未单独验证）。
  - “拖动岛→重启回原位”：同上未执行；夹取数学由 `IslandLayoutCheck` + 构建验证覆盖。
