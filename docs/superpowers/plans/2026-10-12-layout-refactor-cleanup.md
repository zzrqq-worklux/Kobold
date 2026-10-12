# Kobold 布局重构清理（终审 triage 批 A/B）实施计划

> **状态**：**已获用户批准（2026-10-12）**——批 A（triage 1+3+4）与批 B（triage 2）范围已定；
> `CellSize(scale)` 采用"零行为变化"语义（仅 Ceiling，clamp 留在调用点，Migrate 维持不 clamp）；
> triage 5（XamlLoadCheck 拆分）明确不在本轮（仅 3 行机械改签名，拆分不成比例）。
> 执行方式：subagent-driven-development（每任务 = 实现子代理 TDD → 暂存不提交 → 控制者快照 + 审查包 →
> 修复循环 → 复审通过后由实现子代理按计划提交信息提交；冒烟由用户操作）。
> 本计划**不属于** `2026-09-28-widget-plugins` 与 `2026-10-10-widget-adaptive-layout` 计划。
> worktree：`.worktrees/layout-refactor-cleanup`；分支：`layout-refactor-cleanup`（基线 420d583）。

**Goal：** 消化 2026-10-10 计划终审 triage 的逻辑重构建议：归一 folder 格尺寸计算、
todo 布局 metrics 单次计算、列表行高常量复用、菜单未用参数删除；全部零行为变化。

**Architecture：** 纯内部抽取与常量替换，无新文件、无新依赖、无公开 API 语义变化：
`PanelLayoutAdapter` 增私有 `CellSize(scale)`；`TodoCalendarView` 增私有 `TodoMetrics` + `ComputeMetrics(today)`，
三条布局路径（Refresh / ApplyResizeLayout / SplitterDragMoved）共用；`WidgetMenuBuilder.Build` 删签名参数
（两参形式仅内部与自检使用，改动经编译期强制）。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；Newtonsoft.Json；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵）。

**Spec：** 无独立 spec（bounded 澄清直接进本计划）。需求来源：2026-10-10 终审 triage 的
"重构建议"摘要（5 条中取 4 条）与本计划"决策记录"。

## Global Constraints

- C# 7.3 / net48，不引入新依赖；构建 0 警告。
- **零行为变化**：所有现有 pin 的断言语义不动（Task R1 仅因签名删除机械更新 5 处调用点 = 2 生产 + 3 自检）；
  不改任何常量值、不改任何公式。
- TDD：R1 的红 = 自检先改两参调用 → 编译失败（实测 CS7036；均为真实编译失败，本文档已勘误）；
  R2 的红 = 反射 pin 先调用尚不存在的
  `ComputeMetrics` → 运行时失败。红必须在实现前真实跑出并留档。
- 不新增自检工程、不新增文件（除本计划）；`tools/Run-Checks.ps1` 无需改动。
- 不驱动用户真实鼠标；离屏 pin + 反射即可复现。
- 每个提交独立可编译、可回退；提交需任务审查通过后由实现子代理按 pathspec 执行。
- 全量矩阵 `Run-Checks.ps1 -Group all`（31 行）由控制者在整支终审前跑；config-pin 失败先排除
  "正在使用 Kobold" 的并发误写，重跑确认。
- 推送/合并/发布必须先问用户；版本/打包/Release 不在本计划；无用户可见变化，CHANGELOG 不加条目。
- 探针组件 `sdd-layout-probe` 保留勿动；`.worktrees/widget-plugins` 保留勿动。

## 决策记录（2026-10-12 用户确认）

1. `CellSize(scale)` 只做两式 `Math.Ceiling`，**不**在 helper 内 clamp；`AdaptFolder`/`MinFolderSize`
   在调用处显式 `Clamp`，`MigrateFolderSize` 维持不 clamp（对越界旧值零行为变化）。
2. XamlLoadCheck 拆分不在本轮。

---

## Task R1: 批 A——CellSize 归一 + LIST_ROW_H + 菜单 kind 参数删除

**Files：**
- Modify: `Core/PanelLayoutAdapter.cs`（增 `CellSize`；替换 `AdaptFolder` 171-173、`MinFolderSize` 194-196、`MigrateFolderSize` 270-271）
- Modify: `Controls/TodoCalendarView.xaml.cs:200`
- Modify: `Helpers/WidgetMenuBuilder.cs:26`
- Modify: `Controls/TodoWidget.xaml.cs:77`、`Controls/FolderWidget.Interactions.cs:136`
- Test: `tests/XamlLoadCheck/Program.cs:1520-1522, 1564, 4263`

**Interfaces：**
- Produces: `WidgetMenuBuilder.Build(IWidgetWindow host, Action onClosed)` —— 唯一重载，`WidgetKind kind` 参数不存在。
- Produces: `private static (double w, double h) PanelLayoutAdapter.CellSize(double scale)`（仅 Ceiling）。

- [ ] **Step 1（红）：** 先改自检调用点：`tests/XamlLoadCheck/Program.cs`
  - :1520 删除 `var kind = WidgetKindRegistry.Find("todo");`
  - :1522 → `var menu = WidgetMenuBuilder.Build(widget, null);`
  - :1564 → `var reopened = WidgetMenuBuilder.Build(widget, null);`
  - :4263 → `var menu = WidgetMenuBuilder.Build(widget, null);`

  运行：`dotnet build tests\XamlLoadCheck\XamlLoadCheck.csproj -c Debug --nologo`
  预期：**编译失败**（`Build` 无两参重载；实测 CS7036——丢实参的规范错误码，原计划预测的 CS1501 已勘误）。
  把完整错误输出交给控制者留档。

- [ ] **Step 2（实现）：** 精确改动：
  (1) `Helpers/WidgetMenuBuilder.cs:26`
  ```csharp
  public static ContextMenu Build(IWidgetWindow host, Action onClosed)
  ```
  (2) 生产调用点：
  - `Controls/TodoWidget.xaml.cs:77` → `WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;`
  - `Controls/FolderWidget.Interactions.cs:136` → `var menu = WidgetMenuBuilder.Build(this, onClosed);`
  - 两文件内 `WidgetKindRegistry` 仅此一处使用，删参后不得有残留引用；`using Kobold.Core;` 保留
    （两文件仍有其他 Core 类型使用）。
  (3) `Core/PanelLayoutAdapter.cs` 在私有 `Clamp` 上方新增：
  ```csharp
  private static (double w, double h) CellSize(double scale)
  {
      return (Math.Ceiling(BASE_CELL_W * scale), Math.Ceiling(BASE_CELL_H * scale));
  }
  ```
  `AdaptFolder` 171-173 行改为：
  ```csharp
  double scale = Clamp(itemScale, MIN_ITEM_SCALE, MAX_ITEM_SCALE);
  var cell = CellSize(scale);

  var layout = new FolderLayout
  {
      Columns = Math.Max(MIN_COLUMNS, (int)Math.Floor(usableW / cell.w)),
      Scale = scale,
      CellWidth = cell.w,
      CellHeight = cell.h
  };
  layout.VisibleRows = Math.Max(1, (int)Math.Floor(usableH / layout.CellHeight));
  ```
  `MinFolderSize` 194-196 行改为：
  ```csharp
  var cell = CellSize(Clamp(itemScale, MIN_ITEM_SCALE, MAX_ITEM_SCALE));
  return (MIN_COLUMNS * cell.w + WidgetConstants.PADDING + CHROME_BORDER_W,
          WidgetConstants.HEADER_HEIGHT + cell.h + WidgetConstants.PADDING);
  ```
  `MigrateFolderSize` 270-271 行改为：
  ```csharp
  var cell = CellSize(itemScale);
  int rows = PanelRows.VisibleRows(itemCount, gridColumns, maxPanelRows);

  double w = gridColumns * cell.w + WidgetConstants.PADDING + CHROME_BORDER_W;
  double h = WidgetConstants.HEADER_HEIGHT + rows * cell.h + WidgetConstants.PADDING
  ```
  （`h` 的第三行"页脚"三元表达式原样保留。）
  (4) `Controls/TodoCalendarView.xaml.cs:200`：
  ```csharp
  if (_addBoxRevealed && dayIsEmpty) h += PanelLayoutAdapter.LIST_ROW_H; // the revealed input row
  ```

- [ ] **Step 3（绿）：**
  - `dotnet run --project tests\AdaptiveLayoutCheck\AdaptiveLayoutCheck.csproj -c Debug --nologo` → 全绿
    （MinFolderSize 156x137/186x154/246x190 与 clamp pins、MigrateFolderSize 270x252/414x192 原值原样）
  - `dotnet run --project tests\XamlLoadCheck\XamlLoadCheck.csproj -c Debug --nologo` → 全绿
  - `dotnet run --project tests\MenuRenderCheck\MenuRenderCheck.csproj -c Debug --nologo` → 全绿（菜单路径）
  - `dotnet build Kobold.csproj -c Debug --nologo` → 0 警告

- [ ] **Step 4（提交，3 笔，按 pathspec；全部审查通过后执行）：**
  1. `refactor(layout): share the folder cell-size maths` → `git add Core/PanelLayoutAdapter.cs`
  2. `refactor(todo): reuse the list row height for the revealed input` → `git add Controls/TodoCalendarView.xaml.cs`
  3. `refactor(menu): drop the unused widget kind parameter` → `git add Helpers/WidgetMenuBuilder.cs Controls/TodoWidget.xaml.cs Controls/FolderWidget.Interactions.cs tests/XamlLoadCheck/Program.cs`

---

## Task R2: 批 B——TodoCalendarView 布局 metrics 单次计算

**Files：**
- Modify: `Controls/TodoCalendarView.xaml.cs`（增 `TodoMetrics` struct + `ComputeMetrics`；`Refresh` / `ApplyResizeLayout` / `SplitterDragMoved` 改用）
- Test: `tests/XamlLoadCheck/Program.cs`（新 pin + 注册）

**Interfaces：**
- Produces: `private TodoMetrics TodoCalendarView.ComputeMetrics(DateTime today)`；私有 struct `TodoMetrics`
  字段（字段名进 pin）：`List<TodoItem> Parents`、`bool DayIsEmpty`、`int NaturalRows`、`int WeekCount`、
  `double ContentH`、`double AddBlock`、`double Cell`、`bool ShowLabels`。

- [ ] **Step 1（红）：** 在 `tests/XamlLoadCheck/Program.cs` 的 Task A4.2 pins 块之后新增 pin
  `TodoMetricsAreComputedOnce` 并注册：
  ```csharp
  failures += TryCheck("todo: the layout metrics compute in one pass", TodoMetricsAreComputedOnce);
  ```
  pin 代码（反射走既有 `ViewPrivateMethod`；fixture 与分隔线 pin 相同，理解已由引擎 pins 锁定）：
  ```csharp
  private static void TodoMetricsAreComputedOnce()
  {
      ThemeManager.Initialize("dark");
      Localization.SetLanguage("zh");
      try
      {
          var data = new TodoData
          {
              PanelWidth = 342,
              PanelHeight = 385,
              SelectedDate = new DateTime(2026, 10, 12)
          };
          var widget = new TodoWidget(data);
          var view = widget.CalendarView;

          var metrics = ViewPrivateMethod("ComputeMetrics")
              .Invoke(view, new object[] { new DateTime(2026, 10, 12) });
          var type = metrics.GetType();
          int weekCount = (int)type.GetField("WeekCount").GetValue(metrics);
          double cell = (double)type.GetField("Cell").GetValue(metrics);

          if (weekCount != 5 || Math.Abs(cell - 36) > 0.01)
          {
              throw new InvalidOperationException(
                  "the metrics fixture computed " + weekCount + " weeks / cell " + cell.ToString("0.##") +
                  ", wanted 5 / 36");
          }
          if (!(bool)type.GetField("ShowLabels").GetValue(metrics))
              throw new InvalidOperationException("zh with a 36-high cell must show the day labels");
          if (!(bool)type.GetField("DayIsEmpty").GetValue(metrics) ||
              (int)type.GetField("NaturalRows").GetValue(metrics) != 0)
              throw new InvalidOperationException("the itemless fixture must read as an empty day");
          if (Math.Abs((double)type.GetField("ContentH").GetValue(metrics) - 343) > 0.01 ||
              Math.Abs((double)type.GetField("AddBlock").GetValue(metrics) - 36) > 0.01)
              throw new InvalidOperationException(
                  "the metrics fixture must carry the 343 content height and the 36 add block");
          if (Math.Abs(DayGridHeight(view) - weekCount * cell) > 0.01)
              throw new InvalidOperationException(
                  "the computed cell does not match the rendered day grid: " +
                  DayGridHeight(view).ToString("0.##") + " vs " + (weekCount * cell).ToString("0.##"));
      }
      finally
      {
          Localization.SetLanguage("en");
      }
  }
  ```
  运行：`dotnet run --project tests\XamlLoadCheck\XamlLoadCheck.csproj -c Debug --nologo`
  预期：**运行时失败**（`ViewPrivateMethod("ComputeMetrics")` 找不到方法）。完整输出留档。

- [ ] **Step 2（实现）：** `Controls/TodoCalendarView.xaml.cs`：
  (1) 在 `EffectiveCellHeight` 之后、`ApplyGeometry` 之前插入 struct 与方法：
  ```csharp
  /// <summary>
  /// The day's rows, the displayed weeks and the height split the three
  /// render paths share (Refresh, the incremental resize, the splitter drag).
  /// </summary>
  private struct TodoMetrics
  {
      public List<TodoItem> Parents;
      public bool DayIsEmpty;
      public int NaturalRows;
      public int WeekCount;
      public double ContentH;
      public double AddBlock;
      public double Cell;
      public bool ShowLabels;
  }

  /// <summary>
  /// One pass over the layout inputs for the current data and panel height;
  /// every render path consumes the same numbers.
  /// </summary>
  private TodoMetrics ComputeMetrics(DateTime today)
  {
      var parents = TodoListOps.ForDay(_data.Items, _selected, today);
      bool dayIsEmpty = parents.Count == 0;
      int naturalRows = CountNaturalRows(parents);
      int weekCount = TodoCalendar.WeekCount(_viewYear, _viewMonth,
          _culture.DateTimeFormat.FirstDayOfWeek);
      double contentH = CurrentContentHeight(weekCount, naturalRows, dayIsEmpty);
      double addBlock = AddBlockHeight(dayIsEmpty);
      double cell = EffectiveCellHeight(weekCount, naturalRows, contentH, addBlock);
      bool showLabels = Localization.CurrentLanguage == "zh"
                        && cell >= PanelLayoutAdapter.LABEL_MIN_CELL;

      return new TodoMetrics
      {
          Parents = parents,
          DayIsEmpty = dayIsEmpty,
          NaturalRows = naturalRows,
          WeekCount = weekCount,
          ContentH = contentH,
          AddBlock = addBlock,
          Cell = cell,
          ShowLabels = showLabels
      };
  }
  ```
  (2) `Refresh` 134-158 的计算段改为（`_culture`/`_accentBrush` 保持在前，顺序不变）：
  ```csharp
  DateTime today = DateTime.Today;
  var metrics = ComputeMetrics(today);

  BuildMonthHeader(today);
  BuildWeekdayHeader();
  BuildCalendar(today, metrics.Cell, metrics.ShowLabels);
  BuildDayList(metrics.Parents, today);
  ApplyGeometry(metrics.WeekCount, metrics.Cell, metrics.NaturalRows, metrics.ContentH, metrics.AddBlock);
  UpdateAddRowVisuals(metrics.DayIsEmpty);
  ```
  （Refresh 尾部的 `SplitterBand`、`DesiredHeight = NaturalHeight(...)` 改用
  `metrics.WeekCount / metrics.NaturalRows / metrics.DayIsEmpty`。）
  (3) `ApplyResizeLayout` 292-302 行改为：
  ```csharp
  DateTime today = DateTime.Today;
  var metrics = ComputeMetrics(today);

  if (metrics.ShowLabels != _builtShowLabels) BuildCalendar(today, metrics.Cell, metrics.ShowLabels);
  ApplyGeometry(metrics.WeekCount, metrics.Cell, metrics.NaturalRows, metrics.ContentH, metrics.AddBlock);
  _builtCellHeight = metrics.Cell;
  ```
  (4) `SplitterDragMoved` 1350-1357 行改为：
  ```csharp
  var metrics = ComputeMetrics(DateTime.Today);
  double maxCell = Math.Min(PanelLayoutAdapter.CELL_MAX,
      MaxCalendarCell(metrics.WeekCount, metrics.ContentH, metrics.AddBlock));
  double cell = _splitStartCell + (localY - _splitStartY) / Math.Max(1, metrics.WeekCount);
  ```

- [ ] **Step 3（绿）：**
  - `dotnet run --project tests\XamlLoadCheck\XamlLoadCheck.csproj -c Debug --nologo` → 全绿（含新 pin）
  - `dotnet run --project tests\AdaptiveLayoutCheck\AdaptiveLayoutCheck.csproj -c Debug --nologo` → 全绿（旁证无回归）
  - `dotnet build Kobold.csproj -c Debug --nologo` → 0 警告

- [ ] **Step 4（提交，1 笔）：**
  `refactor(todo): compute the layout metrics once per pass` →
  `git add Controls/TodoCalendarView.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## 收尾（控制者执行，非任务）

- [ ] 整支终审（requesting-code-review）：diff 420d583..HEAD 全量审查，重点：
  零行为变化证据（build 0 警告、全 pin 绿、三处路径输出一致）、签名删除无残留引用。
- [ ] `tools\Run-Checks.ps1 -Group all`（31 行）全绿；config-pin 误报先重跑确认。
- [ ] 计划文件追加"执行记录"（提交哈希/审查结论/矩阵结果），提交
  `docs: record the refactor cleanup execution`。
- [ ] `tools\deploy.ps1` 部署 → **用户手动冒烟**（folder 图标/尺寸菜单、todo 面板/分隔线、右键菜单各走一遍）。
- [ ] 用户确认后：合并 main + 推送（需用户拍板）；worktree/SDD 工作区按收尾规范清理。

---

## 执行记录（2026-10-12）

**任务审查（每任务先审后交）**

- R1：实现子代理 TDD（红 = 改两参调用点后编译失败 CS7036；绿 = Kobold 构建 0 警告 +
  AdaptiveLayoutCheck/XamlLoadCheck/MenuRenderCheck 全绿，pins 156x137/186x154/246x190、
  270x252/414x192 原值原样）→ 快照 12c646a 审查：Spec ✅ / quality Approved →
  提交 `f595798`（CellSize）、`408be5d`（LIST_ROW_H）、`5dafd49`（菜单 kind 参数删除）。
- R2：实现子代理 TDD（红 = 反射 `ComputeMetrics` 运行时 "not found"；绿 = XamlLoadCheck 含新 pin
  `todo: the layout metrics compute in one pass`、AdaptiveLayoutCheck、构建 0 警告）→
  快照 5b57ac7 审查：Spec ✅ / quality Approved → 提交 `5c3b12d`。
- 两任务快照树与提交树逐字节一致（`git diff <snapshot> HEAD` 为空）。

**整支终审**

- 审查范围 420d583..5c3b12d（计划记录 + 5 提交）；结论：**Ready to merge = Yes**，
  无 Critical/Important；零行为变化四重取证（常量等值 LIST_ROW_H=30、既有 pin 全部原样、
  5/5 调用点覆盖且无残留、抽取保序且新调用 helper 全为纯函数）。
- 终审 minor 处置（全部"可留"）：被删解释性注释未迁移（化妆级）；分隔线每步双算 metrics
  （计划内单工厂设计的固有代价，UI 尺度可忽略）；pin 名 "compute in one pass" 措辞偏强；
  `WidgetMenuBuilder` 文档 "kind-aware" 仍准确（kind 感知改由 `host.Data` 分支承担）。
- 勘误（本记录随附修正）：Global Constraints 原写"3 处调用点"，实为 5（2 生产 + 3 自检）；
  R1 红预期原写 CS1501，实测 CS7036（丢实参的规范错误码，同为真实编译失败）。

**全量矩阵**

- 命令：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all`；
  HEAD `5c3b12d`；结果 **31/31 全绿**（29 控制台自检 + installer + config-pin），
  末行 `All 31 check(s) passed.`；一次通过，无 config-pin 并发误报。
- 构建：`dotnet build Kobold.csproj -c Debug` 0 警告 / 0 错误（两任务 Step 3 各复跑一次）。

**手动冒烟（用户操作）**

- 部署 `tools\deploy.ps1`（Release）后由用户冒烟：folder 图标/尺寸菜单、todo 面板/分隔线、
  右键菜单；结果待用户确认后补记（未确认前不视为通过）。

**未验证项（如实声明）**

- 本计划零用户可见变化；混合 DPI/多屏验收沿用项目既有单屏限制声明。
- SDD 工作区 ledger（`.superpowers/sdd/2026-10-12-layout-refactor-cleanup/progress.md`）含
  全部 Ruling 与 deferred minors，供终审 triage；工作区在收尾后按规范清理。
