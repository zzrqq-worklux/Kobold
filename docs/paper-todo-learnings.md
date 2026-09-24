# PaperTodo 代码调研：Kobold 可借鉴的工程实践

> 调研日期：2026-09-24
> 调研对象：[snownico0722/PaperTodo](https://github.com/snownico0722/PaperTodo)（WPF / .NET 10 / C#，2.4k star，Windows 桌面便签）
> 本地克隆（只读参考）：`%TEMP%\opencode\PaperTodo`（调查时基于 commit `4284dac`，70e2e77 之后 main 头部）
> 对照对象：Kobold（WPF / .NET Framework 4.8，~12k 行应用代码）

本文是"PaperTodo 有什么值得 Kobold 学"的详细版，含 PaperTodo 侧文件路径索引，供后续实现时回查。
所有 `src/...`、`tests/...`、`doc/...` 相对路径均指 PaperTodo 仓库；`Core/...` 等相对路径指 Kobold 仓库。

---

## 1. 结论摘要

PaperTodo 值得学的不是功能，而是三样工程纪律：

1. **数据安全纪律** — 原子写、读失败保守恢复、破坏性操作 fail-closed、崩溃边界不强行保存。
2. **工程化闭环** — 单一测试入口、行为断言契约、marker 门控 CI、changelog 驱动的发布说明、版本单一真源。
3. **知识治理** — 路由（AGENTS）/ 当前架构（ARCHITECTURE）/ 历史决策（DECISIONS）/ 实验证据（EXPERIMENTS）四域分离，写入规则防止漂移。

功能层只有 3 处具体技术值得直接用：

- 多显示器工作区几何缓存 + 窗口恢复的"物理矩形 + 目标屏 DPI"策略；
- Topmost 全屏避让（前台全屏时把常驻窗口压下去）；
- 高对比度回退 + 系统偏好事件（Kobold 目前完全没处理）。

明确**不要**照搬：插件协议栈、Edge Capsule 全套 reducer/presenter/frame scheduler、LMDB 图片资产、
全局热键 broker、外部窗口跟踪、遥测、双 CHANGELOG 文件、17 个项目/8 条 workflow 的拆分方式。
它们服务的是 PaperTodo 的多队列/多显示器/插件生态规模，对 Kobold 是抽象税。

已有先例：Kobold 的 `Helpers/WindowSwitcher.cs` 即移植自 PaperTodo 的 `WindowNative`（quiet windows），
说明两个代码库的适配路径已经被验证过一次。

---

## 2. 规模与对照

| 维度 | PaperTodo | Kobold |
| --- | --- | --- |
| 运行时 | .NET 10（`net10.0-windows10.0.17763.0`，`PaperTodo.csproj:6`） | .NET Framework 4.8（`Kobold.csproj`） |
| 语言版本 | 最新 C#（record、file-scoped namespace 等） | C# 7.3（`LangVersion 7.3`） |
| 应用源码 | `src/` 约 200 个文件 + `PaperTodo.Plugin.Abstractions/` | ~12,000 行（56 个 cs/xaml） |
| 测试 | `tests/` 17 个独立 console check 项目（~250 用例 / ~1400 断言） | `tests/` 18 个 console check + 1 个迁移工具 |
| CI | 8 条 GitHub Actions workflow | 无 `.github/` |
| 发布 | `release.yml` 全自动（双单文件资产 + 双语 notes） | `tools/package.ps1` 手工 + 手工 GitHub Release |
| 文档 | AGENTS/ARCHITECTURE(515)/DECISIONS(1735)/EXPERIMENTS(1188)/双 CHANGELOG | `docs/superpowers/` 5 plans + 2 specs（过程材料） |
| 许可 | PolyForm NC + 附加许可（`LICENSE.md`） | MIT（README 声明，仓库无 LICENSE 文件） |

---

## 3. 数据安全与健壮性

### 3.1 原子写（`src/DurableAtomicFileWriter.cs`）

- 流程：同目录写 `.tmp`（`FileMode.Create + FileShare.None`）→ `Flush(flushToDisk: true)`（`:94`）→
  可选临时文件内容校验，失败必删临时文件且**绝不替换旧目标**（`:99-114`）→
  `File.Move(temp, target, overwrite: true)`，仅对 `IOException/UnauthorizedAccessException` 重试 5 次 × 100ms（`:32-41, :120-139`）。
- 可注入故障点：`BeforeTempOpen / AfterTempWrite / AfterFlush / BeforeReplace`（`:6-12`），
  由 `IDurableAtomicFileOperations`（`:22-28`）抽象文件系统操作。
- **不变量**：任何阶段失败保持旧目标可用。

**net48 注意**：`File.Move(..., overwrite)` 无此重载 → 用 `File.Replace(temp, target, null)`（目标须存在）
或先 `File.Delete` 再 `File.Move`；`FileStream.Flush(true)` 在 net48 可用。

### 3.2 读失败保守 + 恢复证据（`src/StateStore.cs`）

- 主文件失败 → 尝试 backup；从 backup 恢复时置 `_preserveRecoveredLoadFilesOnNextSave = true`（`:88-91`）。
- 首次正常保存前，先把失败主文件复制为 `data.failed_load.<ts>.json`、恢复用 backup 复制为
  `data.backup.used_for_recovery.<ts>.json`（`:354-399`），成功后才清除保护状态（`:311-320`）。
- 两者都失败才抛异常；**绝不返回空状态覆盖**（`:105-108`）。
- 读取宽容：允许注释/尾逗号（`StateJsonReadPolicy.cs:7-8`）、忽略未知字段（`StateStore.cs:17-21`）。
- 硬约束（`doc/DECISIONS.md` D-002）："失败启动后不得用默认空状态覆盖无法解析的主文件"。

### 3.3 版本化写入防倒退（`src/StateStore.cs:229-272`，`src/AppController.cs:2858-2884`）

- `SemaphoreSlim` + `long _latestWrittenVersion`：低版本写入直接丢弃；UI 侧保存完成用
  "最后意图" 判定再执行附属清理。仅当 Kobold 引入异步保存后才有意义。

### 3.4 备份刷新策略（`src/StateStore.cs:274-352`，`src/AppController.PersistencePolicy.cs:10-49`）

- 启动 + 每 6 小时从"已验证可反序列化"的主文件刷新备份；保护态拒绝；备份失败独立吞掉，
  不得影响正常保存。

### 3.5 破坏性操作 fail-closed（`src/NoteImageStore.cs:810-826`，`src/AppController.cs:2934-2956`）

- 引用可达性无法可靠证明 → 本轮不回收、禁用 id 复用。Kobold 的 Storage 若将来做清理，采用同一原则。

### 3.6 生命周期与退出（`src/AppController.cs:3609-3647`，`src/AppController.PersistencePolicy.cs:64-88`）

- 正常退出：先提交编辑 → 停定时器 → 同步最终保存 → 释放资源 → `Application.Shutdown()`；
  **不调用 `Environment.Exit` 截断 WPF 生命周期**。
- 系统关机/注销：独立路径 `ExitForSystemShutdown`——不启动新写入、等已进入的写完、禁止排队、失败不阻塞关机。
- Crash boundary（`src/App.xaml.cs:243-286`，D-003）：全局异常只写 crash log（100KB 上限裁到 80KB），
  **绝不在崩溃路径调用普通保存**（内存可能已不一致）。

### 3.7 保存调度（`src/AppController.cs:168-187, 2824-2842`）

- 1s 空闲 debounce + 10s 强制上限双定时器；退出期间 `MarkDirty` 直接短路。

### 3.8 测试验证方式（`tests/PaperTodo.PersistenceChecks/Program.cs`）

- 四个写入阶段故障注入；断言对象是"旧目标仍可加载"（`:86-88`）、"旧备份未被覆盖"（`:110`）、
  "重试次数恰好 3"（`:349-351`）；隔离临时目录 + `finally` 清理（`:563-585`）。

### 3.9 Kobold 现状与差距（已核实）

- `Core/AppConfig.cs:98-123`：`Save()` = 备份复制 + `File.WriteAllText` 直写，异常静默吞掉；
  **非原子、失败不可见**。
- `Core/AppConfig.cs:47-90`：主备都解析失败 → `new AppConfig()` + `Save()`，
  **静默重置且不留失败证据**；恢复后首次保存还会把损坏主文件复制覆盖 backup。
- `Core/AppConfig.cs:129-141`：debounce 每次重置，无强制上限。
- `App.xaml.cs:137-154`：无 `SessionEnding` 钩子；只有托盘退出路径保存（`Services/TrayIconService.cs` → `WidgetManager.Shutdown()`）。
- 落地顺序与方案见 `docs/superpowers/plans/2026-09-24-*`。

---

## 4. 测试与 CI

### 4.1 用例边界规则（`tests/README.md:46-60`，值得逐字借鉴）

- 保留：实际输入输出、可执行 policy/reducer、协议与公开程序集兼容、持久化、线程行为、
  窗口/像素/排版回归；反射触发原行为或故障注入不算无效测试。
- 撤掉：以源码字符串、私有字段/类型存在性、CLR 类型、固定 helper 名称、"旧实现必须消失"、
  "缓存必须同一引用"作为结论的断言；不允许换目录继续跑。
- 保留与否取决于"现在能否保护重要行为"，不取决于历史 PR 来源。

### 4.2 运行器（`tools/testing/Run-Checks.ps1`）

- 显式配置矩阵 `{Group, Project, Configuration, Runtime}`（`:14-33`），不同项目允许不同配置；
- `-List` 只列不跑、失败不中断继续跑完、汇总表 + 非零退出（`:35-39, :82-85`）；
- 本地与 CI 共用同一入口。

### 4.3 CI 组织（`.github/workflows/`）

- **HEAD commit marker 门控**：`[ci]` → Release 构建 + regression + Mica；`[debug]` → Debug 测试包；
  `[debug-ci]` → 两者（`pull-request-build.yml:15-18`）；标记必须在本次 push 最后一个 HEAD（`AGENTS.md:163`）。
- 统一 `permissions: contents: read`、`shell: pwsh`、`timeout-minutes`、job 级 `concurrency`。
- main push 跑 `debug.yml`（Debug 双包）+ `persistence-checks.yml`；路径过滤触发 `plugin-samples.yml`/`pages.yml`。
- 视觉证据 artifact：Mica 检查设 `PAPER_MICA_CAPTURE` 环境变量导出像素图并 `upload-artifact`（7 天）。
- 发布（`release.yml`）不跑测试（测试由 PR CI 承担）；稳定 tag 必须等于 csproj `<Version>`，
  稳定版只能 `workflow_dispatch` + `stable_release_confirmed=true`；tag 默认不可移动。

### 4.4 基准/诊断分离（`tools/README.md`）

- `tests/` 保护正确性，`tools/` 负责运行/采集/测量；性能数字不作 CI 门槛。
- 历史测量记录模式（`tools/PaperTodo.DesktopBenchmarks/PRELOAD-HISTORY.md`）：每条结论绑定
  受测 SHA、run 链接、样本口径，并写明"不可外推"边界。

### 4.5 Kobold 现状

- 18 个 console check + `tools/tests/installer.Tests.ps1`（234 行）全部手工运行；
- README 只列 12 个检查（`README.md:158-173`），实际 18 个 + `MigrateFoldRa`，文档已漂移；
- 无统一的 PASS/FAIL 契约（多数项目已有雏形）、无 run-all 入口、无 CI。

---

## 5. 知识治理、CHANGELOG 与发布自动化

### 5.1 四域知识模型（`AGENTS.md` + `doc/README.md`）

- `AGENTS.md`：任务路由表 + Agent 执行规则 + 禁区（`AGENTS.md:7-37`）；
- `doc/ARCHITECTURE.md`：只记录当前有效技术选型/结构/ownership/方向（写入规则 `AGENTS.md:54-60`）；
- `doc/DECISIONS.md`：历史 context/why/trade-off/rejected/pitfall，编号只增不改史，
  技术选择改变时新增条目并把旧条目标 `Superseded by D-xxx`（写入规则 `AGENTS.md:62-69`）；
- `doc/EXPERIMENTS.md`：可复现实测/证据，实验结论不自动升级为产品路线（`:3-8`）。
- 三条防漂移纪律：每次变更做"知识影响判断"、没有影响不硬改文档（`AGENTS.md:41`）；
  不新增并行架构文档（`:52`）；先核对代码/历史再统一修订（`:50`）。

### 5.2 CHANGELOG 纪律（`AGENTS.md:143-153`）

- 只写"用户可感知差异"；`计划/待办`、`评估`、`Unreleased` 三分；
- 同功能增强合并进原条目，不留 1.1→1.2 演进；开发期自引入自修复的回归不单独成条；
- 发布前从"上一个正式版用户"视角重读。

### 5.3 发布自动化（`.github/workflows/release.yml` + `.github/scripts/`）

- 版本真源 = `PaperTodo.csproj` 唯一 `<Version>`（`AGENTS.md:161`）；
- `release_notes.py`：精确提取 `### <tag>` 小节（恰好一个、非空），中文折叠在前英文在后，
  生成 `## Downloads / 下载` 标签；缺小节直接报错。带单元测试 `test_release_notes.py`。
- `sync_readme.py`：把 `README.zh.md` 全文注入 `README.md` 的
  `<!-- BEGIN/END GENERATED CHINESE README -->` 标记区；`--check` 可接 CI。
- 发布资产：self-contained（压缩）与 no-runtime（不压缩）两个单文件；LMDB 必须从源码 `-ForceRebuild`。
- 仍属人工：改版本号、维护双语 CHANGELOG、README 同步、稳定版前的真机手测。

### 5.4 Kobold 现状与差距

- 无 CHANGELOG、无 LICENSE 文件（README 声明 MIT）、无 `.github/`；
- 版本三处各自为政：`Kobold.csproj <Version>1.2.0</Version>`、`tools/package.ps1 -Version` 默认 `1.0.0`、
  README 示例 `-Version 1.1.0`；
- `docs/superpowers/plans|specs` 是高质量过程材料（含 TDD 步骤与验收清单），但无长期真源，
  决策理由散落在计划"执行记录"里。

---

## 6. UI / Win32 架构（选择性借鉴）

### 6.1 Intent → Reducer → Presenter 单权威模式

- 主链：`EdgeCapsuleIntent` → `EdgeCapsuleReducer` → `EdgeCapsuleModel` → `EdgeCapsuleTargetPlanner`
  → `EdgeCapsulePresentationPlan` → `EdgeCapsulePresenter` → `EdgeCapsulePresentationFrame` → `EdgeCapsuleHost.Apply`
  （`doc/ARCHITECTURE.md:318-340`；实现 `src/EdgeCapsuleModel.cs`、`src/EdgeCapsuleReducer.cs`、`src/EdgeCapsulePresenter.cs`）。
- 呈现契约三矩形：`Bounds`（可见形状）/ `HostBounds`（HWND 容量）/ `InteractiveBounds`（真实命中区）
  （`src/EdgeCapsulePresentation.cs:107-162`；命中判定 `src/EdgeCapsuleGeometry.cs:149-172`）。
- 指针只做一次物理采样 → intent；预测器只允许"否决/延迟"，从不选择目标（`src/EdgeCapsuleHoverIntent.cs:25-29`）。
- 对 Kobold 的适用：**只取轻量版**（岛屿 enum 状态机 + 单源动画过渡），全套基础设施过度设计。

### 6.2 材质与主题分层

- 语义色（`src/Theme.cs:52-65`）与材质（`src/MaterialPalette.cs:6-56`）分离：材质只影响表面，
  语义画刷保持不透明。
- DWM backdrop 生命周期：幂等请求记录 + HRESULT 逐级短路 + 系统事件失效缓存
  （`src/NativeMicaBackdrop.cs:116-227, 317-345`；`src/DwmMicaApi.cs:38-65`；`EncounterCount` 诊断计数）。
- 回退：高对比度用 `SystemColors`（`src/Theme.cs:132-144`）、透明关闭/不支持时回退不透明、
  版本能力门（Redirection Alpha 仅 24H2+，`src/DwmMicaApi.cs:140-147`）。
- 对 Kobold：若做 Mica 皮肤再引入；**高对比度部分建议无条件做**。

### 6.3 监视器几何与恢复（Kobold 直接受益）

- 类型系统区分物理/ DIP：`DeviceScreenPoint/DeviceScreenRect/GlobalScreenDipPoint/MonitorGeometry`
  （`src/ScreenGeometry.cs:5-42`）。
- 监视器缓存 + 失效：`WindowWorkAreaHelper.cs:466-510`；显示变化由 controller 失效并延迟重排
  （`src/AppController.cs:1605-1673`）。
- DPI 来源优先级：`GetDpiForWindow` → `GetDpiForMonitor` → 系统 DPI（`src/WindowWorkAreaHelper.cs:342-363, 536-590`）。
- 窗口恢复：保存 DIP + 捕获时 DPI；恢复"先转物理矩形选屏，再按目标屏 DPI 计算尺寸并 clamp 到工作区"
  （`src/PaperRestoreGeometry.cs:5, :12-52`；测试 `tests/PaperTodo.ThreadingChecks/PaperRestoreChecks.cs:57-145`）。
- Kobold 现状：多处直接读 `SystemParameters.PrimaryScreenWidth`（岛屿居中 `Controls/IslandWindow.xaml.cs`），
  面板夹取用整个虚拟桌面（`Controls/FolderWidget.Interactions.cs:101-109`），`PanelX/PanelY` 是裸 DIP。

### 6.4 全屏前台避让

- 检测器：前台窗口 DWM 扩展帧边界 ≈ 监视器矩形（2px 容差、最小 160px），排除本进程/tool window/
  cloaked/Progman（`src/FullscreenForegroundWindowDetector.cs:263-343`）；本进程窗口允许 1000ms 延续窗口
  避免抖动（`:231-240`）。
- 观察：3 个 `SetWinEventHook`（前台切换 / 对象创建显示 / 前台窗口移动），out-of-context
  （`src/FullscreenForegroundWindowWatcher.cs:9-56`）；控制器去抖 + 5s 全局扫描预算（`src/AppController.cs:1505-1571`）。
- Z 序：`SetWindowPos` + `SWP_NOACTIVATE` 并**校验真实结果**（`GetWindow(GW_HWNDNEXT)` 链），失败单次延迟重试
  （`src/WindowNative.cs:311-443`；`src/WindowNative.FullscreenAvoidanceRetry.cs:8-28`）。
- Kobold 现状：岛屿 `Topmost=True`，无任何检测，全屏时悬浮遮挡。

### 6.5 其他

- 全局热键事务式注册（`src/GlobalHotkeys.cs:698-1041`）：多 owner 冲突预检 + 失败回滚 + `MOD_NOREPEAT` +
  `WM_HOTKEY` 与"最后提交计划"核对。Kobold 无热键，若做只取最小版。
- 窗口关闭焦点交接策略（`src/WindowCloseActivationPolicy.cs:7-44`）：仅当关闭窗口当前确实是前台才交接，
  激活前二次确认。Kobold 面板是 Hide 而非 Close，暂不需要。
- 插件贡献模式：宿主拥有 chrome/生命周期，贡献方只给描述符（`PaperTodo.Plugin.Abstractions/PaperPresentationContracts.cs:3-26`）。
  对 Kobold 只保留"渲染器只接受描述符"的思想（`IconRendererFactory` 已是此结构）。

---

## 7. 不建议移植清单（避免过度工程）

| 项 | 原因 |
| --- | --- |
| 插件协议 2.x + 权限 + Runtime 分域 | 无第二实现方，纯抽象税 |
| Edge Capsule reducer/presenter/frame scheduler/DComp 全套 | 服务多队列边缘停靠，Kobold 无此形态 |
| LMDB / NoteImageStore / 图片 GC | Kobold 无图片资产域；只吸收"fail-closed"原则 |
| `SingleInstanceHelper` 命名管道命令通道 | net48 缺 `WaitForConnectionAsync(token)` 等 API；Kobold 已有 Mutex+Event 单实例 |
| 6 小时备份定时器 / 恢复文件全分类 | 单文件配置用"一个时间戳失败副本 + 保护位"足够 |
| 全局热键 broker 框架 / 外部窗口跟踪 / 遥测 / Pages 官网 | 与当前形态无关 |
| 17 个检查项目拆分 / 8 条 workflow / 双 CHANGELOG | 单维护者项目维护成本高于收益 |

---

## 8. Kobold 落地计划（A → B → C → D）

| 批次 | 内容 | 目标 |
| --- | --- | --- |
| A 数据安全 | `Core/AtomicFile.cs` + `AppConfig` 读失败留证据 + 关机保存 + 保存失败可见 + 10s 强制上限 | 消除用户数据被静默毁掉的路径 |
| B 工程化 | `tools/Run-Checks.ps1`、CHANGELOG、LICENSE、版本接线、release-notes 脚本、marker 门控 workflow | 手工流程闭环化 |
| C 多屏几何 | `Core/MonitorGeometry` 缓存 + 岛屿按光标屏工作区计算 + 面板恢复物理化 | 修多屏/混合 DPI 隐患 |
| D 体验补丁 | 全屏避让最小版 + 高对比度回退 | 岛屿不再挡全屏、系统设置实时响应 |

执行方式：每个批次走 TDD（先写失败自检 → 实现 → 全绿），完成后跑 `tools/Run-Checks.ps1 -Group all` 验证。
计划文件：`docs/superpowers/plans/`（每个批次一份）。
