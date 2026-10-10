# Kobold 组件面板自由缩放与自适应布局（方向 A）实施计划

> **状态**：**已获用户批准（2026-10-10）**——spec §9 七个决策点已拍板（见 spec "决策记录"），本计划按该口径执行。
> 执行方式：subagent-driven-development（每任务 = 实现子代理 TDD → 暂存不提交 → 控制者快照 + 审查包 →
> 修复循环 → 复审通过后由实现子代理按计划提交信息提交；冒烟由用户操作）。
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
- **测试卫生（阶段 0）**：重定向落地前**不得运行** XamlLoadCheck / MenuRenderCheck / WindowSwitcherCheck
  的旧二进制——它们会写真实 `%APPDATA%\Kobold\config.json`；阶段 0 的 red 用"引用尚不存在的注入点 →
  编译失败"取证，**绝不以污染真实文件的方式取证**。
- `Run-Checks.ps1 -Group checks/all` 前后对比真实 `config.json`(+`.backup`) 的 SHA256（矩阵 wrapper pin）；
  失败时先排除"Kobold 正在使用并自行写盘"（并发误报），再查检查项回归。
- 新自检项目必须登记进 `tools/Run-Checks.ps1`（CI 子集标记照旧）。
- 三语 key 同步；`LangCheck` 保持绿；死键删除同步三语。
- 不驱动用户真实鼠标做复现（离屏 pin + 插桩日志）。
- 每个提交独立可回退；阶段 2/3/4 各自手动冒烟；混合 DPI/多屏受单屏限制，如实声明。
- 推送/合并必须先问用户；版本/打包/Release 不在本计划。
- 用户的设计调整优先于计划草图；新需求先与用户确认方案再实施。

---

## 阶段 0：测试卫生——自检矩阵不写真实配置（用户要求先做）

### Task A0.1: 单例配置重定向 + 矩阵字节 pin

**Files:**
- Modify: `Core/WidgetManager.cs`（测试注入点，生产路径不变）、`tests/XamlLoadCheck/Program.cs`
  （Main 顶部重定向 + 进程尾三层 pin）、`tests/MenuRenderCheck/Program.cs`、`tests/WindowSwitcherCheck/Program.cs`
  （各自重定向）、`tools/Run-Checks.ps1`（矩阵前后 SHA256 对比）、`.gitignore`（`.superpowers/`）

**根因（已核实）**：`XamlLoadCheck` 的 `TodoSizeRefreshIsNotStale` → `SelectDay` → `PersistSelection` →
`WidgetManager.Instance.SaveConfig()` → 1 秒 debounce 落盘；单例构造走无参 `AppConfig.Load()`，写真实
`%APPDATA%\Kobold\config.json`（含 `"Folders": null` 降级标记，≤1.3.0 读不了 → 证据副本 + 备份恢复）。
`MenuRenderCheck` / `WindowSwitcherCheck` 也构造单例（只读加载；真实配置缺失/损坏时 `Load` 会写证据副本
或创建默认配置）。

- [ ] **Step 1: 写失败自检**（编译红；**不得运行未重定向的旧二进制**）：
  - XamlLoadCheck 新增：调用尚不存在的 `WidgetManager.OverrideConfigPathsForChecks(main, backup)` →
    编译失败即红；pin 逻辑（真实 config+backup 的字节快照对比、强制 `Config.Save()` 断言落沙盒、
    比较逻辑自证用例：改临时文件必检出、不改必通过）同批写入。
  - 注入点守卫 pin：`Instance` 已初始化后再调用必须抛 `InvalidOperationException`。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**：
  - 注入点：`public static void OverrideConfigPathsForChecks(string configPath, string backupPath)`；
    首次 `Instance` 前生效，构造时走 `AppConfig.Load(override…)`；生产默认路径不变；已初始化后调用抛错。
  - 三个 WPF 检查在 Main 顶部（碰单例前）把配置/备份重定向到 `%TEMP%\kobold-check-<name>\`（每次启动清建）。
  - XamlLoadCheck 进程尾：强制 `WidgetManager.Instance.Config.Save()`；断言沙盒 config 已生成、
    真实 config/backup 与启动快照逐字节一致（含"不存在→仍不存在"）。
  - `Run-Checks.ps1`：`-Group checks/all`（非 `-List`）时对真实 config+backup 做 SHA256 前后对比，
    变化即失败；失败信息注明"若期间使用过 Kobold 则可能为并发写入，重跑确认"。
  - `.gitignore`：加 `.superpowers/`（SDD 工作区，已转为正式流程）。
- [ ] **Step 4: 自检绿 + 全量回归**：三个 WPF 检查单跑绿；`Run-Checks.ps1 -Group all` 29/29 + wrapper pin 绿；
  真实配置字节在整个矩阵前后不变。
- [ ] **Step 5: 提交**（两笔，先审查后提交）→
  `docs: record the approved decisions and the matrix-hygiene task`（spec/plan，控制者已修订暂存）→
  `fix(tests): keep the self-check matrix off the real config`。
- [ ] **Step 6: 冒烟（用户）**：跑一次 `Run-Checks.ps1 -Group all` 确认全绿且配置未被改写；
  是否把此提交先合入 main 由用户拍板（避免 main 工作区继续踩坑）。

---

## 阶段 1：纯自适应引擎与迁移函数（应用行为不变）

### Task A1.1: PanelLayoutAdapter + 布局常量 + 自检

**Files:**
- Create: `Core/PanelLayoutAdapter.cs`（纯函数 + `FolderLayout`/`TodoLayout` 结构）
- Create: `tests/AdaptiveLayoutCheck/`（新自检工程，照抄既有模板，`Ci=$true`）
- Modify: `tools/Run-Checks.ps1`（登记新项）

- [ ] **Step 1: 写失败自检**（关键用例；常量已定：MAX_CELL_W=112、scale clamp 0.9..1.45、
  cellW=ceil(76×scale)、cellH=ceil(89×scale)、todo 格高 22..36、小字阈值 32、todo 固定块 133）：
  - folder：narrow（usableW < 137）→ `columns==2`、`scale==0.9`（clamp）；宽 340 → `columns==3` 且尺度按公式；
    usableW 224/225 临界 → 2→3 列且尺度回落（1.45 → ≈0.99）；`visibleRows` 边界（1 行 / 多行 / 高度不足）；
    `scrolls` 与 `PanelRows` 语义一致；宽度增大 → `columns` 单调不减；列数下限恒 ≥2；
  - todo：`cellH` clamp（22/36）；`ShowDayLabels`（31.9 隐藏 / 32 显示；与宽度无关——无宽度兜底）；
    `ListRows` clamp（0..6）；清单优先（自然行不满时月历获得余量）与月历 22 下限保护（清单收缩）；
    `DefaultTodoSize`（5 周/1 行 = 343、5 周/0 行 = 313）；
  - 迁移：`MigrateFolderSize(3,3,5,1.1)` → 270×252 / `(4,1,30,1.3)` → 414×192（含截断页脚 +20）。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（§spec 4.2/4.3/4.4 的公式；布局常量收敛进引擎——含 todo 固定块 133 的分解常量；
  `PanelRows` 复用原样）。
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
- [ ] **Step 3: 实现**（`Load` 内一次性填充；`CreateDefault` 给新组件默认尺寸——folder = 3 列 × ≤3 行 ×
  1.1 倍换算、todo = 342 × 自然高度；旧字段保留写出）。
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
- [ ] **Step 3: 实现**（协议与 Header 拖动一致，遵守 r19 顺序不变量：capture → active → Started；
  尺寸上下限实现定值并 pin：folder 最小 180×137（2 列×1 行 @0.9，含截断页脚时留页脚高）、
  todo 最小 = 40+133+6×22+30 = 335（宽 342）；最大 = 工作区）。
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
  透明度/DPI 恢复/菜单其余项）+ 缩放后观感（P1 档位回落）。
- [ ] **Step 5: 提交** → `feat(folder): adapt the folder panel to free resizing`

---

## 阶段 4：TodoWidget 接入引擎

### Task A4.1: 日历/清单/小字自适应

**Files:**
- Modify: `Controls/TodoCalendarView.xaml(.cs)`（格高/清单行数由引擎输出；`BuildDayCellLabel` 按
  `ShowDayLabels` 显隐；`RequestContentResize` 自动尺寸移除）、`Controls/TodoWidget.xaml.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`（小字显隐随尺寸、清单行数、面板尺寸稳定 pins）

- [ ] **Step 1: 写失败自检**：格高 28（迁移默认）→ 无第二行小字；格高 ≥32 → 有；清单行数随高度；
  换日/增删条目不再改变面板尺寸（尺寸稳定 pin）；`DesiredHeight` 与引擎公式（固定块 133 等）一致性 pin；
  无宽度兜底分支。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（引擎输出接入；渲染侧消费引擎常量——格高/行高/固定块分解；zh 判定与数据可用性沿用；
  语义行为不动）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟**：Todo 全清单（顺延/完成/子待办/编辑/右键盘菜单/
  换月/今天/持久化）+ 缩放观感（小字显隐、密度）。
- [ ] **Step 5: 提交** → `feat(todo): adapt the todo panel and day labels to free resizing`

---

## 阶段 5：收尾

### Task A5.1: README + CHANGELOG

- README（英中同步）：面板自由缩放与自适应；旧设置（列数/图标大小/最大行数）移除说明；
  数据兼容（新字段、旧字段保留）；行为变化：面板不再随内容自动收放；zh 第二行小字在面板
  足够高（格高 ≥32）时显示、迁移后默认隐藏（拖高即现）。
- CHANGELOG「Unreleased」：自由缩放、自适应、Todo 小字按尺寸显隐；行为变化一句（面板不再自动收放）。
- 提交 → `docs: document the adaptive panel resizing`

### Task A5.2: 全量回归 + 执行记录 + 终审

- `Run-Checks.ps1 -Group all` 全绿；计划「执行记录」写入手动验收结果与未验证项；
  终审（整分支，含 ledger minor/裁决清单）→ `finishing-a-development-branch`。

---

## Self-Review

- **Spec 覆盖**：spec §3 交互=A2.2；§3.3 持久化/迁移=A2.1；§4 引擎=A1.1；§4.2/4.3 接线=A3.1/A4.1；
  §4.4 迁移公式=A1.1+A2.1；§6 测试=各任务；§8 阶段=本计划六阶段（0-5，阶段 0 为测试卫生）。
- **占位符扫描**：无 TBD；关键数值已定（MAX_CELL_W=112、scale 0.9..1.45、小字阈值 32、格高 22..36），
  最小尺寸为"实现定值 + pin"，观感微调留待用户冒烟。
- **类型一致性**：`PanelLayoutAdapter` 的输出结构在 A1.1 定义，A3.1/A4.1 消费；
  `PanelWidth/PanelHeight` 在 A2.1 定义，A2.2 写、A3.1/A4.1 读。
- **风险**：阶段 3 触碰现有文件夹布局（菜单项移除是用户可感知变化）——单独提交 + 冒烟 + README；
  回退点 = 阶段 3 之前。
