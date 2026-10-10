# Kobold 组件面板自由缩放与自适应布局（方向 A）实施计划（草案）

> **状态**：草案 — 2026-10-10 起草，**待用户审阅**；**依赖** `docs/superpowers/specs/2026-10-10-widget-adaptive-layout-design.md`
> 先获批准（含 §9 决策点），批准后本计划再细化/校正，然后按 subagent-driven-development 执行。
> 本计划**不属于** `2026-09-28-widget-plugins` 计划。

**Goal：** 组件面板（folder / todo）原生自由拖动缩放 + 通用自适应引擎；取代文件夹 列数/图标大小/
最大行数 手动设置（旧字段作迁移输入）；Todo 日历第二行小字按尺寸显隐。

**Architecture：** `Core` 新增纯函数 `PanelLayoutAdapter`（尺寸 → 列/尺度/行数/格高/显隐 + 迁移换算，
复用 `PanelRows`/`WidgetConstants`/既有格高字号常量）；`WidgetData` 新增 `PanelWidth/PanelHeight`；
`WidgetChrome` 承载缩放手柄、`WidgetWindowBase` 承载缩放协议与持久化；两类组件分别接入引擎输出。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；Newtonsoft.Json；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵）。

## Global Constraints

- C# 7.3 / net48，不引入新 NuGet 依赖。
- TDD：每个任务先写失败自检（**编译失败也算红**）→ 最小实现 → 自检与全量回归全绿；提交需审查通过。
- 新自检项目必须登记进 `tools/Run-Checks.ps1`（CI 子集标记照旧）。
- 三语 key 同步；`LangCheck` 保持绿；死键删除同步三语。
- 不驱动用户真实鼠标做复现（离屏 pin + 插桩日志）。
- 每个提交独立可回退；阶段 2/3/4 各自手动冒烟；混合 DPI/多屏受单屏限制，如实声明。
- 推送/合并必须先问用户；版本/打包/Release 不在本计划。
- 用户的设计调整优先于计划草图；新需求先与用户确认方案再实施。

---

## 阶段 1：纯自适应引擎与迁移函数（应用行为不变）

### Task A1.1: PanelLayoutAdapter + Throttle 常量 + 自检

**Files:**
- Create: `Core/PanelLayoutAdapter.cs`（纯函数 + `FolderLayout`/`TodoLayout` 结构）
- Create: `tests/AdaptiveLayoutCheck/`（新自检工程，照抄既有模板，`Ci=$true`）
- Modify: `tools/Run-Checks.ps1`（登记新项）

- [ ] **Step 1: 写失败自检**（关键用例）：
  - folder：narrow 面板 → `columns==2`、`scale==0.9`（clamp）；宽 340 → 列/尺度符合公式；
    `visibleRows` 边界（1 行 / 多行 / 高度不足）；`scrolls` 与 `PanelRows` 语义一致；
    宽度增大 → `columns` 单调不减；
  - todo：`cellH` clamp（22/36）；`ShowDayLabels` 阈值（31.9 隐藏 / 32 显示）；内容宽 <260 隐藏；
    `ListRows` clamp（1..6）；
  - 迁移：`MigrateFolderSize(3,3,5,1.1)` / `(4,1,30,1.3)`（含截断页脚）公式核对；
    `DefaultTodoSize`。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（§spec 4.2/4.3/4.4 的公式；`PanelRows` 复用原样）。
- [ ] **Step 4: 自检转绿 + 全量回归**（`Run-Checks.ps1 -Group all` 全绿、build 0 警告）。
- [ ] **Step 5: 提交** → `feat(layout): add the pure adaptive layout engine`

---

## 阶段 2：缩放交互与尺寸持久化（两类组件同时获得）

### Task A2.1: 尺寸字段 + 迁移接线

**Files:**
- Modify: `Core/WidgetData.cs`（`double? PanelWidth/PanelHeight`）、`Core/AppConfig.cs`（迁移调用）、
  `Core/FolderData.cs`/`Core/TodoData.cs`（新组件默认尺寸，如放工厂）
- Modify: `tests/WidgetKindsCheck/Program.cs`（迁移 pin：旧字段 → 尺寸、只算一次、写回）

- [ ] **Step 1: 写失败自检**：无尺寸的老配置加载后 folder/todo 均获得迁移尺寸并写回；
  已有 `PanelWidth/PanelHeight` 的配置不被覆盖；仅 folder 有旧字段的混合配置各自迁移。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（`Load` 内一次性填充；`CreateDefault` 给新组件默认尺寸；旧字段保留写出）。
- [ ] **Step 4: 自检绿 + 全量回归**。
- [ ] **Step 5: 提交** → `feat(layout): persist per-widget panel sizes with legacy migration`

### Task A2.2: 缩放手柄 + 缩放协议 + 持久化

**Files:**
- Modify: `Controls/WidgetChrome.xaml(.cs)`（四边隐形带 + 右下角 grip、光标、事件转发）、
  `Controls/WidgetWindowBase.cs`（缩放循环：capture/物理增量÷DPI/clamp/实时重排/松手
  `FitPanelToWorkArea` + 写回 + 防抖保存）、`Controls/IWidgetWindow.cs`（如需）
- Modify: `tests/XamlLoadCheck/Program.cs`（手柄/光标/锁定/拖动步进/持久化 pins）

- [ ] **Step 1: 写失败自检**：手柄存在与光标映射（四边/角）；`IsLocked` 时不参与命中；
  合成拖动（增量×DPI）→ `Width/Height` 按 clamp 变化且内容重排；松手后 `PanelWidth/PanelHeight`
  已写回、尺寸落在工作区内（离屏 pin，不驱动真实鼠标）。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（协议与 Header 拖动一致；尺寸上下限按 spec 3.2/实现定值）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟**（用户）：拖动观感、最小/最大、锁定、重启恢复、
  主题/透明度、folder 与 todo 全交互回归。
- [ ] **Step 5: 提交** → `feat(layout): add native panel resizing with size persistence`

---

## 阶段 3：FolderWidget 接入引擎

### Task A3.1: 菜单项移除 + 渲染接线

**Files:**
- Modify: `Controls/FolderWidget.Theme.cs`/`FolderWidget.xaml.cs`（`CalculatePanelSize` 退出；
  `GetContentSize` 用持久化尺寸；`BindableUniformGrid.Columns` 与 `LayoutTransform` 尺度由引擎给）、
  `Helpers/WidgetMenuBuilder.cs`（移除 列数/大小/行数 区）、`Core/WidgetKindRegistry.cs`
  （`SupportsGridColumns/SupportsItemScale/SupportsMaxRows` 退休）、`Core/Localization.cs`（死键删除，三语）
- Modify: `tests/WidgetKindsCheck`、`tests/MenuRenderCheck`、`tests/XamlLoadCheck`（断言跟进）

- [ ] **Step 1: 失败自检**：菜单不再含三个布局入口；能力标志退休后的断言更新；引擎接入后
  不同面板尺寸 → 列数/尺度/行数按公式（对照 A1.1）。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（渲染路径单一来源；旧字段不再消费）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟**：文件夹全清单（开合/浏览/拖拽/收纳/颜色/锁定/置顶/
  透明度/DPI 恢复/菜单其余项）+ 缩放后观感（含 P1/P2 档位）。
- [ ] **Step 5: 提交** → `feat(folder): adapt the folder panel to free resizing`

---

## 阶段 4：TodoWidget 接入引擎

### Task A4.1: 日历/清单/小字自适应

**Files:**
- Modify: `Controls/TodoCalendarView.xaml(.cs)`（格高/清单行数由引擎输出；`BuildDayCellLabel` 按
  `ShowDayLabels` 显隐；`RequestContentResize` 自动尺寸移除）、`Controls/TodoWidget.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`（小字显隐随尺寸、清单行数、面板尺寸稳定 pins）

- [ ] **Step 1: 写失败自检**：小尺寸 → 无第二行小字；大尺寸 → 有；清单行数随高度；换日/增删条目
  不再改变面板尺寸（尺寸稳定 pin）。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（引擎输出接入；zh 判定与数据可用性沿用；语义行为不动）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟**：Todo 全清单（顺延/完成/子待办/编辑/右键盘菜单/
  换月/今天/持久化）+ 缩放观感（小字显隐、密度）。
- [ ] **Step 5: 提交** → `feat(todo): adapt the todo panel and day labels to free resizing`

---

## 阶段 5：收尾

### Task A5.1: README + CHANGELOG

- README（英中同步）：面板自由缩放与自适应；旧设置（列数/图标大小/最大行数）移除说明；
  数据兼容（新字段、旧字段保留）。
- CHANGELOG「Unreleased」：自由缩放、自适应、Todo 小字显隐；行为变化一句（面板不再自动收放）。
- 提交 → `docs: document the adaptive panel resizing`

### Task A5.2: 全量回归 + 执行记录 + 终审

- `Run-Checks.ps1 -Group all` 全绿；计划「执行记录」写入手动验收结果与未验证项；
  终审（整分支，含 ledger minor/裁决清单）→ `finishing-a-development-branch`。

---

## Self-Review

- **Spec 覆盖**：spec §3 交互=A2.2；§3.3 持久化/迁移=A2.1；§4 引擎=A1.1；§4.2/4.3 接线=A3.1/A4.1；
  §4.4 迁移公式=A1.1+A2.1；§6 测试=各任务；§8 阶段=本计划五阶段。
- **占位符扫描**：无 TBD 签名；数值临界（MAX_CELL_W≈112、小字阈值 32、最小尺寸）标注"实现定值 + pin"，
  由 spec §9 决策点与冒烟确定。
- **类型一致性**：`PanelLayoutAdapter` 的输出结构在 A1.1 定义，A3.1/A4.1 消费；
  `PanelWidth/PanelHeight` 在 A2.1 定义，A2.2 写、A3.1/A4.1 读。
- **风险**：阶段 3 触碰现有文件夹布局（菜单项移除是用户可感知变化）——单独提交 + 冒烟 + README；
  回退点 = 阶段 3 之前。
