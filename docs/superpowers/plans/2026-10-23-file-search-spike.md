# 2026-10-23 文件搜索 Spike 决策记录

**Task:** `2026-10-23-file-search-widget` / Task 1
**基线:** `1a042de`（本分支含 spec `d3b52cf`、plan `8b40df9`）
**Spike 工程:** `tests/FileSearchSpike`（net48 / C# 7.3，独立 console，未触碰 `Core/FileSearch`）
**修订:** 评审后修复轮，见 §9（同集合 P95 对比、跳过计数拆分、watcher 判定、targets_ok 等）

## 1. 结论表

| 项目 | 结果 |
|---|---|
| provider / 版本 | Microsoft.Data.Sqlite.Core 8.0.30 + SQLitePCLRaw.provider.e_sqlite3 2.1.12 |
| 原生 DLL 相对路径 | runtimes\win-x64\native\e_sqlite3.dll |
| 300k 插入耗时 | 2116 ms（5 次运行 2028–2459 ms） |
| 索引库大小 | 33.5 MB（35,139,584 B，WAL 已 checkpoint 截断） |
| 前缀 P95 / 子串 P95 | 54.9 / 137.4 ms（5 次运行最差 54.9 / 137.4 ms，均 < 300 ms） |
| 同集合子串 P95（与 FTS5 同为 8 关键词/160 样本） | 140.2 ms（LIKE） vs 488.7 ms（FTS5 trigram） |
| 2 万文件扫描吞吐 | 41,995 entries/s（5 次运行 36,055–91,455 entries/s，含目录） |
| C:\ watcher | 稳定（探针事件收到 + 30 秒 0 错误，12 个 Created 事件） |
| 查询后端结论 | LIKE（不启用 FTS5 trigram） |
| 默认根结论 | 所有固定 NTFS 盘（保留 `SearchRoots.FixedNtfsRoots()`），加容量上限与降级提示 |
| 300 ms / 300 MB 门槛 | `targets_ok=True`（`query_p95_lt_300ms:True`、`db_lt_300MB:True`） |

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
4. 四类查询：前缀、前缀区间、子串、计数，各 400 样本；其中长度 ≥3 的 8 个关键词的子串样本单独汇总为 `infix_3plus`（160 样本），**与 FTS5 使用完全相同的样本集合**，用于同集合对比。
5. FTS5 trigram 对照：独立 DB，`fts5(Name, Path, tokenize='trigram')`，用 `ATTACH` + `INSERT ... SELECT` 灌入同样的 300k 行；查询语句与 LIKE 一致（同样的 `ORDER BY Name LIMIT 300`）。
6. 扫描：`%TEMP%` 下 2,000 目录 × 10 文件（20,000 文件）用 `EnumerateFileSystemInfos()` 逐目录遍历；再只读扫描 `%USERPROFILE%\Documents`（存在时）。
7. `FileSystemWatcher(@"C:\")`，`IncludeSubdirectories=true`，`InternalBufferSize=64KB`，向 `%TEMP%` 写探针文件，等待事件 10 秒，然后观察 30 秒。`stable = ProbeEvent && Errors == 0`。
8. 300 ms（前缀/子串 P95）/ 300 MB（库大小）门槛在汇总处输出 `targets_ok`。

未扫描真实全盘；`C:\` 只做只读 watcher。

## 4. 测量结果

共跑 5 次：run A/B 直接执行 exe，run C 首次 `dotnet run`，run D 源码微调后的 `dotnet run`，run E 为评审修复轮后的 `dotnet run`（当前提交）。「最终 run」= run E；「5 次区间」用来说明抖动幅度，结论一律按最差值判定。

### 4.1 插入与库大小

| 指标 | 最终 run (E) | 5 次区间 |
|---|---|---|
| `insert_ms`（单事务 300k 行） | 2116 | 2028–2459 |
| 折算插入速率 | ≈142k 行/s | 122k–148k 行/s |
| `db_bytes` | 35,139,584 | 恒定 |
| `wal_bytes`（checkpoint 后） | 0 | 恒定 |
| `page_bytes`（`page_count*page_size`） | 35,139,584 | 恒定 |
| 每行占用 | ≈117 B/行 | 恒定 |

库大小在所有运行中完全一致，说明插入/建索引是确定性的；只有时间受缓存/负载影响。

### 4.2 LIKE 查询

| 查询 | 样本 | median (ms) | P95 (ms) | max (ms) | P95 5 次区间 |
|---|---|---|---|---|---|
| `NameLower LIKE 'kw%'` | 400 | 32.4 | **54.9** | 65.2 | 22.9–54.9 |
| 区间前缀 `NameLower >= 'kw' AND NameLower <= 'kw'+U+FFFF` | 400 | 0.4 | 1.2 | 1.6 | 0.5–1.2 |
| `NameLower LIKE '%kw%' OR Path LIKE '%kw%' ORDER BY Name LIMIT 300` | 400 | 98.4 | **137.4** | 160.1 | 62.4–137.4 |
| 同上，仅 ≥3 字符的 8 个关键词（**与 FTS5 同集合**） | 160 | 108.3 | **140.2** | 160.1 | 仅 run E 测得 |
| `COUNT(*)` 同上条件 | 400 | 94.3 | **128.2** | 178.9 | 58.0–130.2 |

`infix_first_kw=100 rows=300`（首个关键词 `100` 的命中被 `LIMIT 300` 截断，符合预期）。

`EXPLAIN QUERY PLAN`：

- 前缀：`SCAN files USING INDEX idx_files_name_lower` —— **全索引扫描，不是 seek**。因为 `NameLower` 是 BINARY collation，而 `LIKE` 默认大小写不敏感，SQLite 的 LIKE 优化不生效。
- 子串：`SCAN files / USE TEMP B-TREE FOR ORDER BY` —— 全表扫 + 临时 B 树排序。

### 4.3 FTS5 trigram 对照

| 指标 | 最终 run (E) | 5 次区间 |
|---|---|---|
| trigram 可用 | 是 | 是 |
| 建索引耗时（300k 行） | 3132 ms | 1485–3132 ms |
| FTS 库大小 | 45,453,312 B（43.3 MB） | 恒定 |
| 命中数一致性（`spike`） | LIKE 300,000 = FTS 300,000 | 恒定 |
| 子串 P95（160 样本，8 个 ≥3 字符关键词） | **488.7 ms** | 209.2–488.9 ms |
| median / max | 2.7 / 575.5 ms | median 1.3–3.0 |

样本 160 = 8 个长度 ≥3 的关键词 × 20 轮（trigram 无法回答 1–2 字符查询）。**同集合对比**：同一 run 内 LIKE 的 `infix_3plus` P95 = 140.2 ms，FTS5 = 488.7 ms（同为 160 样本、同一批关键词、同样的 `ORDER BY Name LIMIT 300`）。

### 4.4 扫描吞吐

| 场景 | 条目 | 耗时 | 吞吐 | 跳过 (目录 / reparse / 其他条目) |
|---|---|---|---|---|
| 合成树（20,000 文件 + 2,000 目录） | 22,000 | 523 ms | 41,995 entries/s（5 次 36,055–91,455） | 0 / 0 / 0 |
| `%USERPROFILE%\Documents` | 26,001 文件 + 1,513 目录 | 350 ms | 78,565 entries/s（5 次 62,794–130,540） | **0 / 3 / 0** |

合成树创建本身耗时 15,564 ms（5 次 6664–16,191 ms，22,000 条目），说明**创建**比扫描慢一个数量级，测试中的吞吐以遍历为准。

### 4.5 C:\ watcher

| 指标 | 最终 run (E) | 5 次区间 |
|---|---|---|
| 探针事件（在 `%TEMP%` 深层目录创建文件） | 收到（10 s 内） | 5/5 收到 |
| 30 秒窗口内 Created 事件 | 12 | 12–65 |
| `Error` 事件（缓冲溢出等） | 0 | 0 |
| 30 秒稳定性（`ProbeEvent && Errors == 0`） | 稳定 | 5/5 稳定 |

探针文件位于 `%TEMP%`（`C:\Users\<user>\AppData\Local\Temp`），距根 5 层，事件仍送达 → 递归监听从根生效。

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

**同集合对比（run E，160 样本，同样 8 个 ≥3 字符关键词，同样 `ORDER BY Name LIMIT 300`）：**

| 后端 | P95 | median |
|---|---|---|
| LIKE（`NameLower LIKE '%q%' OR Path LIKE '%q%'`） | **140.2 ms** | 108.3 ms |
| FTS5 trigram（`MATCH`） | **488.7 ms** | 2.7 ms |

- 目标「300k 行 P95 < 300 ms」：LIKE 全 20 关键词 P95 = 137.4 ms、同集合 140.2 ms、计数 128.2 ms → **达标且有余量**（`targets_ok=True`）。
- FTS5 在同集合下反而慢 3.5 倍：median 只有 2.7 ms，说明慢的是高命中关键词（`txt`/`spike` 命中 30 万行）后的大排序，而不是索引查找。LIKE 与 FTS 的查询形状完全一致，因此这是可比的差距，不是样本偏差。
- FTS5 额外成本：+43.3 MB 索引（比主表还大）、建索引 1.5–3.1 s、**查询下限 3 字符**（1–2 字符关键词无法用 trigram，会直接改变产品行为），以及一套需要与主表同步的写路径。

结论：v1 保持 `NameLower` 前缀 + `LIKE '%q%'` + `Path LIKE`，`ISearchIndexStore.Query` 签名不变。FTS5 留作后续若引入内容搜索/更大库时的选项。

### 5.3 前缀查询改用区间 seek（实现细节）

`LIKE 'kw%'` 的查询计划是**全索引扫描**（54.9 ms P95，最差 54.9 ms；300k 行下够用但会随行数线性增长）；等价区间谓词的 P95 是 **1.2 ms**（5 次最差 1.2 ms），median 0.4 ms，是真正的 seek。

建议 Task 4 在 `SearchIndexStore.Query` 内部把前缀条件写成（实测形状，含上界）：

```sql
NameLower >= @q AND NameLower <= @q || char(0x10FFFF)
```

- 绑定前必须把 `@q` 小写化（`ToLowerInvariant()`），因为 `NameLower` 存的是小写值，而区间比较是按 code point 的二进制比较，不会做大小写折叠。
- 用**闭区间 `<=`**（与实测一致）：`< @q || char(0x10FFFF)` 会把恰好等于 `q + U+10FFFF` 的路径漏掉；`<=` 则包含它。
- 实测那一轮用的是 `<= @q || char(0xFFFF)`；Task 4 统一用 `char(0x10FFFF)` 作为 BMP 之上也无歧义的上界即可。
- 外部 `ISearchIndexStore.Query` 签名不变。

### 5.4 默认根：所有固定 NTFS 盘（保留现状），v1 不设硬上限

- 本机两个固定 NTFS 卷：`C:` 400 GB、`D:` 553.6 GB，均为 Fixed/NTFS/Healthy。plan 里的 `SearchRoots.FixedNtfsRoots()` 与实测吞吐（36k–91k entries/s）匹配：2,000,000 条目的首建约 22–55 s，一分钟后可查，可接受。
- **但库大小是风险点**：合成数据 117 B/行，而合成路径（`C:\spike\123.txt`）比真实 Windows 路径短 2–3 倍。按真实路径估 200–250 B/行，**300 MB 预算 ≈ 120–150 万行**。开发机常见 100–300 万文件 → 可能顶到或超过 brief 里的 300 MB 目标。
- **v1 决策（ledger ruling）：不设行数/库大小硬上限，也不在超限时停止索引。** 300 MB 与 300 万文件只是观察目标：`meta` 记录行数、设置页显示索引大小/最后更新时间，供真实冒烟后决定是否需要护栏。加硬上限会直接违背「所有固定 NTFS 盘可搜」，因此即使超出预算也保持全盘可搜，不静默截断根或行。降级方案（写进 Task 4 的注释/后续任务即可）：若某卷为网络盘/可移动/非 NTFS 或扫描超时，`FixedNtfsRoots()` 过滤后回退到用户目录集合。
- 另：`D:` 本机几乎为空（553.4/553.6 GB 空闲），多卷枚举本身不是成本，实际成本在条目数。

### 5.5 Watcher：按根 watch + reconcile 兜底，不把 `C:\` 递归监听当默认策略

只读探针显示 `C:\` 全递归监听稳定：5/5 次都收到深层探针事件，30 秒窗口 0 错误（12–65 个 Created 事件）。但事件率完全取决于环境（编译、AV 扫描、OneDrive 同步），64 KB 缓冲溢出只会以 `Error` 事件表现，容易被忽略。

结论：Task 7 按**配置根**（`SearchRoots` 解析结果）而不是字面 `C:\` 建 watcher；`Error` 一律置 `IncrementalAvailable = false` 并交给 30 秒 reconcile，这与 plan 中 `FileSystemWatcherHost.ReconcileRequested` 的设计一致。

### 5.6 扫描实现约束（跳过计数已拆分）

`EnumerateFileSystemInfos()` 逐目录遍历 + 跳过 reparse point，跳过原因分三类计数：

| 运行 | skipped_dirs（打不开/枚举中断） | skipped_reparse（主动剪枝） | skipped_entries（属性读不到，类型未知） |
|---|---|---|---|
| 合成树 22,000 条目 | 0 | 0 | 0 |
| `%USERPROFILE%\Documents` | 0 | **3** | 0 |

即 `Documents` 的 3 个跳过项是 **reparse point**（OneDrive/已知文件夹联接一类），**不是**权限问题；上一版把三类混为一个计数并归因于「权限/瞬时 IO」是没有依据的，已在本轮修正。生产实现也必须保留这个区分：只有 `skipped_dirs > 0` 才能说明遇到访问拒绝或 IO 故障，而 reparse 是设计内的主动剪枝。watcher 与 scan 都要吞 `UnauthorizedAccessException`/`IOException`。合成目录创建耗时（6.7–16.2 s/22k）说明**测试里不要用真实文件铺量**做性能断言，用 `%TEMP%` + 小规模即可。

## 6. 对后续任务的具体影响

| 任务 | 影响 |
|---|---|
| Task 4（store） | `Kobold.csproj` 加 `<PlatformTarget>x64</PlatformTarget>`；留意 binding redirect（本 spike 未产生）；前缀用闭区间 seek 且先小写化；不建 FTS5 表；`idx_files_name_lower` 保留 |
| Task 4（容量） | `meta` 记录行数/库大小仅供设置页显示；v1 **不设硬上限**，不要自动缩小默认根或截断行 |
| Task 5（scan） | 逐目录 `EnumerateFileSystemInfos()` + 跳 ReparsePoint + 吞权限/IO 异常；**分别统计 skipped_dirs / skipped_reparse / skipped_entries**；吞吐按 ≥30k entries/s 规划 |
| Task 7（watcher） | 按根建 watcher，64 KB 缓冲；`Error` → `IncrementalAvailable=false` + reconcile |
| 测试 | 只用 `%TEMP%` 小规模数据；不要断言绝对毫秒，用宽松上限（P95 目标 300 ms 有 2 倍余量） |

## 7. 复现

```bash
dotnet run --project tests/FileSearchSpike -c Release --nologo
```

退出码 0；输出所有 `key=value` 指标 + 一张 markdown 汇总表 + `targets_ok`；结束后清理 `%TEMP%` 下的库、合成树和探针文件。

## 8. 已知限制

- 单机、5 次运行样本；P95 受 AV/后台负载与文件缓存影响明显（同一构建下子串 P95 从 62.4 ms 到 137.4 ms，FTS 从 209.2 ms 到 488.9 ms）。结论用「5 次运行的较差值」判定，仍远低于 300 ms。
- `infix_3plus`（同集合对比）只在 run E 测量，没有跨运行区间；但它与全集合 `infix` P95 在同一 run 内仅差 2.8 ms（140.2 vs 137.4），说明 1–2 字符关键词对 LIKE 的 P95 影响很小，样本集合的差异**不足以**改变结论方向。
- 合成路径短，库大小与每行字节是**下限**；真实库会更大（见 5.4）。
- 未测并发写 + 查询（单进程单连接顺序执行）；WAL + `busy_timeout=10000` 只验证了能开、能 checkpoint。
- 未测 >300k 行规模、未测 NTFS 压缩/加密目录、未测网络盘。
- `C:\` watcher 只观察了 30 秒空闲窗口，未在高 churn（编译/AV 全盘扫描）下压测缓冲溢出路径。

## 9. 修复轮（评审后）

| 评审项 | 处理 |
|---|---|
| FTS5 与 LIKE 的 P95 样本集合不一致 | 新增 `infix_3plus`：只取与 trigram 相同的 8 个 ≥3 字符关键词、160 样本，与 FTS5 在同一 run 内对比（140.2 vs 488.7 ms）。§4.2/§4.3/§5.2 已改，结论方向不变 |
| 跳过计数混用，却归因「权限/瞬时 IO」 | 拆成 `skipped_dirs` / `skipped_reparse` / `skipped_entries` 三个计数；实测 Documents 的 3 个跳过全部是 reparse point，已删除无依据的归因（§4.4/§5.6） |
| 「86,474 files/s」与 entries/s 不一致 | §1 统一为 entries/s（含目录） |
| 区间谓词 `<` 会漏 `q + U+10FFFF` | 改为实测的闭区间 `<=`，并注明绑定前必须小写化（§5.3） |
| 运行次数表述与报告不一致 | 统一为 5 次：A/B 直接执行 exe，C/D/E 为 `dotnet run`（§4 开头） |
| 决策记录里出现本机用户名路径 | 改为 `%USERPROFILE%\Documents` |
| watcher 仅用 `Errors == 0` 判稳定 | 改为 `ProbeEvent && Errors == 0`，输出名不变 |
| 缺少 300 ms / 300 MB 门槛的单一信号 | 汇总新增 `targets_ok=` 与 `targets_detail=`（本次 `True`） |
| `InfixFirstRows` 名字与语义不符 | 改名为 `FirstInfixKeyword/FirstInfixRows`，输出 `infix_first_kw=100 rows=300` |
