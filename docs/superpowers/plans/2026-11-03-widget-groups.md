# Widget 标签组合 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: `subagent-driven-development`. **本计划强制 subagent 模式，不是可选项。** 主 agent 只负责读代码、确认设计、拆任务、派发 subagent、审核 diff、跑 `dotnet build` / 相关 `tests/*Check`、处理跨任务冲突和最终 `Run-Checks`。所有实现任务（**包括 T0 Spike**）必须通过 `subagent` 工具派发；主 agent 不直接写实现代码。每个 subagent 必须先写失败检查，再实现；只改本任务文件；返回改动文件、检查命令、检查结果。

**Goal:** 把多个 Widget 合并成带标签组的单个组窗口；支持合并、切换、分离、持久化、island 打开/切换、组级锁定/置顶/几何，首版不做嵌套组、组内单独 resize、跨组复杂拖拽。

**Architecture:** `WidgetGroup` 是组状态唯一来源；`WidgetData.GroupId/GroupOrder` 只做成员索引。组窗口复用一个组级外壳，组内每个组件保留自己的 View 实例和数据状态；切换只改变 active View 和键盘焦点，不销毁重建 View。组尺寸共享，成员自己的尺寸只在合并计算和分离恢复时使用。

**Tech Stack:** WPF / .NET Framework 4.8 / C# 7.3；现有 `tests/*Check` 控制台项目 + `tools/Run-Checks.ps1`。

**Visual references:**
- 最终标签方向：`docs/mockups/2026-11-03-widget-groups-ui1e.html` / `.png`
- island + 样式参考：`docs/mockups/2026-11-03-widget-groups-ui1d.html` / `.png`
- 尺寸策略：`docs/mockups/2026-11-03-widget-groups-ui1c.html` / `.png`
- 旧参考（不作为最终方向）：`ui1`、`ui1b`（左侧 rail）

---

## 执行模式：强制 Subagent

**主 agent 不做实现。** 主 agent 的动作只有：

1. 读 `AGENTS.md`、本计划和相关源码，确认设计/任务边界。
2. 用 `subagent` 工具派发任务；每个任务一个 subagent；独立任务可并行，有依赖的任务按 T1→T2→T4→T5→T6→T7 顺序。
3. 审核 subagent 的 `git diff`，确认只改了任务范围。
4. 跑 `dotnet build` + 相关 `tests/*Check`；必要时跑 `tools/Run-Checks.ps1`。
5. 处理跨任务冲突；不通过就退回对应 subagent 重做，不自己打补丁。
6. 最终验证；除用户明确要求外不提交 git。

**T0 Spike 也是一个 subagent 任务。** 新对话的第一个动作必须是：

```text
用 subagent 工具派一个 T0 Spike subagent，给它本计划 T0 段和 UI1e 路径；
不要让主 agent 自己写 tests/GroupUiSpike。
```

**每个 subagent 必须收到并遵守：**

- 先写失败检查（`tests/*Check` 的断言/编译失败，或 Spike 的验收清单），运行并确认红。
- 再写最小实现，运行检查确认绿。
- 只改本任务列出的文件；不改其他任务文件、不顺手重构。
- 不提交 git；不 reset/stash 当前未提交的文件夹颜色修复。
- 返回：改动文件、检查命令、完整结果、未解决问题。
- 如果发现任务边界外的问题：停止并报告，不要扩大改动。

**Subagent 提示模板：**

```text
你是 Task T? 的 subagent。先读 AGENTS.md 和
docs/superpowers/plans/2026-11-03-widget-groups.md 的对应任务。
只改任务列出的文件。先写失败检查并运行，确认失败原因正确；再写最小实现。
不提交 git；不要 reset/stash/删除当前未提交的文件夹颜色修复和 mockups。
返回改动文件、命令、结果、阻塞点。
```

---

## 已锁定的决策

1. **标签位置**：顶部标签，位于 title 之上，全部左对齐。
2. **标签视觉**：无底框、无胶囊、无常驻背景；active 只用 **2px 细下划线** + 当前组件色图标 + 文字加粗；hover 只提亮文字；键盘 focus 才显示 1px 焦点环/下划线。
3. **title**：标签显示组件身份，title 显示当前上下文（Todo 选中日、Note 标题、Folder 路径/选择、Search 状态），避免和标签重复。
4. **island**（2026-11-04 P2 设计已确认，取代原“每成员一个 tile”）：同组成员折叠成一个 44×44 组文件夹 tile；内部 2×2 成员图标网格，≤4 全显示，5+ 显示前 3 个 + `+N`；点击向右展开 4 列成员面板；只有展开面板里的 active 成员显示 open 环；折叠 tile 用中性细轮廓表示组打开。实现见 P2 设计节，本轮先不实现。
5. **尺寸**：组窗口一套尺寸；切换标签不跳窗；成员自己的 `PanelWidth/Height` 只在合并计算和分离恢复时使用；组内不做 per-tab resize。
6. **组状态**（2026-11-04 P2 设计已确认，取代原“组级 pin”）：组级 `IsExpanded/IsLocked/PanelX/Y/DpiScale/Width/Height/ActiveWidgetId`；pin 改为成员级 `WidgetData.IsPanelPinned`；组内任一成员 pinned → 组窗口失焦不收起，全部未 pinned → 收起；组窗口 header 的 pin 只操作 active 成员；组级 `IsPanelPinned` 变为派生/兼容字段。成员内容状态仍由各自数据和 View 管理。
7. **首版范围**：不做嵌套组、不做组内单独 resize、不做 group→group 合并、不做标签拖进另一个组。
8. **Folder**：v1 目标包含 Folder，但按 T3a（行为保持抽取）→ T3b（组内接入）分阶段；若 T3a 回归过大，v1 先发 Todo/Note/Search，Folder 延后，并在计划/CHANGELOG 说明。

---

## 当前工作区状态（不要破坏）

当前分支 `main`，未提交。下列改动是刚部署过的文件夹颜色修复，**不要 reset/stash/回退**：

- `Core/FolderColor.cs`
- `Controls/FolderWidget.Dialogs.cs`
- `Controls/FolderWidget.xaml.cs`
- `Helpers/FolderIconSource.cs`（新增）
- `tests/FolderColorCheck/Program.cs`

当前验证基线（本计划编写前已跑过）：

- `dotnet build Kobold.csproj -c Debug`：成功，0 warning / 0 error
- `tools/Run-Checks.ps1 -Group ci`：26 个检查全部通过
- `tests/FolderColorCheck`：全部通过，包含“改色后重新加载新图标”
- `tests/XamlLoadCheck`：全部通过
- 新版本已部署到 `%LocalAppData%\Programs\Kobold`，进程已运行

工作区还有：

- 根目录 `desktop.ini`：用户给 `Kobold` 文件夹上色留下的隐藏/系统文件，不是代码，不要删除。
- `docs/mockups/2026-11-03-widget-groups-ui1*.html/png`：视觉稿，不要当成生产代码。

**不要提交 git，除非用户明确要求。**

---

## 尺寸策略（正式实现必须按这个）

设：

- `GROUP_TAB_ROW = 32`（顶部标签行）
- `HEADER = 40`（现有 `WidgetConstants.HEADER_HEIGHT`）
- 成员 `WidgetData.PanelWidth/PanelHeight` 是独立面板的完整窗口尺寸（含 40 header）
- 组窗口 `WidgetGroup.PanelWidth/PanelHeight` 是组窗口完整尺寸（含 32 tab row + 40 header）

规则：

1. **合并时**
   - 组宽 = `max(目标组宽, 新成员 PanelWidth, 所有成员最小宽)`，clamp 到工作区。
   - 组高 = `max(目标组高, 新成员 PanelHeight + GROUP_TAB_ROW, 所有成员最小高 + GROUP_TAB_ROW)`，clamp 到工作区。
   - 新成员更大 → 组长大；新成员更小 → 组不缩。
2. **切换标签时**
   - 不修改组窗口尺寸。
   - active View 的宿主返回“组内容区尺寸” = `组宽 - border`、`组高 - GROUP_TAB_ROW - HEADER - border`。
   - View 按这个尺寸重排；现有 Todo/Note/Search/Folder 都按响应式布局处理。
   - 如果未来某个 View 声明最大内容宽度，在内容区里 **左上对齐**，不居中、不强行拉满。
3. **组 resize**
   - 只改组 `PanelWidth/Height/X/Y/DpiScale`。
   - 组最小尺寸 = `max(所有成员最小宽)` × `max(所有成员最小高) + GROUP_TAB_ROW`；不能小于它。
   - 成员的 `PanelWidth/Height` 不被组 resize 改写。
4. **分离时**
   - 新独立窗口使用该成员自己的 `PanelWidth/Height`，不继承组尺寸。
   - 位置取组窗口当前位置 + 小幅 cascade，clamp 到工作区。
   - 组打开/锁定/置顶状态按组状态继承（打开、pin、lock）；解组后成员自己的展开/锁定/置顶以组状态为准。
5. **组内不做 per-tab resize**：不允许切到小组件时把组窗口缩回去；避免跳窗和布局抖动。

---

## island 方案（正式实现）

保留每个成员一个 tile，不折叠成“组 tile”：

- 组内组件仍出现在 `_config.Widgets` 顺序中，各自一个 44×44 tile。
- 同组成员 tile 右上角显示数量角标（`2`/`3`…）；悬停 tooltip 列出成员名。
- 只有 active 成员显示现有 open 环；其他成员只显示角标。
- 点击规则：
  - 组关闭，点组内任意 tile → 打开组并切到该成员。
  - 组已打开，点另一个成员 → 只切标签，保持组打开。
  - 组已打开，点 active 成员 → 收起组窗口（沿用当前 toggle 手感）。
  - 右键成员 tile → 成员菜单 + “切换到该标签 / 分离此组件 / 隐藏整组”。
  - 删除/分离后剩 1 个 → 自动解散；角标消失。
- island 不加“整组一个 tile + 弹出成员列表”的模式（v1 不做；以后标签很多再考虑）。
- island 拖拽合并不在 v1；合并入口是窗口 header 拖拽。

---

## T0 · Spike（先做，不进入正式实现）

**执行方式：** 一个 T0 subagent 完成；主 agent 只审核 diff、跑 build/启动 Spike、记录 PASS/FAIL。**主 agent 不自己写 Spike。**

**目标：** 用真实 `TodoCalendarView` + `NotePanelView` 验证“重挂到组窗口 + 顶部裸标签 + 细下划线”可行；不做生产改动。

**位置：** `tests/GroupUiSpike/`（临时 WinExe，引用主项目；不加入 `Run-Checks`，不提交）。

**UI：** 按 `docs/mockups/2026-11-03-widget-groups-ui1e.html` 的 A 案：顶部裸标签、无底框、active 2px 细下划线、title 在标签下方。Spike 可以自建简单外壳，不必先改生产 `WidgetChrome`。

### 步骤

- [ ] **T0.1 读取上下文**
  - 读 `AGENTS.md` + 本计划；确认当前未提交的文件夹颜色修复不要动。
  - 读 `Core/WidgetManager.cs`、`Controls/WidgetWindowBase.cs`、`WidgetChrome.xaml(.cs)`、`TodoWidget`、`NoteWidget`、`TodoCalendarView`、`NotePanelView`。

- [ ] **T0.2 Bootstrap（临时 sandbox）**
  - `WidgetManager.OverrideConfigPathsForChecks(temp, temp.backup)`，再创建 `Kobold.App` 并 `InitializeComponent()` 加载主题资源。
  - `WidgetManager.Instance` 初始化 `ThemeManager`；全程只写临时 config。
  - 造 Todo 样例（今天/父任务/子任务/完成项）和 Note 样例（2–3 篇 markdown）。

- [ ] **T0.3 建临时 `GroupSpikeWindow`**
  - 760×540 左右；布局：顶部 32px 标签行 + 40px title 行 + 内容。
  - 标签：`待办` / `笔记`，无底框、左对齐；active 2px 下划线用组件色；点击切换。
  - title 显示上下文（Todo 日期 / Note 标题）。
  - 壳用现有主题 token；可先用普通 `Window`，Show/Hide/Topmost 用简单方法；如果要验证 `WidgetWindowBase` 的 pin/lock，可临时继承它并用样例 `TodoData` 作为组状态（仅 Spike）。

- [ ] **T0.4 挂真实 View**
  - 创建隐藏的 `TodoWidget` / `NoteWidget` 作为临时 host，把 `ContentHost.Content` 摘下，塞进组窗口两个内容槽。
  - 两个 View 实例都保留，切换只改 `Visibility`，不重建。
  - 记录 View 对 host 的依赖：`IsPanelOpen/Height/GetPanelSize`、`NotifyDataChanged`、`RefreshChromeTitle`、`PanelChrome`、窗口级键盘处理。

- [ ] **T0.5 控制窗（Spike 专用）**
  - 小控制窗：Show / Hide / Toggle Topmost / Toggle Pin / Toggle Lock / Close。
  - 用来验证组窗口显示隐藏、置顶、pin、lock 是否符合独立 Widget 行为。

- [ ] **T0.6 交互验证**
  - Todo add box 输入 → 切 Note → Note 编辑 → 切回 Todo；确认文字、caret、焦点、Ctrl+Z、Esc、外部点击失焦提交。
  - Note 标题菜单、Todo 右键菜单、Todo 复选框/子任务各走一遍。
  - 连续切换 10 次；观察闪烁、布局跳动、输入法候选窗错位。
  - resize 组窗口到 700×500 / 820×600；Todo/Note 重排是否正常；切标签是否保持组尺寸。
  - Show/Hide/Pin/Lock/Topmost 各验证一次。
  - 关闭 Spike 后检查真实 `%AppData%\Kobold\config.json` 字节不变。

### Spike 验收

- [ ] 真实 Todo/Note View 能渲染、能输入；不是 mock。
- [ ] 切换标签后两边都能继续输入；caret/焦点不丢，或记录精确失败步骤。
- [ ] Ctrl+Z、Esc、外部点击提交、Note 标题菜单、Todo 右键菜单可用。
- [ ] 顶部裸标签点击切换；active 2px 下划线；无常驻底框。
- [ ] Show/Hide/Pin/Lock/Topmost 符合现有独立 Widget 行为。
- [ ] resize 组窗口时内容重排；切标签不跳窗。
- [ ] 只写临时 config；真实 config 不被改写。
- [ ] 不修改 `Core/WidgetManager.cs`、`AppConfig.cs`、`WidgetChrome`、`TodoWidget`、`NoteWidget` 等生产文件。

### Spike 报告

新会话结束时返回：

- 改动文件（应只有 `tests/GroupUiSpike/**`）
- 运行命令
- 每项验收的 PASS/FAIL
- 截图/录屏路径（如有）
- 精确失败步骤（如果失败）
- 结论：**GO**（进入正式 T1/T2）或 **FALLBACK**（退回 Workspace 预设组方案）

---

## 正式实现任务拆分

**每个任务一个 subagent；主 agent 不直接写实现。** 每个 subagent 先写失败检查，再实现；主 agent 审核 diff + 跑 build/checks。任务之间不要跨范围改文件。

### T1 · 组数据模型 + 配置持久化 + 迁移/修复

**Files:**
- Create: `Core/WidgetGroup.cs`
- Modify: `Core/WidgetData.cs`（`GroupId`, `GroupOrder`）
- Modify: `Core/AppConfig.cs`（`Groups`、加载修复、`RemoveWidget` 清理）
- Modify: `Core/StartupPanels.cs`（组恢复规则，或新增 `GroupStartupPolicy`）
- Create: `tests/WidgetGroupsCheck/WidgetGroupsCheck.csproj` + `Program.cs`
- Modify: `tests/ConfigSafetyCheck/Program.cs`、`tests/WidgetKindsCheck/Program.cs`（组 JSON/修复）
- Modify: `tools/Run-Checks.ps1`（`WidgetGroupsCheck`，`Ci = true`）

**Interfaces:**
- `WidgetGroup`：`Id`, `ActiveWidgetId`, `PanelX/Y/DpiScale/Width/Height`, `IsExpanded`, `IsLocked`, `IsPanelPinned`。
- `WidgetData.GroupId`（string，null 忽略）、`GroupOrder`（int?，null 忽略）。
- `AppConfig.Groups`（`List<WidgetGroup>`，null 忽略；未配置不写空对象）。

**失败检查（先红）：**
- 组 JSON round-trip：GroupId/Order、ActiveId、几何、expanded/locked/pinned 全部保留。
- 非法修复：孤儿 GroupId 清除；<2 成员组解散；ActiveId 缺失回退第一个；Order 重复归一；重复 GroupId/组 Id 处理。
- 删除成员：`RemoveWidget` 清理 GroupId；剩 1 个自动解散。
- 旧配置无 Groups 可加载且保存不破坏。

**通过检查：**
```powershell
dotnet run --project tests\WidgetGroupsCheck
dotnet run --project tests\ConfigSafetyCheck
dotnet run --project tests\WidgetKindsCheck
```

---

### T2 · View 宿主抽象 + Todo/Note/Search 适配

**Files:**
- Create: `Controls/IWidgetViewHost.cs`（internal）
- Create: `Controls/IWidgetContentView.cs`（internal）
- Create: `Controls/WidgetViewFactory.cs`
- Modify: `Controls/TodoCalendarView.xaml.cs`
- Modify: `Controls/NotePanelView.xaml.cs`
- Modify: `Controls/SearchPanelView.xaml.cs`
- Modify: `Controls/TodoWidget.xaml.cs`
- Modify: `Controls/NoteWidget.xaml.cs`
- Modify: `Controls/SearchWidget.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`（新增 pin）

**关键接口（最小表面）：**

```csharp
internal interface IWidgetViewHost
{
    WidgetData Data { get; }
    WidgetChrome Chrome { get; }
    bool IsPanelOpen { get; }
    double ContentWidth { get; }
    double ContentHeight { get; }
    (double width, double height) GetPanelSize();
    void NotifyDataChanged();
    void RefreshChromeTitle(string title = null);
    void PersistSelection();
    void ShowWidgetMenu();
}

internal interface IWidgetContentView
{
    WidgetData Data { get; }
    FrameworkElement View { get; }
    string ChromeTitle { get; }
    bool HasTitleMenu { get; }
    void Attach(WidgetData data, IWidgetViewHost host);
    void Activate();
    void Deactivate();
    void Refresh();
    void RefreshChromeTitle();
    void ToggleTitleMenu(FrameworkElement anchor);
    bool HandlePreviewKeyDown(KeyEventArgs e);
    void HandleMouseDownOutsideEditor(object source);
    void FlushPendingEdit();
    bool TryUndo();
}
```

**必须解决的宿主问题：**
- `TodoCalendarView` 不再用 `_host.IsPanelOpen + _host.Height - 42` 直接算内容高度；改用 `IWidgetViewHost.ContentHeight`（独立窗口返回 `Height - 42`，组窗口返回组内容区高度）。
- `NotePanelView` 的 `NotifyDataChanged` / `RefreshChromeTitle` 走接口；`ChromeTitle` 暴露给组 window。
- `SearchPanelView` 的 `PanelChrome` 改走 `IWidgetViewHost.Chrome`；Search 的 glow 仍然工作。
- 把 `TodoWidget` / `NoteWidget` 窗口级 `PreviewKeyDown` / `PreviewMouseLeftButtonDown` 逻辑收进 View（或 View 的 `HandlePreviewKeyDown` / `HandleMouseDownOutsideEditor`），独立窗口和组窗口都走同一个入口。
- `FlushPendingEdit` / `TryUndo` 在组 close / 切标签时能被组窗口调用。
- 独立 Widget 行为不变。

**失败检查（先红）：**
- `XamlLoadCheck` 新增：View 能 attach 到一个 fake `IWidgetViewHost`；`ContentHeight` 改变时 Todo 布局跟着变；Note 的 `ChromeTitle` / flush / undo 走接口。
- 现有 Todo/Note/Search 检查保持绿。

**通过检查：**
```powershell
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\WidgetGroupsCheck   # 若 T1 已存在
```

---

### T3a · FolderPanelView 行为保持抽取

**Files:**
- Create: `Controls/FolderPanelView.xaml` + `.xaml.cs`
- Modify: `Controls/FolderWidget.xaml` / `.xaml.cs` + 现有 partials
- Modify: `tests/XamlLoadCheck/Program.cs`
- 现有 Folder 检查保持通过

**目标：**
- 把 `FolderWidget.xaml` 里的 `BackButton/PathButton/HelpButton/ContentPanel/MoreItemsText` 和内容行为搬进 `FolderPanelView`。
- `FolderWidget` 只保留窗口外壳、host 适配和生命周期；行为输出与现在一致。
- 先不做组窗口接入；只做行为保持。

**失败检查（先红）：**
- `XamlLoadCheck` 加载 `FolderPanelView`，确认 content/host 结构存在。
- 现有 `FolderListingCheck`、`FolderWatchCheck`、`FolderColorCheck`、`FolderIconCheck` 保持绿。

**通过检查：**
```powershell
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\FolderListingCheck
dotnet run --project tests\FolderWatchCheck
dotnet run --project tests\FolderColorCheck
```

---

### T3b · Folder 组内接入（可与 T4 并行）

**Files:**
- `Controls/FolderPanelView.*`
- `Controls/FolderWidget.*`
- `Controls/IWidgetViewHost.*`（如需 HWND/激活钩子）
- `tests/XamlLoadCheck/Program.cs`

**目标：**
- `Activate/Deactivate` 驱动 watcher / browse refresh / native menu hook。
- 原生 Shell 菜单的 HWND 从 host 取（组窗口 host 提供 `PanelHwnd`），不再假设 `FolderWidget` 自己是顶层窗口。
- Folder 的尺寸自适应使用 `IWidgetViewHost.ContentWidth/Height`，不要读组窗口总高度。
- 锁定时拖放/浏览行为保持现有规则。

**通过检查：**
```powershell
dotnet run --project tests\FolderListingCheck
dotnet run --project tests\FolderWatchCheck
dotnet run --project tests\FolderColorCheck
dotnet run --project tests\XamlLoadCheck
```

> 如果 T3a 出现大面积回归：停止 T3b，v1 先发 Todo/Note/Search，Folder 组内接入延后；在 CHANGELOG/README 写明。

---

### T4 · WidgetGroupWindow：组外壳 + 顶部裸标签 + 内容切换

**Files:**
- Create: `Controls/WidgetGroupWindow.xaml` + `.xaml.cs`
- Create: `Controls/WidgetGroupTabStrip.cs`（或 code-behind）
- Modify: `Controls/WidgetChrome.xaml` + `.xaml.cs`（可选顶部 tab row / 标题 accent 控制）
- Modify: `Controls/WidgetWindowBase.cs`（如需抽 `IPanelState`/`PanelWindowController`，只做最小共享）
- Modify: `tests/XamlLoadCheck/Program.cs`
- Modify: `tests/MenuRenderCheck/Program.cs`（tab 菜单）

**实现：**
- 组窗口布局：`tab row 32` + `header 40` + `content`；tab 行在 title 之上。
- 标签：`待办 / 笔记 / 搜索` 文字 + 图标；无底框；active 2px 下划线（当前组件色）；左边距和 title 对齐；点击切换。
- title 行：当前上下文（Todo 日期 / Note 标题 / Folder 路径 / Search 状态）+ 锁/钉 + 当前组件菜单。
- 内容 host：每成员一个 View 实例；active `Visible`，其他 `Collapsed`；切换时保存/恢复焦点。
- `WidgetGroupWindow` 实现 `IWidgetWindow`（`WidgetId = group id`，`Data = active member`，组级 show/hide/pin/lock/geometry）。
- 不复制 `WidgetWindowBase` 的 600 行；抽一个可复用 panel controller 或让 base 接受一个 `IPanelState`，组窗口和独立窗口共用 show/hide/DPI/drag/resize。
- 关闭组窗口：flush 所有成员 View 的 pending edit；保存组状态。

**失败检查（先红）：**
- `XamlLoadCheck`：组窗口加载；顶部裸标签存在且无底框；active 下划线存在；切 tab 改变 active content；title 跟着变；锁/钉视觉跟组状态；没有独立 3px 色条。
- `MenuRenderCheck`：标签右键菜单/当前组件菜单有“分离此组件”等（T6 可先放占位，但结构在）。

**通过检查：**
```powershell
dotnet build Kobold.csproj -c Debug
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\MenuRenderCheck
```

---

### T5 · Manager / island / 启动恢复 / 菜单接入

**Files:**
- Modify: `Core/WidgetManager.cs`
- Modify: `Core/StartupPanels.cs`
- Modify: `Controls/IslandWindow.xaml.cs`
- Modify: `Helpers/WidgetMenuBuilder.cs` 或 Create `Helpers/WidgetGroupMenuBuilder.cs`
- Modify: `Controls/WidgetWindowFactory.cs` / Create `WidgetViewFactory` 接入
- Modify: `tests/WidgetGroupsCheck/Program.cs`
- Modify: `tests/StartupPanelsCheck/Program.cs`
- Modify: `tests/IslandLayoutCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**实现：**
- `WidgetManager` 维护组与成员：
  - 组窗口按 `WidgetGroup` 创建；组内成员不再创建独立窗口。
  - `FindHostForWidget(widgetId)` 解析到组窗口；组 id / 成员 id 都能查。
  - `_widgets` 包含未分组窗口 + 组窗口；`ShowAll/HideAll/Shutdown/SetLanguage/RefreshAllWidgets/IsBusy` 按 host 处理。
  - `RefreshIsland` 传成员 GroupId/组大小；open state 只标 active 成员。
- island：
  - 成员 tile 角标；只有 active open 环。
  - 点击规则：关闭→打开并切；打开其他成员→切；active→收起。
  - 右键成员菜单：切换此标签 / 分离 / 隐藏整组 / 成员常用操作。
- 启动：
  - 先恢复组（组 `IsExpanded`），再恢复未分组 Widget。
  - 组成员自己的 `IsExpanded` 不参与独立窗口恢复。
- Search：组内 Search 仍计入 `FileSearchIndexService.SyncWithWidgets(_config.Widgets)`；不要因组窗口创建/关闭误停索引。

**失败检查（先红）：**
- `WidgetGroupsCheck`：组查找、active 投影、启动恢复、删除/解散清理。
- `StartupPanelsCheck`：组成员不重复恢复；组只恢复一次。
- `IslandLayoutCheck`：成员 tile 不改变布局，角标不改变计数。
- `XamlLoadCheck`：island open state 只亮 active。

**通过检查：**
```powershell
dotnet run --project tests\WidgetGroupsCheck
dotnet run --project tests\StartupPanelsCheck
dotnet run --project tests\IslandLayoutCheck
dotnet run --project tests\XamlLoadCheck
```

---

### T6 · 合并 / 撕出 / 右键分离 / 自动解散

**Files:**
- Create: `Core/GroupDragPolicy.cs`（纯逻辑）
- Modify: `Controls/WidgetWindowBase.cs`（header drag merge target）
- Modify: `Controls/WidgetGroupWindow.xaml.cs`（tab drag-out）
- Create: `Helpers/WidgetGroupMenuBuilder.cs` 或扩展 `WidgetMenuBuilder`
- Modify: `tests/WidgetGroupsCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**实现：**
- **合并**：header 拖动结束，用屏幕坐标和所有 widget/group 窗口矩形找目标（排除 source）；目标显示插入提示（tab ghost/边框）；release 调 `WidgetManager.MergeWidgets(source, target)`；源/目标 lock 时拒绝；group→group 不合并。
- **tab 撕出**：组标签拖动超过阈值且 release 在组窗口外 → 分离成独立窗口；release 在组内 v1 不重排（可后续）。
- **右键标签“分离此组件”**：独立窗口；组剩 1 自动解散。
- **自动解散**：组窗口剩 1 成员 → 生成/恢复独立窗口，清理组数据/窗口；island 角标消失；组窗口关闭前 flush。
- **组合并顺序**：source 插入到 target 之后；source 成为新 tab；active 保持 target（除非用户拖的是 tab；tab 拖出不属于合并）。
- **锁定**：组 lock 禁止组拖动/resize、标签拖出、合并；右键分离置灰；必须先解锁。

**失败检查（先红）：**
- 纯逻辑：目标选择排除 source、阈值、lock 拒绝、顺序插入、单成员解散、无 group→group。
- `XamlLoadCheck`：tab 右键菜单项存在；组窗口 tab 可拖；merge target 提示可见（不需要真实鼠标）。

**通过检查：**
```powershell
dotnet run --project tests\WidgetGroupsCheck
dotnet run --project tests\XamlLoadCheck
# 手工 smoke：真实窗口拖拽合并 / 撕出 / 右键分离 / 自动解散
```

---

### T7 · DPI / 多屏 / 配置安全 / 最终回归

**Files:**
- `Core/PanelPlacement`/`MonitorInterop` 调用（组窗口）
- `Core/AppConfig.cs` / `ConfigSafetyCheck`
- `docs/CHANGELOG.md`、`README.md`（如行为变化）
- `tools/Run-Checks.ps1`（矩阵）
- `tests/WidgetGroupsCheck`、`XamlLoadCheck`

**实现/检查：**
- 组几何保存/恢复按 `PanelDpiScale`，跨不同 DPI 屏 clamp 到工作区。
- 合并/分离/组 resize 后组最小尺寸不小于成员 max min。
- 组配置损坏/孤儿/重复/单成员修复不丢数据。
- 关闭/退出：组窗口 flush 所有成员 pending edit；`SaveConfigNow` 落盘。
- 全量检查。

**通过检查：**
```powershell
dotnet build Kobold.csproj -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group ci
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group checks
```

> `Run-Checks -Group checks` 包含本地 UI 检查（XamlLoadCheck/MenuRenderCheck/DesktopIconsCheck/FolderColorCheck 等），需要交互桌面。

---

## 检查矩阵（新增）

- `WidgetGroupsCheck`：纯逻辑/配置/合并/解散/投影；加入 `Run-Checks.ps1`，`Ci = true`。
- `XamlLoadCheck`：组窗口加载、顶部裸标签、active 下划线、切 tab、title 上下文、island 角标/open state、Folder view 加载。
- `MenuRenderCheck`：标签右键菜单/成员菜单结构。
- `ConfigSafetyCheck`：Groups 块损坏/修复。
- `WidgetKindsCheck`：GroupId/GroupOrder JSON round-trip。
- `StartupPanelsCheck`：组恢复只一次。
- `Folder*Check`：T3a/T3b 回归。
- `GroupUiSpike`：**不**加入 Run-Checks，手工 Spike。

---

## 风险

1. **View 重挂后的焦点/键盘/输入**：`TodoCalendarView` 仍可能读 host 尺寸；`NotePanelView` 的 chrome title/flush；`SearchPanelView` 的 `PanelChrome`；窗口级 Ctrl+Z/Esc/外部点击处理。T0 Spike 先验证；T2 用 interface + focus snapshot 解决。
2. **Folder 抽取回归**：浏览栈/watcher/拖放/原生 Shell 菜单/选择都耦合窗口；T3a 先行为保持，T3b 再接组；T3a 回归过大就延后 Folder。
3. **组状态/几何/生命周期多源**：组 vs 成员、island active、启动恢复、自动解散、lock/pin、DPI；T1 纯策略 + 配置修复 + T5 投影 + T7 全量回归。
4. **顶部标签高度/组窗口 chrome**：tab row + header 占 72px；小组件（Note/Folder）内容变矮。Spike 验证 760×540 和 700×500；如果内容太挤，再考虑 28px tab row 或 title 行压缩，不要退回左侧 rail。
5. **合并手势命中**：source 窗口跟随鼠标，不能只用 `WindowFromPoint`；用屏幕坐标 + 所有候选窗口矩形 + 排除 source。T6 纯逻辑先测。

---

## 新对话 Handoff Prompt

在新对话里直接发：

```text
读 AGENTS.md 和 docs/superpowers/plans/2026-11-03-widget-groups.md。
用 subagent 模式，不要主 agent 自己写实现。
第一步：用 subagent 工具派一个 T0 Spike subagent，不要自己写 tests/GroupUiSpike。
只做 T0 Spike，不改生产代码，不提交 git。
工作区当前有未提交的文件夹颜色修复（Core/FolderColor.cs、Controls/FolderWidget*.cs、
Helpers/FolderIconSource.cs、tests/FolderColorCheck/Program.cs）和 UI mockups；
不要 reset/stash/删除它们。
Spike 放 tests/GroupUiSpike/，用真实 TodoCalendarView + NotePanelView，
按 UI1e 的顶部裸标签 + 2px 细下划线。
每个 subagent 先写失败检查/验收，再实现；只改任务范围文件。
主 agent 只审核 diff、跑 build/Spike、记录 PASS/FAIL；返回 GO/FALLBACK 结论。
```

如果 Spike GO，再发：

```text
Spike GO。用 subagent 模式按计划 T1 → T2 → T3a/T3b → T4 → T5 → T6 → T7 执行；
每个任务派一个 subagent，先写失败检查；主 agent 不直接写实现，只审核并跑 build + 相关 Check。
不要提交 git，除非我明确要求。
```

---

## P2 · island 组文件夹 + 单成员 pin（2026-11-04 设计确认，实现未开始）

**状态：** 设计已确认；本轮不实现，不改 island 数据结构。视觉稿：
`docs/mockups/2026-11-04-island-group-folder.html` / `.png`；决策说明：
`.superpowers/sdd/2026-11-03-widget-groups/P2-design-options.md`。

确认的 7 项：

1. **折叠组 tile**：44×44；内部 2×2 成员图标网格；≤4 全显示，5+ 显示前 3 个 +
   `+N`（N=count-3）；不使用单个 Folder 图标；不显示数量角标。
2. **展开面板**：island 胶囊本身向右延伸，不是独立 bubble/popup；左侧 tiles 不动，
   右侧同一背景/边框/阴影内放 4 列成员网格（1px 内缝，无内圆角、无 pointer notch）；
   总宽 312 DIP（300–340 范围）；4×66 单元 + 8 间距 + 24 内边距；6 成员 2 行；
   9–12 成员 3 行；更多内部滚动。
3. **右侧空间不足**：整条展开后的 island 左移/clamp 并保持 16 DIP 工作区边距，
   左侧 tiles 和成员区一起移动；v1 不翻到左侧，也不拆成独立 bubble。
4. **单成员 pin**：每个成员独立 `WidgetData.IsPanelPinned`；组内任一 pinned →
   组窗口失焦不收起；全部未 pinned → 收起；组窗口 header pin 只操作 active 成员；
   组级 `WidgetGroup.IsPanelPinned` 变派生/兼容字段。
5. **open 环**：只在展开面板里标记 active 成员；组窗口继续使用 2px tab 下划线；
   折叠 tile 不显示成员色环。
6. **pin 图标**：展开面板里 pinned 成员常显金色直立 pin；未 pinned 默认无图标，
   hover 显示灰色 45° pin；折叠 tile 不显示 pin。
7. **组打开提示**：折叠 tile 外加 1.5px 中性色细轮廓；不指向任何成员。

实现顺序（P3，待用户另行派发）：先扩展 `WidgetGroupTileInfo` 为成员投影
（kind/color/order/pin + overflow），再让 `IslandWindow` 现有 capsule 向右扩展并
内嵌 4 列成员网格（不要新建独立的 `GroupMemberPanel` Window/Popup），最后把 pin
规则从组级改为成员级；merge/detach/dissolve 复用 P0-B 的 in-place 刷新。实现前必须
重新确认 `WidgetGroup.IsPanelPinned` 的兼容迁移与 island 数据投影。
