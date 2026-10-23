# Kobold 文件搜索组件设计（全盘元数据索引）

> **状态**：**草案（待用户评审）**。本 spec 只定义 v1，不包含实现。
> **基线**：`main` @ `1a042de`（island launcher / 笔记右键 / Todo 拉长已合并）。
> **关系**：新增独立组件；不修改文件夹、待办、笔记、色盘、右键菜单的既有行为。
> **代码走查**：`Core/WidgetData.cs`、`Core/WidgetKindRegistry.cs`、`Core/WidgetManager.cs`、
> `Controls/IslandWindow.xaml(.cs)`、`Controls/WidgetWindowBase.cs`、`Windows/SettingsWindow.xaml(.cs)`、
> `tools/Run-Checks.ps1`、`tests/XamlLoadCheck`、`tests/ShellOpsCheck`。
> **参考实现**：本机废弃项目 `FileSearchApp`（.NET 8 + Avalonia + SQLite）。只借鉴 Core 的
> 索引/搜索/排除/watcher 思路，不直接引用工程，不移植 Avalonia UI、跨平台层、托盘/热键/单实例。

## 0. 决策记录（2026-10-22 用户确认）

1. **全盘元数据索引**：索引所有**固定 NTFS 盘**，默认不索引网络盘、可移动盘。
2. **默认常用类型**：默认只收常用文档/图片/媒体/压缩包/代码/快捷方式；设置里可切“全部文件”。
3. **文件和文件夹都索引**：文件夹本身作为结果；双击文件打开，双击文件夹在资源管理器打开。
4. **首次引导确认后索引**：第一次创建/打开搜索组件时展示范围说明，用户点“开始索引”后才后台建库；
   以后 Kobold 启动自动做增量/对账，不重复全量重建。
5. **无管理员权限**：v1 不依赖 USN Journal / MFT / Windows 服务 / 驱动 / 系统级 Shell 扩展；
   只用当前账户可读目录 + `FileSystemWatcher` + 周期对账。索引不完整时如实显示“跳过 N 个无权限目录”。
6. **内容搜索不在 v1**：不读 PDF/Office/文本内容，不做 FTS 内容表，不做 OCR。
7. **索引是派生数据**：SQLite 只存文件索引，用户配置仍留在 `config.json`；索引库可随时删除重建。
8. **一个全局索引服务**：多个搜索组件共享同一份 `search-index.db`，不按 widget 各建一份。

## 1. 目标与成功标准

| # | 目标 | 验收口径 |
|---|---|---|
| 1 | 可创建搜索组件 | island `＋` 新建菜单出现“文件搜索”；创建后 island 有独立 tile；重启后恢复 |
| 2 | 首次建库可控 | 第一次打开面板出现范围说明；点“开始索引”才扫描；可取消；取消不留下半成品可见索引 |
| 3 | 全盘可搜 | 固定 NTFS 盘上当前账户可读的文件/文件夹可按名称、路径搜索 |
| 4 | 搜索快且不卡 | 输入 200ms 防抖；查询可取消；UI 线程不在 SQLite/目录遍历上阻塞 |
| 5 | 增量可靠 | 新建/修改/删除/重命名后结果在合理时间内更新；watcher 异常有对账兜底 |
| 6 | 结果可操作 | 双击文件打开；双击文件夹打开；右键“打开所在位置 / 复制路径” |
| 7 | 无管理员 | 安装、运行、建库、搜索全程不弹 UAC；跳过受保护目录并计数 |
| 8 | 零回归 | 既有组件与 32 项检查不回归；新增 FileSearchCheck 后全量 33/33；0 警告 |

## 2. 非目标（v1 不做）

- 不搜文件内容；不做 PDF/Word/Excel/PPT 解析、OCR、全文高亮。
- 不做 USN Journal / MFT / Everything SDK / Windows Search 集成。
- 不做 Windows 服务、驱动、Shell 扩展、系统级搜索提供程序。
- 不做网络盘、可移动盘、云盘占位文件的默认索引（后续可加，但 v1 不做）。
- 不做跨平台；只支持 Windows / .NET Framework 4.8 / WPF。
- 不做全局热键、独立悬浮搜索窗、托盘二级入口；只作为 Kobold 组件。
- 不做结果表头多列排序、文件预览、缩略图、原生文件图标（v1 用类型 glyph）。
- 不把索引写入 `config.json`；不迁移 FileSearchApp 的旧 `index.db`。

## 3. 产品范围

### 3.1 索引根

- 启动时枚举 `DriveInfo`，取 `DriveType.Fixed` 且文件系统为 NTFS 的卷。
- 默认全部勾选；首次引导允许用户取消某个盘。
- 根变化后不自动删旧库：标记需要 Rebuild，由用户确认。
- 不跟随 reparse point / junction / symlink，避免循环和重复。
- 受保护目录、无权限目录逐目录 `try/catch` 跳过；记录跳过数量。

### 3.2 默认常用类型

设置中的“索引类型”是**用户可见的产品范围**，不是性能优化手段。默认集合：

```text
文档：pdf doc docx xls xlsx ppt pptx txt md rtf csv
图片：jpg jpeg png gif bmp webp svg heic
媒体：mp4 mkv avi mov mp3 wav flac
压缩：zip 7z rar tar gz
代码/配置：cs js ts py java json xml yaml yml sql html css
快捷方式：lnk url
```

附加开关：

- **全部文件**：忽略扩展名白名单，仍受目录排除/权限约束。
- **无扩展名文件**：默认开启，覆盖 `README`、`Makefile`、`LICENSE` 等。
- **索引文件夹**：默认开启且不随类型开关关闭。

扩展名比较大小写不敏感；按最后一个扩展名归类（如 `.tar.gz` 归到 `.gz`），完整文件名仍参与搜索。

### 3.3 目录排除

目录排除是**性能/安全边界**，始终生效，不因“全部文件”关闭：

```text
C:\Windows\WinSxS
C:\Windows\System32\config
C:\Windows\ServiceProfiles
C:\Windows\Temp
$Recycle.Bin
System Volume Information
C:\Users\<user>\AppData\Local\Temp
node_modules
.git
.svn
浏览器/CEF 缓存（blob_storage、WXWorkCefCache、WasmTtsEngine 等）
包管理器缓存（NuGet、npm、pip、Cargo 等）
```

- 默认列表内置；设置里可追加用户目录。
- 名称匹配和完整路径匹配都支持；路径统一 `GetFullPath` 归一化。
- 跳过 reparse point，不进入被排除目录的子目录。

### 3.4 结果与交互

- 搜索框：200ms 防抖；空查询显示提示/最近索引状态，不显示全量结果。
- 结果上限 300 条；状态栏显示“显示前 300 条，共 N 条匹配”。
- 排序：文件名匹配优先于路径匹配；同组按名称。
- 行内容：类型 glyph、文件名、所在路径、大小、修改时间；长路径省略。
- 双击文件 `ShellExecute` 打开；双击文件夹打开资源管理器。
- Enter 打开当前选中；右键菜单：打开、打开所在位置、复制路径。
- 搜索中/索引中显示状态；不阻塞输入。
- 无结果、未建库、索引暂停、索引损坏各有独立空态。

## 4. 架构

```text
WidgetManager
  └─ FileSearchIndexService（应用内单例）
       ├─ SearchIndexSettings（来自 AppConfig）
       ├─ DirectoryScanner（全量/对账）
       ├─ SearchIndexStore（SQLite，派生数据）
       ├─ FileSystemWatcherHost（增量队列）
       └─ FileSearchQuery（读取连接 + 查询）

SearchWidget（WPF Window）
  └─ SearchPanelView
       ├─ 搜索框 / 状态行
       └─ 虚拟化结果列表
```

### 4.1 组件边界

- `FileSearchIndexService`：生命周期、启动/暂停/重建、进度事件、状态；不依赖 WPF 控件。
- `DirectoryScanner`：可取消的目录遍历；只产生 `FileEntry`（路径、名称、扩展名、是否目录、大小、修改时间）。
- `SearchIndexStore`：schema、批量 upsert、删除、查询、VACUUM；所有写操作串行化。
- `FileSystemWatcherHost`：事件入队，单 worker 消费；目录重命名补插；错误上报触发对账。
- `SearchPanelView`：只做展示和输入；搜索/索引进度通过事件回 UI 线程。

### 4.2 SQLite 索引

位置：`%LocalAppData%\Kobold\search-index.db`（不放 Roaming，避免大文件漫游）。

```sql
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 10000;
PRAGMA user_version = 1;

CREATE TABLE files (
    Path        TEXT PRIMARY KEY COLLATE NOCASE,
    Name        TEXT NOT NULL,
    NameLower   TEXT NOT NULL,
    Extension   TEXT NOT NULL,
    IsDirectory INTEGER NOT NULL,
    SizeBytes   INTEGER NOT NULL,
    ModifiedUtc INTEGER NOT NULL,
    Root        TEXT NOT NULL
);
CREATE INDEX idx_files_name_lower ON files(NameLower);
CREATE INDEX idx_files_extension ON files(Extension);
CREATE INDEX idx_files_root ON files(Root);

CREATE TABLE meta (Key TEXT PRIMARY KEY, Value TEXT);
```

- 批量写入每 500–1000 行一个事务；全程单个写连接/写 worker。
- 查询用独立只读连接；WAL 允许重建期间继续搜索。
- 全量扫描写入同构的 `files_new` 暂存表，完成后短事务 `DROP files` + `RENAME files_new TO files`；
  增量更新直接写 `files`。这样取消全量扫描不会破坏正在使用的旧索引。
- 写入更新 `meta.last_scan_utc`、`meta.schema_version`、`meta.last_error`。
- v1 查询策略：先 `NameLower` 前缀匹配（可走索引），再 `NameLower LIKE '%q%'` / `Path` 包含补足；
  300 条上限。若真实全盘 P95 超标，spike 结论允许切换 FTS5 trigram；接口不变。
- 索引库损坏/版本不匹配：保留旧库为 `.failed-<timestamp>` 证据（可选），重新建库；不得静默把用户配置连带损坏。

### 4.3 全量扫描

- 枚举固定 NTFS 盘；逐盘、逐目录迭代遍历（不使用一次性 `GetFiles` 全量数组）。
- 每个目录独立 `try/catch`：`UnauthorizedAccessException` / `IOException` 跳过并计数。
- 跳过 reparse point；长路径使用 `\\?\` 前缀；超过路径上限的记录跳过并计数。
- 批量 upsert；每 500ms 上报一次进度（已扫描目录/文件/跳过数）。
- 支持取消；取消后清理 `files_new` 临时表，不破坏旧索引。
- 扫描线程降优先级；不在 UI 线程执行。
- 首扫完成后自动 checkpoint + 可选 VACUUM（只在一轮完成后做一次）。

### 4.4 增量与对账

- 每个可读根目录一个 `FileSystemWatcher`，`IncludeSubdirectories=true`；
  `InternalBufferSize` 适当放大。
- 事件只入队，不在回调里写库；单 worker 串行写。
- Created/Changed：upsert；Deleted：删路径及其前缀；Renamed：删旧路径 + 补新子树（目录事件）。
- `Error` 事件（缓冲溢出）上报状态，触发后台 Reconcile。
- 启动时：若已有库，启动 watcher + 后台 Reconcile（只补差异/清死链），不重扫全盘。
- 周期 Reconcile：默认 10 分钟静默执行；可配置关闭。
- watcher 无法覆盖的根/盘（如休眠、拔盘、权限）降级为周期对账，状态栏显示“增量不可用，依赖对账”。

### 4.5 生命周期

1. 用户在 island `＋` 创建“文件搜索”组件，面板打开。
2. 未配置索引：显示首次引导（固定盘复选、类型说明、排除说明、范围量级说明，
   不承诺精确文件数），点“开始索引” -> `FileSearchIndexService.StartFullScan()`；点“稍后”只显示空态。
3. 建库完成：面板可搜索；写 `SearchIndexConfigured=true`。
4. 之后 Kobold 启动：若已配置且仍有搜索组件，启动服务、watcher、后台 Reconcile；不自动全量重建。
   删除最后一个搜索组件后停止 watcher 以省资源，索引库保留；再次创建时复用。
5. 设置/组件菜单提供：暂停、继续、重建、取消、查看索引大小/最后更新时间。
6. Kobold 退出：停止 watcher，等待写 worker 落盘（有超时）；索引库持久保留。

### 4.6 配置

`AppConfig` 新增一个全局 `FileSearch` 配置对象（JSON 字段，默认 null 视为未配置）：

```text
Enabled             bool
Configured          bool
Roots               string[]    // 盘符或目录；空 = 所有固定 NTFS 盘
IndexAllFiles       bool        // false = 使用 Types
Types               string[]    // 小写扩展名，不带点
IndexFolders        bool        // 默认 true
IndexExtensionless  bool        // 默认 true
ExcludeDirectories  string[]    // 追加排除目录
ReconcileMinutes    int         // 默认 10，0 = 关闭
```

- 配置写 `config.json`，沿用现有 `SaveDebounced`/原子保存/备份。
- 索引库不写配置；`SearchData` 只保存 widget 的公共字段（名称、颜色、位置、面板尺寸等）。
- 新增 `SearchData : WidgetData`，`Kind = "search"`；不持久化搜索词。

## 5. UI

### 5.1 搜索组件

- 标准 `WidgetWindowBase` 面板，宽度沿用现有 panel 宽度策略（内容宽度约 420）。
- 顶部：搜索框（占满宽度）、清除按钮、状态文字。
- 中部：虚拟化结果列表；行高约 44；路径灰色小字；文件夹和文件用不同 glyph。
- 底部/状态行：`Indexing 12,340 / 240,000`、`Ready`、`Paused`、`Index unavailable`、
  `Showing first 300`、`N folders skipped`。
- 未配置：说明 + “开始索引”按钮。
- 索引中：可继续搜索已有结果；顶部显示进度，不阻塞输入。
- 错误：显示简短错误 + “重试/重建”；不弹模态错误框打断输入。

### 5.2 设置与组件菜单

- 首次引导：固定盘复选、类型说明、排除说明、开始/稍后。
- 设置窗口新增“文件搜索”区：根、类型、全部文件、无扩展名、文件夹、追加排除目录、
  索引状态/大小/最后更新、重建/暂停。
- 组件右键菜单加入：`重建索引`、`暂停索引`（与全局设置共享状态）。

### 5.3 本地化

新增 en / zh / ja 三语 key（至少）：

```text
UI_WidgetKindSearch
Island_NewSearchHint
Search_Placeholder
Search_Empty
Search_NoIndex
Search_Indexing
Search_Ready
Search_Paused
Search_Error
Search_Rebuild
Search_StartIndexing
Search_Later
Search_Open
Search_OpenLocation
Search_CopyPath
Search_ShowingFirst
Search_SkippedFolders
Search_AllFiles
Search_Types
Search_Excludes
```

三语文案随实现补齐；`LangCheck` 必须覆盖。

## 6. 无管理员约束

- 进程保持 `asInvoker`；安装器继续 `%LocalAppData%\Programs\Kobold`。
- 不使用 USN/MFT、Windows 服务、驱动、Shell 扩展。
- 索引范围 = 当前账户可读范围；受保护目录跳过并显示数量。
- 不尝试通过 `SeBackupPrivilege` 等绕过 ACL。
- 长路径使用 `\\?\`；不修改系统长路径组策略。
- 若 `FileSystemWatcher` 对某个根启动失败，该根降级为周期对账，不使整个服务失败。

## 7. 性能与可靠性目标

| 指标 | 目标 |
|---|---|
| UI 卡顿 | 搜索/建库期间 UI 线程单次阻塞 < 50ms |
| 搜索延迟 | 10 万文件 P95 < 150ms；30 万文件 P95 < 300ms（本机实测记录） |
| 首扫 | 可取消；每 500ms 有进度；不一次性把全盘路径读入内存 |
| 索引库 | 30 万文件目标 < 300MB；内容索引不建 |
| 增量 | 普通创建/删除/重命名在 2s 内反映；watcher 异常后 10 分钟内对账修复 |
| 稳定性 | 单目录权限错误/单个文件锁不影响整轮扫描 |

**Spike gate（实现前先做）**：在用户本机用标准账户扫描可读固定盘，记录文件数、目录数、
跳过数、首扫耗时、索引库大小、常见关键词 P95、`C:\` watcher 是否稳定。若首扫或搜索
明显不达标，再决定是否引入 FTS5 trigram；若 watcher 在根上不稳定，v1 直接以“用户目录 +
项目盘 + 对账”为默认根。

## 8. 测试

### 8.1 新增 `tests/FileSearchCheck`

只用临时目录/临时 DB，不触碰真实磁盘：

- 扩展名白名单、全部文件、无扩展名、文件夹开关。
- 排除目录（名称/完整路径）、reparse point 跳过。
- 目录遍历：嵌套、空目录、权限拒绝模拟（可注入拒绝目录）、取消。
- SQLite schema/批量 upsert/删除/重命名前缀删除。
- 搜索：名称优先、路径补足、大小写、中文、文件夹结果、300 上限。
- watcher：临时目录创建/修改/删除/重命名；目录重命名补插；错误降级（能稳定 pin 的部分）。
- 配置 round-trip / 默认值 / 旧配置缺字段。

### 8.2 既有检查

- `XamlLoadCheck`：SearchWidget 加载、面板结构、island 新建菜单项、三语 key 文案。
- `LangCheck`：新 key 全覆盖。
- `ShellOpsCheck`：打开文件/定位路径的 shell 调用（若走现有 ShellFileOperations）。
- 安装器：SQLite native 文件随包安装，卸载不残留索引库（或明确保留策略）。
- `Run-Checks.ps1` 矩阵新增 `FileSearchCheck`；全量从 32 项变 33 项。

### 8.3 冒烟（用户手动）

- 无管理员账户首次引导 -> 开始索引 -> 进度/取消。
- 全盘可读范围内的文件/文件夹搜索；中文名、长路径、无扩展名。
- 新建/重命名/删除文件后结果更新；重启后索引保留。
- 重建索引；索引库损坏/删除后恢复。
- 搜索时 UI 不卡；索引中可搜索。
- 受保护目录跳过计数；不弹 UAC。

## 9. 风险与回退

| 风险 | 处理 |
|---|---|
| 全盘首扫慢/占 IO | 后台低优先级、进度、取消、默认保守排除；Spike 先测 |
| watcher 漏事件 | 事件队列 + 周期对账 + 手动重建 |
| SQLite 写锁/UI 卡死 | 单写 worker + WAL + 只读查询连接；UI 不碰 DB |
| 无管理员导致索引不完整 | 明确文案“当前账户可读”；跳过计数；不承诺系统文件 |
| SQLite 原生依赖打包失败 | Spike 先验证 provider；安装器加 native 文件检查 |
| 索引库过大 | 只存元数据；常用类型默认；不建内容表；VACUUM |
| 搜索结果杂 | 类型白名单 + 目录排除 + 名称/路径两段排序 |
| 新增组件导致 32 项矩阵变化 | 更新 `Run-Checks.ps1` 与文档，目标 33/33 |

回退：删除 `search-index.db`、关闭 `FileSearch.Enabled`、从 island 删除搜索 widget；
不影响 folder/todo/note 数据。

## 10. 待实现前确认的细节

以下不阻塞 spec，留给实现计划/Spike 定稿：

1. SQLite provider：`Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` 还是
   `System.Data.SQLite.Core`（net48 + 无管理员 + 安装包体积/原生 DLL）。
2. 查询后端：先用归一化 `NameLower` + LIKE；Spike 超标再上 FTS5 trigram。
3. 首扫吞吐与索引库大小在本机的实测基线。
4. 设置区放在现有 SettingsWindow 的哪个分组；组件菜单是否同时提供重建/暂停。
5. 结果行是否显示大小/时间两列，还是路径 + 名称优先（视觉冒烟定）。

## 11. 验收清单

- [ ] island `＋` 可创建“文件搜索”组件；重启恢复。
- [ ] 首次引导可开始/取消；取消后可再次进入。
- [ ] 固定 NTFS 盘可读文件/文件夹可搜；受保护目录跳过并有计数。
- [ ] 名称优先、路径补足；中文/大小写/无扩展名/长路径可用。
- [ ] 双击文件打开、双击文件夹打开；右键定位/复制路径。
- [ ] 索引中可搜索；UI 不卡；搜索可取消。
- [ ] 增量更新、周期对账、重建、暂停均可用。
- [ ] 全程无 UAC；安装仍按用户。
- [ ] 新增 FileSearchCheck + 既有检查全绿；0 警告；矩阵 33/33。
- [ ] 真实全盘冒烟通过（用户手动）。
