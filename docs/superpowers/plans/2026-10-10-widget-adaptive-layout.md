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
- Create: `Core/PanelSizeMigration.cs`（单一来源：按 kind 填充缺失的 `PanelWidth/PanelHeight`——
  folder 用 `MigrateFolderSize`、todo 用 `DefaultTodoSize`（周数 = 选中月，culture = 语言映射；
  行数 = 当日顶层 + 子行数））
- Modify: `Core/WidgetData.cs`（`double? PanelWidth/PanelHeight`）、`Core/AppConfig.cs`（Load 末尾逐 widget 填充；
  非 defaulted 不强制写盘）、`Core/WidgetManager.cs`（`CreateWidget` 对新组件调用同一填充）、
  `Core/TodoCalendar.cs`（新增 `CultureForLanguage` 与 `WeekCount`，`BuildMonth` 复用 `WeekCount`）、
  `Controls/TodoCalendarView.xaml.cs`（改用 `TodoCalendar.CultureForLanguage`，删除私有副本）
- Modify: `tests/WidgetKindsCheck/Program.cs`（迁移 pin）、`tests/TodoCalendarCheck/Program.cs`
  （`WeekCount`/`CultureForLanguage` pins）

- [ ] **Step 1: 写失败自检**：无尺寸的老配置加载后 folder/todo 各自获得迁移尺寸（逐字段：缺失才补、
  已存在保留）；已有成对尺寸不被覆盖；混合配置各自迁移；未知 kind 不填充；默认配置的 folder 获得默认尺寸；
  非 defaulted 的 Load 不写盘、显式 Save 后尺寸写回；`WeekCount`（4/5/6 周与 culture 对照）与
  `CultureForLanguage`（zh/ja/en/未知）pins。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（`Load` 内一次性填充（内存，靠既有保存路径写回）；`CreateWidget` 对新组件填充
  ——folder = 3 列 × ≤3 行 × ItemScale 换算、todo = 342 × 选中月自然高度；旧字段保留写出）。
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
  尺度/列数由引擎给、随尺寸实时重排；`GetContentSize` 退回引擎默认尺寸；去自动收放）、
  `Controls/WidgetWindowBase.cs`（新增 `OnPanelSizeChanged()` 钩子并在缩放移动中调用）、
  `Helpers/WidgetMenuBuilder.cs`（移除 列数/大小/行数 区）、`Core/WidgetKindRegistry.cs`
  （`SupportsGridColumns/SupportsItemScale/SupportsMaxRows` 退休）、`Core/Localization.cs`（死键删除，三语）
- Modify: `tests/WidgetKindsCheck`、`tests/XamlLoadCheck`（断言跟进；`tests/MenuRenderCheck` 预期无改动，验证即可）

- [ ] **Step 1: 失败自检**：菜单不再含三个布局入口；能力标志退休后的断言更新；引擎接入后
  不同面板尺寸 → 列数/尺度/行数按公式（对照 A1.1）；**无水平裁切 pin（用户冒烟反馈）**：
  最小尺寸 180×137 → 2 列 × 69 完整落在内容宽 164 内；用户实际尺寸 433 宽 → 4 列（总宽 ≤ 内容宽），
  渲染网格消费引擎输出（`BindableUniformGrid.Columns` 与尺度不再来自旧字段）。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（渲染路径单一来源；旧字段不再消费）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟**：文件夹全清单（开合/浏览/拖拽/收纳/颜色/锁定/置顶/
  透明度/DPI 恢复/菜单其余项）+ 缩放后观感（P1 档位回落）。
- [ ] **Step 5: 提交** → `feat(folder): adapt the folder panel to free resizing`

### Task A3.2: 图标大小回归与动态最小尺寸（P3；2026-10-10 用户冒烟后追加）

用户冒烟否决 P1（图标随宽放大）：图标大小保留为用户可设置、面板变宽只增图标间距、能容一列时自动加列
（列数仍全自动、最大行数仍移除）。spec §9-3/§4.2/§3.2 已改写；本节取代 A3.1 的 P1 尺度语义。

**Files:**
- Modify: `Core/PanelLayoutAdapter.cs`（`AdaptFolder` 增 `itemScale` 入参 → `scale = clamp(itemScale, 0.9, 1.5)`
  恒定；`columns = max(2, floor(usableW / ceil(76×scale)))`；新增 `MinFolderSize(itemScale)`；
  删除 `MAX_CELL_W`；`MIN_SCALE/MAX_SCALE` → `MIN_ITEM_SCALE/MAX_ITEM_SCALE`；`MIGRATION_BORDER_W` →
  `CHROME_BORDER_W`）
- Modify: `Controls/WidgetWindowBase.cs`（`GetMinPanelSize` → `protected virtual`）、
  `Controls/FolderWidget.xaml.cs`（`ApplyAdaptiveLayout` 传 `GetItemScale()`；override `GetMinPanelSize` →
  `MinFolderSize`；新增 `public void ApplyItemScaleChange()`——`IsPanelOpen` 且小于新下限 → 放大到下限 +
  `FitPanelToWorkArea`，随后 `UpdateUI()`）
- Modify: `Helpers/WidgetMenuBuilder.cs`（恢复「图标大小」菜单：`host.Data is FolderData` 挂
  `CreateItemSizeMenu()`；`ApplyItemScale` 对 folder 面板调 `ApplyItemScaleChange()`）
- Modify: `Core/Localization.cs`（从 A3.1 前提交恢复 `Menu_ItemSize` + `Size_*` 7 键 × 三语；
  `Menu_GridSize/Menu_Columns/Menu_MaxRows/Menu_Rows` 保持删除）
- Modify: `tests/AdaptiveLayoutCheck/Program.cs`（A1.1 folder pins 按 P3 重写、不得弱化其它断言；
  新增 `MinFolderSize`/临界加列/恒定 scale pins）、`tests/XamlLoadCheck/Program.cs`（folder pins：
  动态最小尺寸、菜单回归、调档放大、变宽增间距、readapt 数值）

- [ ] **Step 1: 写失败自检**（编译红 = 新签名/`MinFolderSize` 未存在；运行时红 = 菜单、动态下限、调档放大）：
  - 引擎：clamp 0.5→0.9、2.0→1.5；恒定 scale 跨宽；@1.1 临界加列（可用宽 419→4 列 / 420→5 列；
    @1.5：342→3 列）；`MinFolderSize`：1.1→186×154、0.9→156×137、1.5→246×190；
    无 cap：`AdaptFolder(150, 616, 14, 0.9)` → 7 可见行（>6）不滚动；最小尺寸处 2 列且无水平裁切；
  - XamlLoadCheck（沙盒 ItemScale 1.1）：433 宽 → 4 列、ScaleX==1.1、334.4 ≤ 415；186×154 → 2 列、
    ScaleX==1.1、167.2 ≤ 168；433/435 同 4 列且首图标 72 宽不变、网格更宽；持久值/拖动下限 →
    186×154；菜单唯一「图标大小」子菜单 6 项、勾选默认、点 1.5 写配置；开面板 186×154 + 档位 1.5 →
    246×190 + ScaleX 1.5 + 2 列；关闭面板不被直接改尺寸（`GetPanelSize()` 读时钳到 246×190）。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（spec §4.2 P3 公式；`ApplyItemScaleChange` = 唯一自动改尺寸例外；三语同步）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟（用户）**：档位切换、变宽只增间距、临界加列、最小尺寸
      随档位、菜单勾选、调档放大已开面板。
- [ ] **Step 5: 提交（两笔，先审查后提交）** →
      `docs: pin the folder P3 layout and the restored item-size menu`（spec/plan，控制者已修订暂存）→
      `feat(folder): render the grid at the user-set icon size`

### Task A3.3: 行距不随面板高度拉伸（2026-10-12 用户冒烟反馈）

用户冒烟反馈：folder 拉高后图标行的上下间距被拉伸。修法：图标网格不再纵向拉伸——行保持固定行距
（`ceil` 前的基础行高 89 × 档位）从上往下紧排，多出的高度留在面板下方，能完整多放一行时自动加行；
横向 P3 行为（变宽只增间距/临界加列）不变。

**Files:**
- Modify: `Controls/FolderWidget.xaml`（`ItemsPanelTemplate` 的 `BindableUniformGrid` 加
  `VerticalAlignment="Top"`；保持 ItemsControl 自身拉伸，命中面/套索行为不变）
- Modify: `tests/XamlLoadCheck/Program.cs`（新 pin：同宽不同高 → 网格行高与行距不变）

- [ ] **Step 1: 失败自检**：12 项 / 433 宽（1.1 档 → 4 列 3 行）在 400 与 700 高下——
  `grid.ActualHeight` 均为 **267**（=3×89，±0.01）、首行容器高 89、第二行容器相对首行 Y 偏移 89；
  修复前两例高度不等（≈314.5 / ≈587.3）即红。
- [ ] **Step 2: 运行确认失败**（运行时红，pin 报两例高度不等）。
- [ ] **Step 3: 实现**（XAML 一行；引擎/渲染代码零改动）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟（用户）**：拉高行距不变、能放整行时加行、宽度行为不变。
- [ ] **Step 5: 提交（两笔，先审查后提交）** →
      `docs: pin the folder row pitch against the panel height`（spec/plan，控制者已修订暂存）→
      `fix(folder): keep the icon row pitch when the panel grows taller`

---

## 阶段 4：TodoWidget 接入引擎

### Task A4.1: 日历/清单/小字自适应

**Files:**
- Modify: `Controls/TodoCalendarView.xaml(.cs)`（格高/清单行数由引擎输出；`BuildDayCellLabel` 按
  `ShowDayLabels` 显隐；渲染侧消费引擎常量 `FIXED_CONTENT_H/LIST_ROW_H/MIGRATION_CELL_H/LIST_MAX_ROWS`；
  `RequestContentResize` 自动尺寸及全部调用点删除；空日展开输入时引擎可用高 −30）、
  `Controls/TodoWidget.xaml.cs`（`RequestContentResize` 删除；`OnPanelSizeChanged` override → `_view.Refresh()`）、
  `Controls/WidgetWindowBase.cs`（`TODO_MIN_HEIGHT` 335 → 337）
- Modify: `tests/XamlLoadCheck/Program.cs`（小字显隐随尺寸、清单行数、面板尺寸稳定、自然高度一致、
  动态格高、拖动实时重排 pins；**添加按钮恒可见 pin**）

- [ ] **Step 1: 写失败自检**：格高 28（迁移默认）→ 无第二行小字；格高 ≥32 → 有（含 31.8/32 边界与
  无宽度兜底）；清单行数随高度（含 6 周最小尺寸回落到 1 行滚动）；换日/增删条目不再改变面板尺寸
  （尺寸稳定 pin，持久 400×500）；`DesiredHeight` 与引擎公式一致（`DesiredHeight + HEADER_HEIGHT(40) ==
  DefaultTodoSize(weekCount, clamp(rows)).h`，展开 +30；面板 = 内容 + 42 见 panel-fits pin）；拖动中格高实时变（readapt pin）；
  **添加按钮恒可见 pin（用户冒烟反馈）**：最小尺寸（342×337；A4.1 由 335 调至 337——含 2 DIP 边框后
  6 周 × 22 + 1 行清单精确装入）下两种状态——6 周月 + 多行清单（视口收缩到 1 行滚动）与空日展开输入
  （引擎可用高 −30、格高 22）——添加行完整落在内容区内（底边 ≤ 内容高 295）。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（引擎输出接入；渲染侧消费引擎常量——格高/行高/固定块分解；zh 判定与数据可用性沿用；
  语义行为不动）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟**：Todo 全清单（顺延/完成/子待办/编辑/右键盘菜单/
  换月/今天/持久化）+ 缩放观感（小字显隐、密度）。
- [ ] **Step 5: 提交（两笔，先审查后提交）** →
      `docs: pin the todo minimum size and the adaptive layout`（spec/plan，控制者已修订暂存）→
      `feat(todo): adapt the todo panel and day labels to free resizing`

### Task A4.2: Todo 交互细化（2026-10-12 用户冒烟后追加）

用户冒烟（A4.1）后确认的五点：① 拖动改为**增量重排**（不再整视图重建，修复添加行位置跳动）；② 今天
标记去实心圆、改**手绘空心圈 + 右下角手绘 ✅**，圈/✅ 随格高缩放（圈径 = `cellH + 4`）；③ **添加行
锚定**（`FitTodoList`：空间不足贴底、够用跟随最后一条，条件需容纳"按钮 + 所需"）；④ **分隔线拖动**
分别调整月历/清单（格高 22..36 按组件持久化 `TodoData.CalendarCellHeight`，外框缩放保持）；⑤ 空间
不足时**点击添加自动滚到底部**。用户已看 mockup 选定 ✅ 方案 B（`today-marker-mockup.png`）。

**Files:**
- Modify: `Core/PanelLayoutAdapter.cs`（新增 `TodoListLayout` 结构 + `FitTodoList(contentHeight,
  calendarHeight, naturalListRows, addBlockHeight)`；新增 `FIXED_ROOT_MARGIN_TOP=8` /
  `FIXED_ROOT_MARGIN_BOTTOM=10`，`FIXED_ROOT_MARGIN=18` 保留为和）
- Modify: `Core/TodoData.cs`（`double? CalendarCellHeight`）
- Modify: `Controls/TodoCalendarView.xaml`（分隔线处（Row 3，9 DIP 全高）加隐形拖动带 `SplitterBand`，
  Cursor SizeNS，三个鼠标事件）
- Modify: `Controls/TodoCalendarView.xaml.cs`（`FitTodoList` 接入与锚定视口；分隔带拖动逻辑（锁定禁用、
  松手 `NotifyDataChanged` 持久化）；增量重排 `ApplyResizeLayout`（格高/圈尺寸/视口；阈值翻转才重建
  日历；清单行不重建）；今天标记重写（圈 + ✅、无实心圆、数字常规色）；`RevealAddBox` 视口不足时
  `ScrollToEnd`）、`Controls/TodoWidget.xaml.cs`（`OnPanelSizeChanged` → `_view.ApplyResizeLayout()`）
- Modify: `tests/AdaptiveLayoutCheck/Program.cs`（`FitTodoList` pins）、`tests/XamlLoadCheck/Program.cs`
  （今天标记/圈缩放/锚定/分隔带/增量重排/滚动 pins；实心圆 pins 重写）、`tests/WidgetKindsCheck/Program.cs`
  （`CalendarCellHeight` JSON 往返 pin）
- Modify: `README.md`、`CHANGELOG.md`（新行为，控制者暂存、随 docs 一笔）

- [ ] **Step 1: 写失败自检**（编译红 = `FitTodoList`/`CalendarCellHeight` 未存在）：
  - 引擎：`FitTodoList` 跟随/贴底/半行/空日/钳零；342×337 6 周 → 视口 30；空日展开（块 66）→ 视口 0；
  - XamlLoadCheck：今天 = 圈 + ✅（Tag "TodayCheck" 画布、无 ≥18 实心盘、数字常规色、今天选中同）；
    圈径 = cellH+4（28→32、36→40）；锚定（高面板视口 = 所需且富余在按钮下方；min 尺寸 30/0 不回归）；
    分隔带拖动改格高并持久化、锁定禁用、外框缩放保持显式格高；拖动中清单行引用不变（增量）；视口不足
    时点击添加后 `VerticalOffset == ScrollableHeight`。
- [ ] **Step 2: 运行确认失败**（编译失败）。
- [ ] **Step 3: 实现**（引擎几何 + 渲染接线 + 分隔带 + 标记重写；三语无改动、无新键）。
- [ ] **Step 4: 自检绿 + 全量回归 + 手动冒烟（用户）**：拖分隔带、锚定、滚到底、今天 ✅、拖动顺滑。
- [ ] **Step 5: 提交（两笔，先审查后提交）** →
      `docs: pin the todo splitter, anchoring and today check`（spec/plan/README/CHANGELOG，控制者已
      修订暂存）→ `feat(todo): split the panel and anchor the add row`

---

## 阶段 5：收尾

### Task A5.1: README + CHANGELOG

- README（英中同步）：面板自由缩放与自适应；旧设置（列数/最大行数）移除、**图标大小保留可设置**
  （P3：面板变宽只增间距、拉高只加行/留白且行距不变、可设置项恢复）说明；数据兼容（新字段、旧字段
  保留）；行为变化：面板不再随内容自动收放；zh 第二行小字在面板足够高（格高 ≥32）时显示、迁移后默认
  隐藏（拖高即现）。
- CHANGELOG「Unreleased」：自由缩放、自适应、Todo 小字按尺寸显隐；行为变化一句（面板不再自动收放）。
- 提交 → `docs: document the adaptive panel resizing`

### Task A5.2: 全量回归 + 执行记录 + 终审

- `Run-Checks.ps1 -Group all` 全绿；计划「执行记录」写入手动验收结果与未验证项；
  终审（整分支，含 ledger minor/裁决清单）→ `finishing-a-development-branch`。

---

## Self-Review

- **Spec 覆盖**：spec §3 交互=A2.2；§3.3 持久化/迁移=A2.1；§4 引擎=A1.1（folder 侧 A3.2 按 P3 修订、
  todo 侧 A4.2 追加）；§4.2 接线=A3.2/A4.1；§4.3=A4.1/A4.2；§4.4 迁移公式=A1.1+A2.1；§6 测试=各任务；
  §8 阶段=本计划六阶段（0-5，阶段 0 为测试卫生；阶段 3 追加 A3.2/A3.3、阶段 4 追加 A4.2）。
- **占位符扫描**：无 TBD；关键数值已定（folder：scale clamp 0.9..1.5、临界加列按格宽；todo：小字阈值 32、
  格高 22..36），最小尺寸为"实现定值 + pin"，观感微调留待用户冒烟。
- **类型一致性**：`PanelLayoutAdapter` 的输出结构在 A1.1 定义，A3.1/A4.1 消费；
  `PanelWidth/PanelHeight` 在 A2.1 定义，A2.2 写、A3.1/A4.1 读。
- **风险**：阶段 3 触碰现有文件夹布局（菜单项移除是用户可感知变化）——单独提交 + 冒烟 + README；
  回退点 = 阶段 3 之前。

---

## 执行记录（2026-10-12）

**全量矩阵（Task A5.2）**

- 命令：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all`；执行日期
  2026-10-12，HEAD `486406a`；结果：31/31 全绿（29 控制台自检 + installer + config-pin），
  末行 `All 31 check(s) passed.`。
- A5.1 期间曾出现 config-pin 红：用户正在运行已安装版 Kobold、并发写盘（矩阵 wrapper pin 的预设归因）；
  静默窗口重跑全绿；最终记录以 2026-10-12 本次为准。
- 构建：`dotnet build Kobold.csproj -c Release` 0 警告 / 0 错误（A4.2 复跑）。

**手动冒烟（用户操作）**

- 阶段 2（缩放/持久化，A2.2）：用户报两现象（todo 添加按钮被挤、folder 水平裁切）→ 诊断 = 旧渲染未接入
  引擎的过渡态（下限 clamp 已生效）；A3.1 修 folder、A4.1 修 todo。
- 阶段 3（folder）：A3.1 无裁切通过；但 P1（拉宽图标变大）观感不佳 → 用户改定 P3（A3.2：图标六档可设、
  变宽只增间距、临界加列、动态最小尺寸、菜单回归）→ 冒烟发现**拉高行距被拉伸** → A3.3 修复（行距恒定、
  只加行/留白）——均已复检通过。
- 阶段 4（todo）：A4.1 冒烟反馈五点 → A4.2 细化（分隔线拖动分调、添加行锚定、点击添加滚动到底、
  今天手绘圈 + ✅、增量重排）→ 2026-10-12 部署复检通过。
- 全交互回归（锁定/最小/最大、重启恢复、主题/透明度、语言、菜单、岛与面板）在各轮冒烟中覆盖，
  未报回归。

**未验证项（如实声明）**

- 混合 DPI / 多屏真机验收受本机单屏限制（沿用项目既有声明）；跨屏尺寸恢复依赖既有 `PanelPlacement`
  机制。
- 分隔线拖动的低概率边界（拖动中 Alt+Tab / 失去 mouse capture）冒烟未复现；ledger 留有 minor
  （可加 `LostMouseCapture` 硬化）。
- ≤1.3.0 降级路径为既有隔离实测记录；本计划未触碰该路径。

**过程基线**

- 拖动不变量（capture → active → Started）pins 保持；自检矩阵不再写真实配置（阶段 0），wrapper
  config-pin 保持全绿。
- SDD 工作区 ledger（`.superpowers/sdd/2026-10-10-widget-adaptive-layout/progress.md`）含全部 Ruling 与
  deferred minors，供终审 triage。
