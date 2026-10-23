# 2026-10-23 文件搜索 Spike 决策记录

**Task:** `2026-10-23-file-search-widget` / Task 1
**基线:** `1a042de`（本分支含 spec `d3b52cf`、plan `8b40df9`）
**Spike 工程:** `tests/FileSearchSpike`（net48 / C# 7.3，独立 console，未触碰 `Core/FileSearch`）

## 1. 结论表

| 项目 | 结果 |
|---|---|
| provider / 版本 | Microsoft.Data.Sqlite.Core 8.0.30 + SQLitePCLRaw.provider.e_sqlite3 2.1.12 |
| 原生 DLL 相对路径 | runtimes\win-x64\native\e_sqlite3.dll |
| 300k 插入耗时 | 2028 ms（4 次运行 2028–2459 ms） |
| 索引库大小 | 33.5 MB（35,139,584 B，WAL 已 checkpoint 截断） |
| 前缀 P95 / 子串 P95 | 22.9 / 62.4 ms（4 次运行最差 51.7 / 135.2 ms，均 < 300 ms） |
| 2 万文件扫描吞吐 | 86,474 files/s（4 次运行 36,055–91,455 files/s） |
| C:\ watcher | 稳定（探针事件收到，30 秒 0 错误，48 个 Created 事件） |
| 查询后端结论 | LIKE（不启用 FTS5 trigram） |
| 默认根结论 | 所有固定 NTFS 盘（保留 `SearchRoots.FixedNtfsRoots()`），加容量上限与降级提示 |

## 2. 环境

| 项目 | 值 |
|---|---|
| OS | Windows 11 企业版 10.0.26200（x64） |
| CPU | 11th Gen Intel Core i7-1195G7，4 核 / 8 线程 |
| 内存 | 15.8 GB |
| 磁盘 | PCIe SSD 1TB；固定卷 C: 400 GB（NTFS）、D: 553.6 GB（NTFS） |
| SDK | .NET SDK 8.0.424（编译 net48） |
| SQLite | `sqlite_version = 3.53.3`（由本机缓存的 `SQLitePCLRaw.lib.e_sqlite3 2.1.12` 提供；该包的 `e_sqlite3.dll` 无版本资源、构建时间 2026-07，可能为内部重打包版本，公网 2.1.12 发布时通常更旧） |
| 进程 | 64 位（`IntPtr.Size = 8`） |
| 依赖来源 | 本机 NuGet 缓存，无需联网还原 |

固定关键词（20 个，故意混合选择度）：`100, 999, spike, txt, 25, 0, 5, 123, 888, 42, 24, a, z, 7, 33, 555, 77, 9, 15, 2500`。
每个关键词跑 20 轮，P95 取全部样本的最近秩（`ceil(0.95*n)-1`）。时间包含完整结果行读取（`Path`/`Name` 都读走），不只是首行。

## 3. 方法

1. `SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3()); SQLitePCL.raw.FreezeProvider();`
2. 库建于 `%TEMP%`：`PRAGMA journal_mode=WAL; busy_timeout=10000; synchronous=NORMAL`；表/索引与 brief 完全一致（`files` 8 列 + `idx_files_name_lower`）。
3. 单事务 300k 行参数化插入（`C:\spike\<i>.txt`）。
4. 前缀 / 前缀区间 / 子串 / 计数四类查询各 400 样本。
5. FTS5 trigram 对照：独立 DB，`fts5(Name, Path, tokenize='trigram')`，用 `ATTACH` + `INSERT ... SELECT` 灌入同样的 300k 行。
6. 扫描：`%TEMP%` 下 2,000 目录 × 10 文件（20,000 文件）用 `EnumerateFileSystemInfos()` 逐目录遍历；再只读扫描 `%USERPROFILE%\Documents`（存在）。
7. `FileSystemWatcher(@"C:\")`，`IncludeSubdirectories=true`，`InternalBufferSize=64KB`，向 `%TEMP%` 写探针文件，等待事件 10 秒，然后观察 30 秒。

未扫描真实全盘；`C:\` 只做只读 watcher。

## 4. 测量结果

共跑 4 次（2 次直接执行 exe、1 次 `dotnet run`、1 次源码微调后的最终 run）。「最终 run」为 `dotnet run --project tests/FileSearchSpike -c Release` 的完整输出；「4 次区间」用来说明抖动幅度，结论一律按最差值判定。

### 4.1 插入与库大小

| 指标 | 最终 run | 4 次区间 |
|---|---|---|
| `insert_ms`（单事务 300k 行） | 2028 | 2028–2459 |
| 折算插入速率 | ≈148k 行/s | 122k–148k 行/s |
| `db_bytes` | 35,139,584 | 恒定 |
| `wal_bytes`（checkpoint 后） | 0 | 恒定 |
| `page_bytes`（`page_count*page_size`） | 35,139,584 | 恒定 |
| 每行占用 | ≈117 B/行 | 恒定 |

库大小在所有运行中完全一致，说明插入/建索引是确定性的；只有时间受缓存/负载影响。

### 4.2 LIKE 查询（400 样本/类）

| 查询 | median (ms) | P95 (ms) | max (ms) | P95 4 次区间 |
|---|---|---|---|---|
| `NameLower LIKE 'kw%'` | 18.2 | **22.9** | 32.4 | 22.9–51.7 |
| 区间前缀 `NameLower >= 'kw' AND <= 'kw\uFFFF'` | 0.3 | 0.5 | 0.8 | 0.5–1.1 |
| `NameLower LIKE '%kw%' OR Path LIKE '%kw%' ORDER BY Name LIMIT 300` | 47.9 | **62.4** | 80.7 | 62.4–135.2 |
| `COUNT(*)` 同上条件 | 46.4 | **58.0** | 71.0 | 58.0–130.2 |

`infix_first_kw_rows=300`（`100` 命中被 LIMIT 截到 300，符合预期）。

`EXPLAIN QUERY PLAN`：

- 前缀：`SCAN files USING INDEX idx_files_name_lower` —— **全索引扫描，不是 seek**。因为 `NameLower` 是 BINARY collation，而 `LIKE` 默认大小写不敏感，SQLite 的 LIKE 优化不生效。
- 子串：`SCAN files / USE TEMP B-TREE FOR ORDER BY` —— 全表扫 + 临时 B 树排序。

### 4.3 FTS5 trigram 对照

| 指标 | 最终 run | 4 次区间 |
|---|---|---|
| trigram 可用 | 是 | 是 |
| 建索引耗时（300k 行） | 1743 ms | 1485–2871 ms |
| FTS 库大小 | 45,453,312 B（43.3 MB） | 恒定 |
| 命中数一致性（`spike`） | LIKE 300,000 = FTS 300,000 | 恒定 |
| 子串 P95（160 样本） | **209.2 ms** | **209.2–488.8 ms** |
| median / max | 1.3 / 221.4 ms | median 1.3–3.0 |

样本 160 = 8 个长度 ≥3 的关键词 × 20 轮（trigram 无法回答 1–2 字符查询）。

### 4.4 扫描吞吐

| 场景 | 条目 | 耗时 | 吞吐 | 跳过 |
|---|---|---|---|---|
| 合成树（20,000 文件 + 2,000 目录） | 22,000 | 254 ms | 86,474 条目/s（4 次 36,055–91,455） | 0 |
| `C:\Users\31177347\Documents` | 25,999 文件 + 1,512 目录 | 210 ms | 130,540 条目/s（4 次 62,794–130,540） | 3 |

合成树创建本身耗时 6664 ms（4 次 7247–16191 ms，22,000 条目），说明**创建**比扫描慢一个数量级，测试中的吞吐以遍历为准。

### 4.5 C:\ watcher

| 指标 | 最终 run | 4 次区间 |
|---|---|---|
| 探针事件（在 `%TEMP%` 深层目录创建文件） | 收到（`watcher_c_root_probe_event=True`，10 s 内） | 4/4 收到 |
| 30 秒窗口内 Created 事件 | 48 | 39–65 |
| `Error` 事件（缓冲溢出等） | 0 | 0 |
| 30 秒稳定性 | 稳定 | 4/4 稳定 |

探针文件位于 `C:\Users\...\AppData\Local\Temp`，距根 5 层，事件仍送达 → 递归监听从根生效。

## 5. 决策

### 5.1 Provider：采用 Microsoft.Data.Sqlite.Core 8.0.30（不用元包、不用 bundle）

三条包（`Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.e_sqlite3` + `SQLitePCLRaw.lib.e_sqlite3`）在 net48 下显式 `SetProvider` 工作正常，`sqlite_version=3.53.3`。**没有**触发 `System.Data.SQLite.Core 1.0.119` 回退，也没有用 `bundle_e_sqlite3`。

**必须记录的平台坑（Task 4 必做）：** 默认 AnyCPU 的 net48 exe 即使 COR 头里没有 `32BITREQUIRED`/`32BITPREFERRED`（即 64 位进程），NuGet 仍会复制 `runtimes/win-x86/native/e_sqlite3.dll`。结果：

```text
spike_error=System.BadImageFormatException: 试图加载格式不正确的程序。 (异常来自 HRESULT:0x8007000B)
   在 SQLitePCL.SQLite3Provider_e_sqlite3.NativeMethods.sqlite3_libversion_number()
   在 SQLitePCL.raw.SetProvider(ISQLite3Provider imp)
```

修法：`<PlatformTarget>x64</PlatformTarget>`（spike 已加），NuGet 随即复制 `win-x64` 的 1,978,880 B 版本。

`Kobold.exe` 当前的 COR flags 与 spike 相同（无 32BITREQUIRED / 32BITPREFERRED），在 x64 Windows 上本来就是 64 位进程 → **Task 4 在 `Kobold.csproj` 里加 `<PlatformTarget>x64</PlatformTarget>` 不改变现有运行行为**，只是把原生依赖钉死。若将来要兼容 32 位 Windows，则必须同时分发 x86/x64 两份原生 DLL 并按进程架构解析，成本更高，v1 不做。

另：`Microsoft.Data.Sqlite` 8.0.30 依赖 `System.Memory 4.5.3` 等 netstandard2.0 包，但在本工程里**没有产生 binding redirect 需求**（`Kobold.FileSearchSpike.exe.config` 只有 `supportedRuntime`，删掉 `AutoGenerateBindingRedirects` 后同样运行通过）。Task 4 引用同样包时留意：若出现 `System.Memory`/`System.Buffers` 版本冲突（`FileLoadException`），再开 `AutoGenerateBindingRedirects` 生成 redirect。

### 5.2 查询后端：LIKE（不启用 FTS5 trigram）

- 目标「300k 行 P95 < 300 ms」：LIKE 子串 P95 = **62.4 ms**（4 次运行最差 135.2 ms），计数 = 58.0 ms（最差 130.2 ms），**达标且有余量**。
- FTS5 trigram 反而更慢（P95 209.2 ms，4 次运行最差 488.8 ms）：median 只有 1–3 ms，说明慢的是高命中关键词（`txt`/`spike` 命中 30 万行）后 `ORDER BY Name LIMIT 300` 的大排序，而不是索引本身。
- FTS5 额外成本：+43.3 MB 索引（比主表还大）、建索引 1.5–2.9 s、**查询下限 3 字符**（1–2 字符关键词无法用 trigram），以及一套需要与主表同步的写路径。

结论：v1 保持 `NameLower` 前缀 + `LIKE '%q%'` + `Path LIKE`，`ISearchIndexStore.Query` 签名不变。FTS5 留作后续若引入内容搜索/更大库时的选项。

### 5.3 前缀查询改用区间 seek（实现细节）

`LIKE 'kw%'` 的查询计划是**全索引扫描**（22.9 ms P95，最差 51.7 ms；300k 行下够用但会随行数线性增长）；等价区间谓词的 P95 是 **0.5 ms**（最差 1.1 ms），median 0.3 ms，是真正的 seek。

建议 Task 4 在 `SearchIndexStore.Query` 内部把前缀条件写成：

```sql
NameLower >= @q AND NameLower < @q || char(0x10FFFF)
```

语义与 `LIKE 'q%'` 一致（`NameLower` 已小写化，ASCII 关键词等价），但走 `idx_files_name_lower` seek。这次 spike 实测用的是 `<= kw || U+FFFF`，Task 4 用 `U+10FFFF` 作为严格上界即可。外部 `ISearchIndexStore.Query` 签名不变。

### 5.4 默认根：所有固定 NTFS 盘（保留现状）+ 容量上限

- 本机两个固定 NTFS 卷：`C:` 400 GB、`D:` 553.6 GB，均为 Fixed/NTFS/Healthy。plan 里的 `SearchRoots.FixedNtfsRoots()` 与实测吞吐（36k–91k 条目/s）匹配：2,000,000 条目的首建约 22–55 s，一分钟后可查，可接受。
- **但库大小是风险点**：合成数据 117 B/行，而合成路径（`C:\spike\123.txt`）比真实 Windows 路径短 2–3 倍。按真实路径估 200–250 B/行，**300 MB 预算 ≈ 120–150 万行**。开发机常见 100–300 万文件 → 可能顶到或超过 brief 里的 300 MB 目标。
- 因此结论是「默认根不变，但加护栏」：`meta` 里维护行数与库大小，超过阈值时停止索引并在 UI 显示「索引超限/需缩减范围」，而不是默认把用户缩小到用户目录。降级方案（写进 Task 4 的注释/后续任务即可）：若某卷为网络盘/可移动/非 NTFS 或扫描超时，`FixedNtfsRoots()` 过滤后回退到用户目录集合。
- 另：`D:` 本机几乎为空（553.4/553.6 GB 空闲），多卷枚举本身不是成本，实际成本在条目数。

### 5.5 Watcher：按根 watch + reconcile 兜底，不把 `C:\` 递归监听当默认策略

只读探针显示 `C:\` 全递归监听在 30 秒内稳定、0 错误、收到深层探针事件（39–65 事件/30 s 的空闲机器）。但事件率完全取决于环境（编译、AV 扫描、OneDrive 同步），64 KB 缓冲溢出只会以 `Error` 事件表现，容易被忽略。

结论：Task 7 按**配置根**（`SearchRoots` 解析结果）而不是字面 `C:\` 建 watcher；`Error` 一律置 `IncrementalAvailable = false` 并交给 30 秒 reconcile，这与 plan 中 `FileSystemWatcherHost.ReconcileRequested` 的设计一致。本次 spike 失败不阻塞任何东西。

### 5.6 扫描实现约束

`EnumerateFileSystemInfos()` 逐目录 + 跳过 `ReparsePoint`：22,000 条目的合成树 0 跳过，`Documents`（26k 文件）3 个跳过（权限/瞬时 IO）。watcher 与 scan 都必须吞 `UnauthorizedAccessException`/`IOException`。合成目录的创建耗时（7–16 s/22k）说明**测试里不要用真实文件铺量**做性能断言，用 `%TEMP%` + 小规模即可。

## 6. 对后续任务的具体影响

| 任务 | 影响 |
|---|---|
| Task 4（store） | `Kobold.csproj` 加 `<PlatformTarget>x64</PlatformTarget>`；留意 binding redirect（本 spike 未产生）；前缀用区间 seek；不建 FTS5 表；`idx_files_name_lower` 保留 |
| Task 4（容量） | `meta` 记录行数/库大小，超限停止并提示；不要自动缩小默认根 |
| Task 5（scan） | 逐目录 `EnumerateFileSystemInfos()` + 跳 ReparsePoint + 吞权限/IO 异常已由实测背书；吞吐按 ≥30k 条目/s 规划 |
| Task 7（watcher） | 按根建 watcher，64 KB 缓冲；`Error` → `IncrementalAvailable=false` + reconcile |
| 测试 | 只用 `%TEMP%` 小规模数据；不要断言绝对毫秒，用宽松上限（P95 目标 300 ms 有 2 倍余量） |

## 7. 复现

```bash
dotnet run --project tests/FileSearchSpike -c Release --nologo
```

退出码 0；输出所有 `key=value` 指标 + 一张 markdown 汇总表；结束后清理 `%TEMP%` 下的库、合成树和探针文件。

## 8. 已知限制

- 单机、4 次运行样本；P95 受 AV/后台负载与文件缓存影响明显（同一构建下子串 P95 从 135.2 ms 到 62.4 ms，FTS 从 488.8 ms 到 209.2 ms）。结论用「4 次运行的较差值」判定，仍远低于 300 ms。
- 合成路径短，库大小与每行字节是**下限**；真实库会更大（见 5.4）。
- 未测并发写 + 查询（单进程单连接顺序执行）；WAL + `busy_timeout=10000` 只验证了能开、能 checkpoint。
- 未测 >300k 行规模、未测 NTFS 压缩/加密目录、未测网络盘。
- `C:\` watcher 只观察了 30 秒空闲窗口，未在高 churn（编译/AV 全盘扫描）下压测缓冲溢出路径。
