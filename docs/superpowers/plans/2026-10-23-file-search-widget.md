# Kobold 文件搜索组件 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Controller must not write production code; dispatch a fresh implementer per task, then a task reviewer.

**Goal:** 在 net48 / WPF / C# 7.3 的 Kobold 中加入一个无管理员权限、全盘元数据索引、可增量更新的“文件搜索”组件；v1 只搜文件名和路径，不读内容。

**Architecture:** 新增 `Kobold.Core.FileSearch` 命名空间：纯 Core 的模型/过滤/扫描/服务，SQLite 访问全部收在 `ISearchIndexStore` 后面；WPF 只放在 `Controls/SearchWidget` 与 `SearchPanelView`。一个全局 `FileSearchIndexService` 管理唯一索引库 `%LocalAppData%\Kobold\search-index.db`，所有搜索组件共享。

**Tech Stack:** .NET Framework 4.8, WPF, C# 7.3, SQLite via `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw`（Spike 决定；备选 `System.Data.SQLite.Core`）, `FileSystemWatcher`, Newtonsoft.Json 13.0.3.

**Spec:** `docs/superpowers/specs/2026-10-22-file-search-widget-design.md`（本分支 `d3b52cf` 提交）

## Global Constraints

以下约束适用于每一个任务，任务 brief 不重复解释：

- 目标框架固定 `net48`、WPF、`LangVersion 7.3`；禁止 net8/net10/Avalonia、禁止现代 C# 语法（record、init、`??=`、`using var`、`Span`、`IAsyncEnumerable` 等）。
- 进程保持 `asInvoker`；禁止 USN Journal、MFT、Windows 服务、驱动、Shell 扩展、提权 helper、`SeBackupPrivilege`。
- 索引只存元数据；禁止内容搜索、PDF/Office 解析、OCR、FTS 内容表。
- 索引库固定 `%LocalAppData%\Kobold\search-index.db`；禁止写入 `config.json`，禁止放进 Roaming。
- 所有 SQLite provider 类型（`SqliteConnection`、`SqliteCommand` 等）只能出现在 `Core/FileSearch/SearchIndexStore.cs` 与 `SqliteBootstrap.cs`；UI、服务、扫描器只依赖 `ISearchIndexStore`。
- `Core/FileSearch` 下禁止 `using System.Windows`、`System.Windows.Controls`、`System.Windows.Threading`。
- 全量扫描必须可取消；写 `files_new` 暂存表，完成后原子换表；取消/失败不得破坏旧索引。
- 批量写入 500–1000 行/事务；单写 worker；查询用只读连接；WAL + `busy_timeout=10000`。
- 搜索 200ms 防抖、可取消、结果上限 300、名称匹配优先于路径、路径补足；空查询不显示全量结果。
- UI 线程不得做目录扫描、SQLite 写或同步查询；结果列表必须虚拟化。
- 目录遍历逐个目录 `try/catch`；跳过无权限目录/reparse point 并计数；不绕过 ACL。
- 长路径文件系统调用使用 `\\?\` 前缀；存储与显示使用普通路径；超过 32,000 字符的记录跳过并计数。
- 不修改既有 folder/todo/note/色盘/右键菜单行为；只在 SearchData 上增加搜索菜单项。
- 新增 `SearchWidget`、`SearchData`、`WidgetKindRegistry` 注册、island 新建入口、三语 key、`FileSearchCheck`。
- 既有 32 项检查不回归；新增后全量 33/33；构建 0 warning。
- 所有测试只用临时目录/临时 DB；禁止扫描真实全盘（除非用户明确批准）。
- 只允许本地 commit；禁止 push、merge main、部署。

## Rulings / assumptions（计划阶段已定，写进 brief）

这些是 spec 未定稿的细节，按以下 ruling 执行；若用户评审计划时不同意，改计划再开工。

1. **Provider**：Spike 优先验证 `Microsoft.Data.Sqlite.Core 8.0.30` + `SQLitePCLRaw.provider.e_sqlite3 2.1.12` + `SQLitePCLRaw.lib.e_sqlite3 2.1.12`（本机 NuGet 缓存已有），显式 `SetProvider`，不引用会拉 `bundle_e_sqlite3` 的元包；备选 `System.Data.SQLite.Core 1.0.119`。Spike 决策写入 decision record，Task 4 按决策实现。
2. **查询后端**：v1 先 `NameLower` 前缀 + `LIKE '%q%'` + `Path LIKE`；Spike 的 300k 行 P95 若超过 300ms，Task 4 改用 FTS5 trigram，但 `ISearchIndexStore.Query` 签名不变。
3. **长路径**：扫描器用 `SearchPath.ToExtended` 调文件系统；数据库存普通路径（去掉 `\\?\`）；`Path.GetFullPath` 失败时保留原路径；超过 32,000 字符跳过并计入 `SkippedLongPaths`。
4. **面板尺寸**：内容 420×500；面板默认 422×562（chrome header + border）；最小 340×320；行高 44。
5. **Reconcile 成本**：周期 Reconcile 仍遍历目录树，但只对差异行做 UPSERT，并用 `reconcile_seen` 清理死链；不重建 `files` 表。默认 10 分钟，0 关闭；启动后 30 秒首次后台 Reconcile。
6. **配置 fingerprint**：`FileSearchSettings.ComputeFingerprint(resolvedRoots)` 覆盖 roots/types/IndexAllFiles/IndexFolders/IndexExtensionless/excludes；库内 `meta.settings_fingerprint` 不一致时 UI 显示 “需要重建”，不自动删除旧库。
7. **卸载策略**：卸载器 `All` 选项删除 `%LocalAppData%\Kobold\search-index.db*`；`ConfigOnly` 只删 Roaming config；索引库目录中的其他文件不动。
8. **SearchData**：只继承公共字段，不持久化搜索词、不持久化结果；`Kind = "search"`。

## File Structure

```text
Core/FileSearch/
  FileSearchSettings.cs       配置对象、默认类型、Normalize、fingerprint
  SearchEntry.cs              一条索引记录（普通路径）
  SearchPath.cs               路径归一化、扩展名、\\?\ 转换、IsUnder
  SearchTypeFilter.cs         默认类型/全部文件/无扩展名
  SearchDirectoryFilter.cs    默认/用户排除目录、reparse 判断辅助
  SearchRoots.cs              固定 NTFS 盘枚举、配置根解析
  ISearchIndexStore.cs        写/读/状态接口（无 provider 类型）
  SearchIndexStore.cs         SQLite schema、批量写、查询、换表、VACUUM
  SqliteBootstrap.cs          唯一 provider 初始化点
  DirectoryScanner.cs         可取消目录遍历、暂存/对账模式
  ScanProgress.cs             ScanProgress / ScanResult
  FileSystemWatcherHost.cs    每根 watcher、事件队列、单 worker
  FileSearchIndexService.cs   全局单例、生命周期、状态、全量/对账/暂停
  SearchQueryResult.cs        查询结果模型
  IndexStatus.cs              索引状态/大小/最后更新时间

Core/SearchData.cs             SearchData : WidgetData
Controls/SearchWidget.xaml(.cs)
Controls/SearchPanelView.xaml(.cs)
Controls/IconRenderers/SearchIconStyles.cs

tests/FileSearchCheck/
  FileSearchCheck.csproj
  Program.cs
tests/FileSearchSpike/
  FileSearchSpike.csproj
  Program.cs
docs/superpowers/plans/2026-10-23-file-search-spike.md
```

---

### Task 1: Spike — SQLite provider、扫描吞吐、搜索 P95、C:\ watcher

**Files:**
- Create: `tests/FileSearchSpike/FileSearchSpike.csproj`
- Create: `tests/FileSearchSpike/Program.cs`
- Create: `docs/superpowers/plans/2026-10-23-file-search-spike.md`

**Interfaces:**
- Consumes: 无（独立 console spike）。
- Produces: 决策记录，至少包含：provider 包名/版本、原生 DLL 相对路径、300k 行插入耗时、索引库大小、前缀/子串查询 P95、C:\ watcher 结果、是否改用 FTS5、是否调整默认根。

- [ ] **Step 1: 创建 spike 项目**

`tests/FileSearchSpike/FileSearchSpike.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>Kobold.FileSearchSpike</AssemblyName>
    <RootNamespace>Kobold.FileSearchSpike</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite.Core" Version="8.0.30" />
    <PackageReference Include="SQLitePCLRaw.provider.e_sqlite3" Version="2.1.12" />
    <PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" Version="2.1.12" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写 provider 初始化 + schema + 300k 合成行插入**

`Program.cs` 必须包含：

```csharp
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
SQLitePCL.raw.FreezeProvider();

string db = Path.Combine(Path.GetTempPath(), "kobold-spike-" + Guid.NewGuid().ToString("N") + ".db");
var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
using (var c = new SqliteConnection(cs))
{
    c.Open();
    Exec(c, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA synchronous=NORMAL;");
    Exec(c, @"CREATE TABLE files (Path TEXT PRIMARY KEY COLLATE NOCASE, Name TEXT NOT NULL,
        NameLower TEXT NOT NULL, Extension TEXT NOT NULL, IsDirectory INTEGER NOT NULL,
        SizeBytes INTEGER NOT NULL, ModifiedUtc INTEGER NOT NULL, Root TEXT NOT NULL);
        CREATE INDEX idx_files_name_lower ON files(NameLower);");
    var sw = Stopwatch.StartNew();
    using (var tx = c.BeginTransaction())
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO files VALUES (@p,@n,@nl,@e,0,@s,@m,@r)";
        for (int i = 0; i < 300000; i++)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@p", @"C:\spike\" + i + ".txt");
            cmd.Parameters.AddWithValue("@n", i + ".txt");
            cmd.Parameters.AddWithValue("@nl", (i + ".txt").ToLowerInvariant());
            cmd.Parameters.AddWithValue("@e", "txt");
            cmd.Parameters.AddWithValue("@s", (long)i);
            cmd.Parameters.AddWithValue("@m", 1700000000L + i);
            cmd.Parameters.AddWithValue("@r", @"C:\");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    Console.WriteLine("insert_ms=" + sw.ElapsedMilliseconds);
}
```

- [ ] **Step 3: 测量前缀/子串查询 P95 与库大小**

对 20 个固定关键词（如 `100`、`999`、`spike`、`txt`、`25`）各跑 20 次，取 P95；分别测：

```sql
SELECT Path, Name FROM files WHERE NameLower LIKE @prefix ORDER BY NameLower LIMIT 300;
SELECT Path, Name FROM files WHERE NameLower LIKE @contains OR Path LIKE @contains ORDER BY Name LIMIT 300;
SELECT COUNT(*) FROM files WHERE NameLower LIKE @contains OR Path LIKE @contains;
```

记录 `query_prefix_p95_ms`、`query_infix_p95_ms`、`db_bytes`、`wal_bytes`。目标：300k 行 P95 < 300ms，库 < 300MB。

- [ ] **Step 4: 小范围真实扫描（不扫全盘）**

在 `%TEMP%` 下创建 20,000 个文件 + 2,000 个目录，用 `DirectoryInfo.EnumerateFileSystemInfos()` 逐目录遍历，记录文件数、目录数、耗时、每秒条目数。再用只读方式扫描 `%USERPROFILE%\Documents`（存在时）或仓库 `docs` 目录，记录可读/跳过目录数。不要扫描 `C:\`。

- [ ] **Step 5: C:\ 根 watcher 稳定性**

```csharp
using (var w = new FileSystemWatcher(@"C:\"))
{
    w.IncludeSubdirectories = true;
    w.InternalBufferSize = 64 * 1024;
    w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
        | NotifyFilters.Size | NotifyFilters.LastWrite;
    var seen = new ManualResetEventSlim();
    w.Created += (s, e) => { if (e.FullPath.StartsWith(tempProbe, StringComparison.OrdinalIgnoreCase)) seen.Set(); };
    w.Error += (s, e) => Console.WriteLine("watcher_error=" + e.GetException().Message);
    w.EnableRaisingEvents = true;
    File.WriteAllText(Path.Combine(Path.GetTempPath(), "kobold-spike-watch.txt"), "x");
    Console.WriteLine("watcher_c_root=" + seen.Wait(10000));
}
```

记录是否收到事件、是否报错、30 秒内是否稳定。watcher 失败不使 spike 失败；记录降级建议。

- [ ] **Step 6: 写 decision record 并运行**

`docs/superpowers/plans/2026-10-23-file-search-spike.md` 必须包含一张表和一段结论：

```text
| 项目 | 结果 |
|---|---|
| provider / 版本 | Microsoft.Data.Sqlite.Core 8.0.30 + SQLitePCLRaw.provider.e_sqlite3 2.1.12 |
| 原生 DLL 相对路径 | runtimes\win-x64\native\e_sqlite3.dll |
| 300k 插入耗时 | <实测> ms |
| 索引库大小 | <实测> MB |
| 前缀 P95 / 子串 P95 | <实测> / <实测> ms |
| 2 万文件扫描吞吐 | <实测> files/s |
| C:\ watcher | 稳定 / 不稳定（说明） |
| 查询后端结论 | LIKE / FTS5 trigram |
| 默认根结论 | 所有固定 NTFS 盘 / 降级方案 |
```

Run: `dotnet run --project tests/FileSearchSpike -c Release --nologo`
Expected: exit 0；输出所有指标；`dotnet build Kobold.csproj -c Release` 仍 0 warning（spike 项目不影响 app）。

- [ ] **Step 7: Commit**

```bash
git add tests/FileSearchSpike docs/superpowers/plans/2026-10-23-file-search-spike.md
git commit -m "test(file-search): spike sqlite provider and scan throughput"
```

---

### Task 2: Core 模型、类型过滤、目录排除、根解析

**Files:**
- Create: `Core/FileSearch/FileSearchSettings.cs`
- Create: `Core/FileSearch/SearchEntry.cs`
- Create: `Core/FileSearch/SearchPath.cs`
- Create: `Core/FileSearch/SearchTypeFilter.cs`
- Create: `Core/FileSearch/SearchDirectoryFilter.cs`
- Create: `Core/FileSearch/SearchRoots.cs`
- Create: `tests/FileSearchCheck/FileSearchCheck.csproj`
- Create: `tests/FileSearchCheck/Program.cs`

**Interfaces:**
- Consumes: 无。
- Produces:
  - `FileSearchSettings.CreateDefault()`, `Normalize()`, `ComputeFingerprint(IReadOnlyList<string> roots)`
  - `SearchEntry` 的 8 个持久化字段 + `NameLower` + `static bool TryCreate(string root, FileSystemInfo info, out SearchEntry entry)`
  - `SearchPath.Normalize`, `.ToExtended`, `.FromExtended`, `.GetExtension`, `.IsUnder`
  - `SearchTypeFilter.IsFileIncluded(string path, FileSearchSettings settings)`
  - `SearchDirectoryFilter.IsExcluded(string path, string name, FileSearchSettings settings)`
  - `SearchRoots.Resolve(string[] configured)`, `SearchRoots.FixedNtfsRoots()`

- [ ] **Step 1: 创建测试项目并写失败测试**

`tests/FileSearchCheck/FileSearchCheck.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <AssemblyName>Kobold.FileSearchCheck</AssemblyName>
    <RootNamespace>Kobold.FileSearchCheck</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Kobold.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs` 的骨架：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kobold.Core.FileSearch;

namespace Kobold.FileSearchCheck
{
    internal static class Program
    {
        private static readonly List<string> Errors = new List<string>();
        private static string _root;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            _root = Path.Combine(Path.GetTempPath(), "kobold-filesearch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try { RunAll(); }
            finally { try { Directory.Delete(_root, true); } catch { } }
            Console.WriteLine();
            if (Errors.Count == 0) { Console.WriteLine("ALL FILE-SEARCH CHECKS PASSED"); return 0; }
            Console.WriteLine("FAILURES (" + Errors.Count + "):");
            foreach (var e in Errors) Console.WriteLine("  - " + e);
            return 1;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("PASS " + what); else Errors.Add(what);
        }

        private static void RunAll()
        {
            TypeFilterCases();
            ExcludeCases();
            PathCases();
            SettingsDefaults();
        }
    }
}
```

先写这四组用例（精确断言）：

```csharp
private static void TypeFilterCases()
{
    var s = FileSearchSettings.CreateDefault();
    Check(SearchTypeFilter.IsFileIncluded(@"C:\a\report.PDF", s), "type: default includes PDF case-insensitively");
    Check(!SearchTypeFilter.IsFileIncluded(@"C:\a\app.exe", s), "type: default excludes EXE");
    Check(SearchTypeFilter.IsFileIncluded(@"C:\a\README", s), "type: extensionless included by default");
    s.IndexExtensionless = false;
    Check(!SearchTypeFilter.IsFileIncluded(@"C:\a\README", s), "type: extensionless can be disabled");
    s = FileSearchSettings.CreateDefault();
    s.IndexAllFiles = true;
    Check(SearchTypeFilter.IsFileIncluded(@"C:\a\app.exe", s), "type: all-files includes EXE");
    Check(SearchTypeFilter.IsFileIncluded(@"C:\a\archive.tar.gz", s), "type: all-files includes multi-dot names");
    s.IndexAllFiles = false;
    Check(SearchTypeFilter.IsFileIncluded(@"C:\a\archive.tar.gz", s), "type: .tar.gz is classified by the last extension (gz)");
}

private static void ExcludeCases()
{
    var s = FileSearchSettings.CreateDefault();
    string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    Check(SearchDirectoryFilter.IsExcluded(Path.Combine(windows, "WinSxS", "x"), "x", s),
        "exclude: WinSxS full path is excluded");
    Check(SearchDirectoryFilter.IsExcluded(@"C:\p\node_modules\a", "a", s),
        "exclude: node_modules name is excluded at any depth");
    Check(SearchDirectoryFilter.IsExcluded(@"C:\p\.git\objects", "objects", s),
        "exclude: a child of .git is excluded by its parent path");
    Check(!SearchDirectoryFilter.IsExcluded(@"C:\p\src", "src", s),
        "exclude: normal source directory is not excluded");
    s.ExcludeDirectories = new[] { @"C:\p\private" };
    Check(SearchDirectoryFilter.IsExcluded(@"C:\p\private\a", "a", s),
        "exclude: user full path is excluded with boundary");
    Check(!SearchDirectoryFilter.IsExcluded(@"C:\p\privateer", "privateer", s),
        "exclude: user path does not match a sibling with the same prefix");
    s.ExcludeDirectories = new[] { "generated" };
    Check(SearchDirectoryFilter.IsExcluded(@"C:\p\x\generated", "generated", s),
        "exclude: user name is excluded");
}

private static void PathCases()
{
    Check(SearchPath.GetExtension(@"C:\a\report.PDF") == "pdf", "path: extension is lowercase without the dot");
    Check(SearchPath.GetExtension(@"C:\a\README") == "", "path: extensionless is empty");
    Check(SearchPath.GetExtension(@"C:\a\archive.tar.gz") == "gz", "path: last extension wins");
    Check(SearchPath.IsUnder(@"C:\p\a\b.txt", @"C:\p"), "path: IsUnder matches an ancestor");
    Check(!SearchPath.IsUnder(@"C:\privateer\a.txt", @"C:\p"), "path: IsUnder respects the separator boundary");
    string extended = SearchPath.ToExtended(@"C:\p\a.txt");
    Check(extended == @"\\?\C:\p\a.txt", "path: normal path gets the \\\\?\\ prefix");
    Check(SearchPath.FromExtended(extended) == @"C:\p\a.txt", "path: extended prefix is removed for storage");
    Check(SearchPath.ToExtended(@"\\server\share\a") == @"\\?\UNC\server\share\a", "path: UNC gets the UNC extended form");
}

private static void SettingsDefaults()
{
    var s = FileSearchSettings.CreateDefault();
    Check(s.Types != null && Array.IndexOf(s.Types, "pdf") >= 0 && Array.IndexOf(s.Types, "cs") >= 0,
        "settings: default types include the spec's document and code types");
    Check(s.IndexFolders && s.IndexExtensionless && !s.IndexAllFiles && s.ReconcileMinutes == 10,
        "settings: defaults are folders/extensionless on, all-files off, reconcile 10");
    string a = s.ComputeFingerprint(new[] { @"C:\" });
    s.Types = new[] { "pdf" };
    string b = s.ComputeFingerprint(new[] { @"C:\" });
    Check(a != b, "settings: fingerprint changes with types");
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: 编译失败或 `FileSearchSettings` 未定义。

- [ ] **Step 3: 实现模型与过滤**

`FileSearchSettings.cs` 关键代码：

```csharp
public sealed class FileSearchSettings
{
    public bool Enabled { get; set; }
    public bool Configured { get; set; }
    public string[] Roots { get; set; }
    public bool IndexAllFiles { get; set; }
    public string[] Types { get; set; }
    public bool IndexFolders { get; set; }
    public bool IndexExtensionless { get; set; }
    public string[] ExcludeDirectories { get; set; }
    public int ReconcileMinutes { get; set; }

    public static readonly string[] DefaultTypes =
    {
        "pdf","doc","docx","xls","xlsx","ppt","pptx","txt","md","rtf","csv",
        "jpg","jpeg","png","gif","bmp","webp","svg","heic",
        "mp4","mkv","avi","mov","mp3","wav","flac",
        "zip","7z","rar","tar","gz",
        "cs","js","ts","py","java","json","xml","yaml","yml","sql","html","css",
        "lnk","url"
    };

    public static FileSearchSettings CreateDefault()
    {
        return new FileSearchSettings
        {
            Enabled = true,
            Configured = false,
            Roots = new string[0],
            IndexAllFiles = false,
            Types = (string[])DefaultTypes.Clone(),
            IndexFolders = true,
            IndexExtensionless = true,
            ExcludeDirectories = new string[0],
            ReconcileMinutes = 10
        };
    }

    public void Normalize()
    {
        Roots = Clean(Roots);
        ExcludeDirectories = Clean(ExcludeDirectories);
        Types = CleanExtensions(Types);
        if (Types.Length == 0 && !IndexAllFiles) Types = (string[])DefaultTypes.Clone();
        if (ReconcileMinutes < 0) ReconcileMinutes = 0;
        if (ReconcileMinutes > 1440) ReconcileMinutes = 1440;
    }

    public string ComputeFingerprint(IReadOnlyList<string> resolvedRoots)
    {
        var parts = new List<string>();
        foreach (var r in resolvedRoots ?? new string[0]) parts.Add(r.TrimEnd('\\').ToLowerInvariant());
        parts.Sort(StringComparer.Ordinal);
        parts.Add(IndexAllFiles ? "all" : string.Join(",", Types ?? new string[0]));
        parts.Add("folders=" + IndexFolders);
        parts.Add("extless=" + IndexExtensionless);
        var ex = new List<string>(ExcludeDirectories ?? new string[0]);
        ex.Sort(StringComparer.OrdinalIgnoreCase);
        parts.Add(string.Join("|", ex));
        return string.Join(";", parts);
    }
}
```

`SearchEntry.TryCreate(root, info, out entry)`：`info` 为 `FileSystemInfo`；读取 `Attributes`/`LastWriteTimeUtc`；目录 `SizeBytes=0`、`IsDirectory=true`；文件大小取 `((FileInfo)info).Length`；路径去 `\\?\` 前缀后存普通路径；任何 `IOException`/`UnauthorizedAccessException` 返回 false。扫描器和 watcher 共用它。

`SearchTypeFilter` 必须按最后一个扩展名判断，扩展名空走 `IndexExtensionless`，`IndexAllFiles` 优先。`SearchDirectoryFilter` 内置：

```csharp
DefaultExcludedNames = { "$Recycle.Bin", "System Volume Information", "node_modules", ".git", ".svn",
    "blob_storage", "WXWorkCefCache", "WasmTtsEngine", "GPUCache", "Code Cache", "npm-cache", "NuGet", "pip", "Cargo" };
DefaultExcludedPaths = { Windows\WinSxS, Windows\System32\config, Windows\ServiceProfiles, Windows\Temp,
    Path.GetTempPath() } // %TEMP% 通常就是 C:\Users\<user>\AppData\Local\Temp
```

`SearchDirectoryFilter.IsExcluded` 先做名称/路径完整匹配，再检查用户追加；完整路径匹配必须加分隔符边界，否则 `C:\p\privateer` 不能命中 `C:\p\private`。`SearchRoots` 用 `DriveInfo.GetDrives()`，筛 `DriveType.Fixed` 且 `DriveFormat` 等于 `NTFS`（不区分大小写）；空 `Roots` 返回所有固定 NTFS 根，非空则归一化、去重、只保留存在目录。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `ALL FILE-SEARCH CHECKS PASSED`。

- [ ] **Step 5: Commit**

```bash
git add Core/FileSearch tests/FileSearchCheck
git commit -m "feat(file-search): add core models and scope filters"
```

---

### Task 3: FileSearch 配置进入 AppConfig

**Files:**
- Create: `Core/SearchData.cs`
- Modify: `Core/AppConfig.cs`
- Modify: `tests/FileSearchCheck/Program.cs`
- Modify: `tests/WidgetKindsCheck/Program.cs`
- Modify: `tests/ConfigSafetyCheck/Program.cs`（若存在配置字段枚举/结构断言）

**Interfaces:**
- Consumes: Task 2 的 `FileSearchSettings`。
- Produces: `AppConfig.FileSearch`（`FileSearchSettings`，默认 null）；`SearchData`（仅数据模型，本任务不注册到 kind registry）；配置 round-trip/normalize；旧配置缺字段安全。

- [ ] **Step 1: 写失败测试**

在 `Program.cs` 增加 `SearchDataModelCase()` 与 `ConfigCases()`：

```csharp
private static void SearchDataModelCase()
{
    var data = new Kobold.Core.SearchData();
    Check(data.Kind == "search", "search data: the kind is search");
}
```

```csharp
private static void ConfigCases()
{
    string dir = Path.Combine(_root, "config");
    Directory.CreateDirectory(dir);
    string main = Path.Combine(dir, "config.json");
    string backup = main + ".backup";

    File.WriteAllText(main, @"{ ""FileSearch"": { ""Configured"": true, ""IndexAllFiles"": true,
        ""Types"": [""PDF"", "".CS""], ""ReconcileMinutes"": -5 } }");
    var cfg = Kobold.Core.AppConfig.Load(main, backup);
    Check(cfg.FileSearch != null && cfg.FileSearch.Configured && cfg.FileSearch.IndexAllFiles,
        "config: FileSearch round-trips its booleans");
    Check(cfg.FileSearch != null && Array.IndexOf(cfg.FileSearch.Types, "pdf") >= 0
          && Array.IndexOf(cfg.FileSearch.Types, "cs") >= 0,
        "config: types are normalized to lowercase without dots");
    Check(cfg.FileSearch != null && cfg.FileSearch.ReconcileMinutes == 0,
        "config: negative reconcile minutes normalize to 0");

    File.WriteAllText(main, @"{ ""Language"": ""en"" }");
    cfg = Kobold.Core.AppConfig.Load(main, backup);
    Check(cfg.FileSearch == null, "config: a missing FileSearch stays null (unconfigured)");

    cfg.FileSearch = FileSearchSettings.CreateDefault();
    cfg.FileSearch.Configured = true;
    cfg.Save();
    var reloaded = Kobold.Core.AppConfig.Load(main, backup);
    Check(reloaded.FileSearch != null && reloaded.FileSearch.Configured,
        "config: a saved FileSearch reloads");
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: 编译失败（`AppConfig.FileSearch` 不存在）。

- [ ] **Step 3: 实现**

在 `AppConfig`：

```csharp
[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
public FileSearchSettings FileSearch { get; set; }
```

构造函数不初始化（保持 null）。`Load` 在 `RepairLegacyItems` 后：

```csharp
if (loaded.FileSearch != null) loaded.FileSearch.Normalize();
```

`RepairLegacyItems` 不强制创建对象；`Normalize()` 负责 null 数组。`Save` 用现有 JSON 序列化，`NullValueHandling.Ignore` 保证未配置不写空对象。

`Core/SearchData.cs`：

```csharp
namespace Kobold.Core
{
    /// <summary>Search widget data: shared fields only; query text is never persisted.</summary>
    public class SearchData : WidgetData
    {
        public SearchData() { Kind = "search"; }
    }
}
```

同时更新 `WidgetKindsCheck` 中“config 字段/结构”若存在显式字段数断言；没有就只加一条 `FileSearch` round-trip 断言。`ConfigSafetyCheck` 若枚举 `AppConfig` 属性做 null 检查，补 `FileSearch` 允许 null。

- [ ] **Step 4: 运行两个检查**

Run:
```bash
dotnet run --project tests/FileSearchCheck -c Release --nologo
dotnet run --project tests/WidgetKindsCheck -c Release --nologo
```
Expected: 全 PASS。

- [ ] **Step 5: Commit**

```bash
git add Core/AppConfig.cs Core/SearchData.cs tests/FileSearchCheck tests/WidgetKindsCheck tests/ConfigSafetyCheck
git commit -m "feat(file-search): persist file search settings in config"
```

---

### Task 4: SQLite 索引存储与查询

**Files:**
- Create: `Core/FileSearch/ISearchIndexStore.cs`
- Create: `Core/FileSearch/SearchIndexStore.cs`
- Create: `Core/FileSearch/SqliteBootstrap.cs`
- Create: `Core/FileSearch/SearchQueryResult.cs`
- Create: `Core/FileSearch/IndexStatus.cs`
- Modify: `Kobold.csproj`
- Modify: `tests/FileSearchCheck/Program.cs`

**Interfaces:**
- Consumes: Task 1 的 provider 决策；Task 2 的 `SearchEntry` / `FileSearchSettings`。
- Produces（后续任务只依赖这些签名）：

```csharp
public interface ISearchIndexStore : IDisposable
{
    string DatabasePath { get; }
    void EnsureSchema();
    void BeginFullScan();
    void UpsertScanBatch(IReadOnlyList<SearchEntry> entries);
    void CompleteFullScan(long skippedDirectories, long skippedReparsePoints, long skippedLongPaths, string fingerprint);
    void AbortFullScan();

    void BeginReconcile();
    void MarkSeenBatch(IReadOnlyList<string> paths);
    void CompleteReconcile(IReadOnlyList<string> roots);
    void AbortReconcile();

    void Upsert(SearchEntry entry);
    void Delete(string path);
    void DeletePathAndPrefix(string path);

    SearchQueryResult Query(string text, int limit, CancellationToken token);
    IndexStatus ReadStatus();
    long GetDatabaseSizeBytes();
    void Checkpoint();
    void Vacuum();
}
```

`SearchQueryResult`：`IReadOnlyList<SearchEntry> Items`, `int TotalMatches`, `bool LimitReached`。
`IndexStatus`：`bool HasIndex`, `DateTime? LastScanUtc`, `DateTime? LastReconcileUtc`, `long SkippedDirectories`, `string LastError`, `string SettingsFingerprint`, `int SchemaVersion`。`NeedsRebuild` 由 service 用 `SettingsFingerprint` 与当前 settings 比较后写进 `FileSearchStatus`，store 不保存“当前设置”。

- [ ] **Step 1: 写失败测试**

增加 `StoreCases()`：

```csharp
private static void StoreCases()
{
    string db = Path.Combine(_root, "store.db");
    using (var store = new SearchIndexStore(db))
    {
        store.EnsureSchema();
        store.BeginFullScan();
        var rows = new List<SearchEntry>
        {
            Entry(@"C:\p\Alpha.txt", "Alpha.txt", "txt", false, 10, @"C:\"),
            Entry(@"C:\p\alphabet.md", "alphabet.md", "md", false, 20, @"C:\"),
            Entry(@"C:\p\子目录", "子目录", "", true, 0, @"C:\"),
            Entry(@"C:\p\sub\Beta.txt", "Beta.txt", "txt", false, 30, @"C:\"),
        };
        store.UpsertScanBatch(rows);
        store.CompleteFullScan(2, 1, 0, "fp");
        Check(store.ReadStatus().HasIndex, "store: full scan publishes an index");

        var q = store.Query("alpha", 300, CancellationToken.None);
        Check(q.TotalMatches == 2 && q.Items.Count == 2 && q.Items[0].Name == "Alpha.txt",
            "store: name prefix ranks before name infix and is case-insensitive");
        q = store.Query("beta", 300, CancellationToken.None);
        Check(q.Items.Count == 1 && q.Items[0].Name == "Beta.txt", "store: path/name match works");
        q = store.Query("子", 300, CancellationToken.None);
        Check(q.Items.Count == 1 && q.Items[0].IsDirectory, "store: Chinese names and folders are searchable");
        q = store.Query("%", 300, CancellationToken.None);
        Check(q.TotalMatches == 0, "store: LIKE wildcards are escaped");
        q = store.Query("a", 1, CancellationToken.None);
        Check(q.Items.Count == 1 && q.LimitReached, "store: the 300/limit cap is reported");

        store.DeletePathAndPrefix(@"C:\p\sub");
        Check(store.Query("beta", 300, CancellationToken.None).TotalMatches == 0,
            "store: deleting a directory removes its prefix subtree");
        store.Checkpoint();
        store.Vacuum();
    }

    string db2 = Path.Combine(_root, "cancel.db");
    using (var store = new SearchIndexStore(db2))
    {
        store.EnsureSchema();
        store.BeginFullScan();
        store.UpsertScanBatch(new[] { Entry(@"C:\old\a.txt", "a.txt", "txt", false, 1, @"C:\") });
        store.CompleteFullScan(0, 0, 0, "old");
        store.BeginFullScan();
        store.UpsertScanBatch(new[] { Entry(@"C:\new\b.txt", "b.txt", "txt", false, 1, @"C:\") });
        store.AbortFullScan();
        Check(store.Query("a", 300, CancellationToken.None).TotalMatches == 1,
            "store: aborting a full scan keeps the old index intact");
    }
}
```

`Entry` helper 构造 `SearchEntry`。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `SearchIndexStore` 未定义。

- [ ] **Step 3: 加包并实现**

`Kobold.csproj` 加（Task 1 若选备选 provider，按 decision record 替换）：

```xml
<PropertyGroup>
  <!-- Spike 6b7b561: AnyCPU + NuGet 会复制 win-x86 原生 DLL，x64 进程加载报 BadImageFormatException。
       钉死 x64；32 位 Windows 不在 v1 范围。 -->
  <PlatformTarget>x64</PlatformTarget>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite.Core" Version="8.0.30" />
  <PackageReference Include="SQLitePCLRaw.provider.e_sqlite3" Version="2.1.12" />
  <PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" Version="2.1.12" />
</ItemGroup>
```

若运行/构建出现 `System.Memory`/`System.Buffers` 的 `FileLoadException`，再加 `<AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>`；Spike 本机未触发。`EnsureSchema` 启动时执行 `SELECT sqlite_version()` 并写入 `meta.sqlite_version`，便于以后核对原生库版本。

`SqliteBootstrap.EnsureInitialized()`：

```csharp
private static bool _initialized;
private static readonly object Gate = new object();
public static void EnsureInitialized()
{
    if (_initialized) return;
    lock (Gate)
    {
        if (_initialized) return;
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        SQLitePCL.raw.FreezeProvider();
        _initialized = true;
    }
}
```

`SearchIndexStore` 构造时调用 bootstrap；所有写方法（`Upsert*`/`Delete*`/`Begin*`/`Complete*`/`Abort*`/`Checkpoint`/`Vacuum`）共用一把 `_writeGate` 锁，保证进程内单写者；查询不拿写锁，使用独立只读连接。连接串：

```csharp
var builder = new SqliteConnectionStringBuilder {
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared
};
```

`EnsureSchema` 执行 spec 的 `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA user_version=1;`，建 `files`/`meta`/索引；若 `files` 不存在则建空表。**只有 `SearchIndexStore.cs` 可引用 `Microsoft.Data.Sqlite`。**

`BeginFullScan`：

```sql
DROP TABLE IF EXISTS files_new;
CREATE TABLE files_new (
    Path TEXT PRIMARY KEY COLLATE NOCASE, Name TEXT NOT NULL, NameLower TEXT NOT NULL,
    Extension TEXT NOT NULL, IsDirectory INTEGER NOT NULL, SizeBytes INTEGER NOT NULL,
    ModifiedUtc INTEGER NOT NULL, Root TEXT NOT NULL);
```

`CompleteFullScan` 在一个事务里：

```sql
DROP TABLE IF EXISTS files;
ALTER TABLE files_new RENAME TO files;
CREATE INDEX idx_files_name_lower ON files(NameLower);
CREATE INDEX idx_files_extension ON files(Extension);
CREATE INDEX idx_files_root ON files(Root);
```

并写 `meta.last_scan_utc`、`meta.skipped_dirs`、`meta.skipped_reparse`、`meta.skipped_long_paths`、`meta.settings_fingerprint`、`meta.last_error = ''`。`AbortFullScan` 只 `DROP TABLE IF EXISTS files_new`。

`BeginReconcile` 建 `reconcile_seen(Path TEXT PRIMARY KEY COLLATE NOCASE)`；`CompleteReconcile` 对每个 root 执行：

```sql
DELETE FROM files
 WHERE Root = @root
   AND NOT EXISTS (SELECT 1 FROM reconcile_seen s WHERE s.Path = files.Path);
```

然后 drop `reconcile_seen`，写 `meta.last_reconcile_utc`。`MarkSeenBatch` 只写 seen 表。

`Upsert` 使用 `INSERT ... ON CONFLICT(Path) DO UPDATE SET ...`；`DeletePathAndPrefix`：

```sql
DELETE FROM files WHERE Path = @p OR Path LIKE @prefix ESCAPE '\';
```

`prefix` 由 `SearchPath.EscapeLike(path.TrimEnd('\\')) + "\\%"` 得到。

`Query`：

Spike 实测 `LIKE 'q%'` 走全索引扫描（22.9ms），等价区间谓词走 seek（0.5ms）；所以 Query 用两段式，先取名称前缀，再补名称子串/路径：

```sql
-- 总数（用于状态栏“共 N 条匹配”）
SELECT COUNT(*) FROM files
WHERE NameLower LIKE @contains ESCAPE '\' OR Path LIKE @contains ESCAPE '\';

-- 第一段：名称前缀，走 idx_files_name_lower
SELECT Path,Name,Extension,IsDirectory,SizeBytes,ModifiedUtc,Root,0 AS MatchRank
FROM files
WHERE NameLower >= @q AND NameLower <= @q || char(0x10FFFF)
ORDER BY NameLower, Path
LIMIT @limit;

-- 第二段：前缀不够 limit 时补名称子串/路径，排除已取的前缀行
SELECT Path,Name,Extension,IsDirectory,SizeBytes,ModifiedUtc,Root,
       CASE WHEN NameLower LIKE @contains ESCAPE '\' THEN 1 ELSE 2 END AS MatchRank
FROM files
WHERE (NameLower LIKE @contains ESCAPE '\' OR Path LIKE @contains ESCAPE '\')
  AND NOT (NameLower >= @q AND NameLower <= @q || char(0x10FFFF))
ORDER BY MatchRank, NameLower, Path
LIMIT @remaining;
```

`@q` 必须是 `ToLowerInvariant()` 后的 query；上界 `<= @q || char(0x10FFFF)` 与 `LIKE 'q%'` 等价（包含 `q + U+10FFFF` 自身），不要改成 `<`。`@contains` = `%` + escaped lower query + `%`。`Items = prefixRows + suffixRows`；`TotalMatches` 取 count；`LimitReached = TotalMatches > Items.Count`。空/空白查询直接返回空结果，不查库。查询连接用 `Mode=ReadOnly`，每个查询新建并 `using` 释放。

容量：v1 **不设硬上限**（全盘可搜优先）；`GetDatabaseSizeBytes()` 与 `meta` 行数只用于设置页显示和 spike 后续观察。若真实冒烟发现 >300MB，再单独决定是否加护栏；不要静默截断根或行。

`ReadStatus` 查 `meta` 和 `PRAGMA user_version`；`GetDatabaseSizeBytes` 返回主文件 + `-wal` + `-shm` 的总和（不存在按 0）。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `ALL FILE-SEARCH CHECKS PASSED`。

- [ ] **Step 5: Commit**

```bash
git add Kobold.csproj Core/FileSearch tests/FileSearchCheck
git commit -m "feat(file-search): add sqlite index store and query"
```

---

### Task 5: 可取消目录扫描器与全量换表

**Files:**
- Create: `Core/FileSearch/ScanProgress.cs`
- Create: `Core/FileSearch/DirectoryScanner.cs`
- Modify: `tests/FileSearchCheck/Program.cs`

**Interfaces:**
- Consumes: `ISearchIndexStore`（Task 4）、`SearchTypeFilter`/`SearchDirectoryFilter`/`SearchRoots`/`SearchPath`（Task 2）。
- Produces:

```csharp
public enum ScanMode { Full, Reconcile }

public sealed class ScanProgress
{
    public long Directories; public long Files; public long SkippedDirectories;
    public long SkippedReparsePoints; public long SkippedLongPaths; public string CurrentDirectory;
}

public sealed class ScanResult
{
    public long Directories; public long Files; public long SkippedDirectories;
    public long SkippedReparsePoints; public long SkippedLongPaths; public bool Cancelled;
}

public sealed class DirectoryScanner
{
    public DirectoryScanner(ISearchIndexStore store, FileSearchSettings settings,
        Action<ScanProgress> progress = null,
        Func<string, bool> isForbiddenDirectory = null,
        Func<string, bool> isReparsePoint = null);
    public ScanResult Scan(IReadOnlyList<string> roots, ScanMode mode, CancellationToken token);
    public ScanResult ScanSubtree(string root, string directory, CancellationToken token);
}
```

- [ ] **Step 1: 写失败测试**

增加 `ScannerCases()`，用临时树：

```csharp
private static void ScannerCases()
{
    string tree = Path.Combine(_root, "tree");
    Directory.CreateDirectory(Path.Combine(tree, "docs"));
    Directory.CreateDirectory(Path.Combine(tree, "node_modules", "pkg"));
    Directory.CreateDirectory(Path.Combine(tree, "empty"));
    File.WriteAllText(Path.Combine(tree, "README"), "x");
    File.WriteAllText(Path.Combine(tree, "docs", "a.PDF"), "x");
    File.WriteAllText(Path.Combine(tree, "node_modules", "pkg", "skip.js"), "x");
    File.WriteAllText(Path.Combine(tree, "app.exe"), "x");

    string db = Path.Combine(_root, "scan.db");
    using (var store = new SearchIndexStore(db))
    {
        store.EnsureSchema();
        var s = FileSearchSettings.CreateDefault();
        s.Roots = new[] { tree };
        var scanner = new DirectoryScanner(store, s);
        store.BeginFullScan();
        var result = scanner.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None);
        store.CompleteFullScan(result.SkippedDirectories, result.SkippedReparsePoints,
            result.SkippedLongPaths, s.ComputeFingerprint(new[] { tree }));
        Check(result.Files == 2 && !result.Cancelled, "scan: default types index README + PDF, skip EXE");
        Check(store.Query("skip", 300, CancellationToken.None).TotalMatches == 0,
            "scan: node_modules is not indexed");
        Check(store.Query("a", 300, CancellationToken.None).TotalMatches == 1,
            "scan: nested file is indexed");

        store.BeginFullScan();
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(tree, "docs") };
        var guarded = new DirectoryScanner(store, s, null, p => forbidden.Contains(p));
        var r2 = guarded.Scan(new[] { tree }, ScanMode.Full, CancellationToken.None);
        store.CompleteFullScan(r2.SkippedDirectories, 0, 0, "x");
        Check(r2.SkippedDirectories >= 1 && store.Query("a", 300, CancellationToken.None).TotalMatches == 0,
            "scan: a denied directory is skipped and counted");

        store.BeginFullScan();
        var cts = new CancellationTokenSource();
        var cancelling = new DirectoryScanner(store, s, p => { if (p.Files >= 1) cts.Cancel(); });
        bool cancelled = false;
        try { cancelling.Scan(new[] { tree }, ScanMode.Full, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        store.AbortFullScan();
        Check(cancelled || cts.IsCancellationRequested, "scan: cancellation stops the scan");
        Check(store.Query("skip", 300, CancellationToken.None).TotalMatches == 0,
            "scan: cancellation leaves no published rows");
    }
}
```

（`p.Files` 是 `ScanProgress`；测试里用 `Action<ScanProgress>` 触发取消，避免依赖真实权限。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `DirectoryScanner` 未定义。

- [ ] **Step 3: 实现**

`Scan` 流程：

1. `Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;`（保存原值，finally 恢复）。
2. `store.BeginFullScan()` 或 `store.BeginReconcile()` 由调用方做；scanner 只写数据：`Full` 调 `UpsertScanBatch`；`Reconcile` 对同一批条目先 `UpsertScanBatch`，再 `MarkSeenBatch`（两条都是 Task 4 已有的接口方法）。
3. 用 `Stack<DirNode>`，逐目录；每个目录 `try/catch (UnauthorizedAccessException/IOException/DirectoryNotFoundException)` 跳过并计数。
4. `DirectoryInfo.EnumerateFileSystemInfos()` 逐项 `MoveNext`，每次属性读取单独 `try/catch`；`(attrs & FileAttributes.ReparsePoint) != 0` 计数并跳过；目录只入栈，不 `GetFiles` 全量数组。
5. 目录条目在 `IndexFolders` 时入批；文件条目用 `SearchTypeFilter`。
6. 每 800 条 `UpsertScanBatch`；每 500ms `progress?.Invoke(...)`；每个目录 `token.ThrowIfCancellationRequested()`。
7. 路径超过 32,000 字符：`SkippedLongPaths++`，不入库。
8. 返回 `ScanResult`；取消时抛 `OperationCanceledException`，调用方 `AbortFullScan`。

`ScanSubtree` 用于 watcher 的目录重命名补插：只遍历一个子树，写 `Upsert`（不是 staging），不要换表。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `ALL FILE-SEARCH CHECKS PASSED`。

- [ ] **Step 5: Commit**

```bash
git add Core/FileSearch tests/FileSearchCheck
git commit -m "feat(file-search): add cancellable directory scanner"
```

---

### Task 6: 全局 FileSearchIndexService 生命周期与全量/对账

**Files:**
- Create: `Core/FileSearch/FileSearchIndexService.cs`
- Modify: `Core/WidgetManager.cs`
- Modify: `tests/FileSearchCheck/Program.cs`

**Interfaces:**
- Consumes: `ISearchIndexStore`, `DirectoryScanner`, `FileSearchSettings`, `SearchRoots`, `SearchQueryResult`, `IndexStatus`。
- Produces：

```csharp
public enum FileSearchState { Unconfigured, NoIndex, Ready, Indexing, Reconciling, Paused, Error }

public sealed class FileSearchStatus
{
    public FileSearchState State; public string Message; public long FilesIndexed;
    public long DirectoriesScanned; public long SkippedDirectories; public bool IncrementalAvailable;
    public bool NeedsRebuild; public string DatabasePath;
}

public sealed class FileSearchIndexService : IDisposable
{
    public static FileSearchIndexService Instance { get; }
    public FileSearchStatus Status { get; }
    public event EventHandler<FileSearchStatus> StatusChanged;
    public event EventHandler<ScanProgress> ProgressChanged;

    public FileSearchIndexService(string databasePath); // 测试/单个实例用；Instance 走默认库
    public void ApplySettings(FileSearchSettings settings);
    public void SyncWithWidgets(IReadOnlyList<WidgetData> widgets);
    public void StartFullScan(FileSearchSettings settings, IReadOnlyList<string> roots);
    public void CancelScan();
    public void Pause();
    public void Resume();
    public void RequestRebuild();
    public SearchQueryResult Search(string text, int limit, CancellationToken token);
    public IndexStatus ReadStatus();
    public void WaitForIdle(TimeSpan timeout); // 测试钩子；生产不调用
    public void Shutdown(TimeSpan timeout);
}
```

- [ ] **Step 1: 写失败测试**

增加 `ServiceCases()`，用真实临时 store + 临时树：

```csharp
private static void ServiceCases()
{
    string db = Path.Combine(_root, "service.db");
    string tree = Path.Combine(_root, "service-tree");
    Directory.CreateDirectory(tree);
    File.WriteAllText(Path.Combine(tree, "one.txt"), "x");

    using (var service = new FileSearchIndexService(db))
    {
        var s = FileSearchSettings.CreateDefault();
        s.Roots = new[] { tree };
        s.Configured = true;
        service.ApplySettings(s);
        service.StartFullScan(s, new[] { tree });
        service.WaitForIdle(TimeSpan.FromSeconds(20)); // 测试钩子，见下
        Check(service.Status.FilesIndexed >= 1 && service.Search("one", 300, CancellationToken.None).TotalMatches == 1,
            "service: a configured full scan publishes searchable rows");

        service.Pause();
        Check(service.Status.State == FileSearchState.Paused, "service: pause changes state");
        service.Resume();
        Check(service.Status.State == FileSearchState.Ready, "service: resume returns to ready");

        int before = service.Search("one", 300, CancellationToken.None).TotalMatches;
        service.StartFullScan(s, new[] { tree });
        service.CancelScan();
        service.WaitForIdle(TimeSpan.FromSeconds(20));
        Check(service.Search("one", 300, CancellationToken.None).TotalMatches == before,
            "service: cancelling a full scan keeps the old rows");
    }
}
```

`WaitForIdle` 是 `public void WaitForIdle(TimeSpan timeout)`，生产代码可用 `ManualResetEventSlim` 实现，仅测试调用；文档注明测试钩子。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `FileSearchIndexService` 未定义。

- [ ] **Step 3: 实现服务**

`FileSearchIndexService`：
- `Instance` 为进程内单例；默认库路径 `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kobold", "search-index.db")`（目录不存在时创建）。public 构造 `FileSearchIndexService(string databasePath)` 供测试。
- 字段：`_store`, `_settings`, `_state`, `_scanCts`, `_scanTask`, `_idleEvent`, `_syncGate`。
- `ApplySettings`：`Normalize`；如果 configured 且 store 有索引，比较 `ReadStatus().SettingsFingerprint` 与 `ComputeFingerprint(SearchRoots.Resolve(settings.Roots))`；不一致只置 `NeedsRebuild=true`，不删库。
- `StartFullScan`：如果未 configured，忽略并返回 `Unconfigured`；置 `Indexing`，后台 Task：
  ```csharp
  store.BeginFullScan();
  var scanner = new DirectoryScanner(store, settings, p => ProgressChanged?.Invoke(this, p));
  try {
      var result = scanner.Scan(roots, ScanMode.Full, token);
      store.CompleteFullScan(result.SkippedDirectories, result.SkippedReparsePoints, result.SkippedLongPaths, fingerprint);
      store.Checkpoint();
      store.Vacuum(); // 一轮全量完成后一次；取消/对账不调用
      state = Ready;
  } catch (OperationCanceledException) {
      store.AbortFullScan();
      state = Paused; // 或 Ready，如果取消是因为暂停
  } catch (Exception ex) {
      store.AbortFullScan();
      state = Error; lastError = ex.Message;
  } finally { _idleEvent.Set(); }
  ```
- `CancelScan`：取消 token，不等待；`Pause`：`_paused=true`，取消 scan，停 watcher，状态 Paused；`Resume`：`_paused=false`，启动 watcher + 30 秒后 reconcile。
- `Start`（内部）：`EnsureSchema`；如果 `ReadStatus` 没有 `files` 或行数为 0 -> `NoIndex`；否则启动 watcher，状态 Ready；30 秒后首次 reconcile。
- `SyncWithWidgets`：`anySearch = widgets.Any(w => w is SearchData)`；无则停 watcher/timer 并状态 Unconfigured（库保留）；有且 configured/enabled -> Start；有但未 configured -> Unconfigured。
- `Shutdown`：取消 scan，停 watcher/timer，等待 `_scanTask` 最多 timeout，`Checkpoint`，`Dispose`。
- 数据库损坏：`EnsureSchema` 抛异常时，关闭 store，把 db 改名为 `search-index.db.failed-<yyyyMMdd-HHmmss>`，重建 schema，状态 Error，`Message = Localization`? Core 不依赖 Localization? It can use plain message; UI maps `Search_IndexCorrupt`. To keep Core pure, store `LastError` code string, e.g. `"index-corrupt"`. Plan: `IndexStatus.LastError` is a message; UI displays `Search_Error` plus message. Fine.

`WidgetManager`：
- `Initialize()` 在 `CreateWidgetInternal` 循环之前或之后：`FileSearchIndexService.Instance.ApplySettings(_config.FileSearch);` 然后 `FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);`
- `CreateWidgetInternal` 返回前：`FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);`
- `OnDeleted` 移除后：`FileSearchIndexService.Instance.SyncWithWidgets(_config.Widgets);`
- `Shutdown()`：**先**停止搜索服务（`FileSearchIndexService.Instance.Shutdown(TimeSpan.FromSeconds(5));`），再关闭 widget 窗口；顺序固定，避免窗口关闭后 watcher 仍写库。若没有搜索组件，service 只是空操作。
- `FileSearchIndexService.Instance` 构造不能加载配置，不依赖 `WidgetManager`；`ApplySettings` 由 `WidgetManager` 在 Initialize 里传入 `_config.FileSearch`（可 null）。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: 全 PASS。

- [ ] **Step 5: Commit**

```bash
git add Core/FileSearch Core/WidgetManager.cs tests/FileSearchCheck
git commit -m "feat(file-search): add global index service lifecycle"
```

---

### Task 7: FileSystemWatcher 增量队列与周期对账

**Files:**
- Create: `Core/FileSearch/FileSystemWatcherHost.cs`
- Modify: `Core/FileSearch/FileSearchIndexService.cs`
- Modify: `tests/FileSearchCheck/Program.cs`

**Interfaces:**
- Consumes: `ISearchIndexStore`, `DirectoryScanner`, `SearchEntryFactory`（在 `SearchEntry.cs` 里加 `TryCreate`）。
- Produces: `FileSystemWatcherHost.Start(IReadOnlyList<string> roots)`, `.Stop()`, `.Dispose()`；`bool IncrementalAvailable`；事件 `ReconcileRequested`。

- [ ] **Step 1: 写失败测试**

增加 `WatcherCases()`：

```csharp
private static void WatcherCases()
{
    string root = Path.Combine(_root, "watch-root");
    Directory.CreateDirectory(root);
    string db = Path.Combine(_root, "watch.db");
    using (var store = new SearchIndexStore(db))
    {
        store.EnsureSchema();
        store.BeginFullScan();
        store.CompleteFullScan(0, 0, 0, "watch");
        var s = FileSearchSettings.CreateDefault();
        s.Roots = new[] { root };
        using (var host = new FileSystemWatcherHost(store, s, null))
        {
            Check(host.Start(new[] { root }), "watch: a temp root starts");
            File.WriteAllText(Path.Combine(root, "created.txt"), "x");
            Check(WaitUntil(() => store.Query("created", 10, CancellationToken.None).TotalMatches == 1, 5000),
                "watch: created file appears in the index");

            string dir = Path.Combine(root, "dir");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "inside.txt"), "x");
            Directory.Move(dir, Path.Combine(root, "renamed"));
            Check(WaitUntil(() => store.Query("inside", 10, CancellationToken.None).TotalMatches == 1, 5000),
                "watch: a renamed directory's subtree is re-indexed");

            File.Delete(Path.Combine(root, "created.txt"));
            Check(WaitUntil(() => store.Query("created", 10, CancellationToken.None).TotalMatches == 0, 5000),
                "watch: a deleted file disappears");
        }
    }
}
```

`WaitUntil` 用 `Thread.Sleep(50)` 轮询。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `FileSystemWatcherHost` 未定义。

- [ ] **Step 3: 实现**

`FileSystemWatcherHost`：
- 每个 root 一个 `FileSystemWatcher`，`IncludeSubdirectories=true`，`InternalBufferSize=64*1024`，`NotifyFilter = FileName | DirectoryName | Size | LastWrite`；`Error` 事件置 `_overflow=true`、`ReconcileRequested?.Invoke()`，并重启 watcher（Stop/Start 同 root）。
- 事件回调只做 `TryAdd` 到 `BlockingCollection<WatcherEvent>(20000)`；满则 `_overflow=true` + `ReconcileRequested`，不阻塞回调。
- 单 worker 线程：`IsBackground=true`，`Priority=BelowNormal`；批量取最多 500 条或 200ms；对每条：
  - `Created/Changed`：`SearchEntryFactory.TryCreate(path, root, out entry)`；成功 `store.Upsert(entry)`，失败 `store.Delete(path)`。
  - `Deleted`：`store.DeletePathAndPrefix(path)`。
  - `Renamed`：`store.DeletePathAndPrefix(oldPath)`；如果新路径是目录，`new DirectoryScanner(store, _settings).ScanSubtree(root, newPath, token)`，否则 `TryCreate + Upsert`。
- `Start` 失败（任一根）不抛；记录 `IncrementalAvailable=false`；服务状态同时显示降级。
- `Stop` 取消 worker、`CompleteAdding`、等待最多 2s、Dispose watchers。
- `PauseProcessing()` / `ResumeProcessing()`：全量扫描期间只入队不写库；扫描完成换表后再 drain，避免增量写进旧表后被换掉。队列满时置 overflow 并请求 Reconcile，不阻塞 watcher 回调。
- `FileSearchIndexService`：
  - `Start()` 后 `new FileSystemWatcherHost(...).Start(roots)`；订阅 `ReconcileRequested` -> `ScheduleReconcile()`。
  - 周期 timer：`ReconcileMinutes > 0` 时 `new Timer(_ => TryStartReconcile(), null, TimeSpan.FromMinutes(n), TimeSpan.FromMinutes(n))`；`TryStartReconcile` 在 `_state` 为 Indexing/Reconciling/Paused 时跳过。
  - `Reconcile` 后台 Task：`store.BeginReconcile()`，scanner `ScanMode.Reconcile`，`store.CompleteReconcile(roots)`；失败 `AbortReconcile` + 状态 Error。
  - `StartFullScan` 与 `Reconcile` 开始时 `host.PauseProcessing()`，`finally` 里 `ResumeProcessing()`；换表/清理死链完成后再 drain 队列，避免“扫描期间新文件被 reconcile 当作死链删掉”或增量写进被替换的旧表。
  - 启动后 30 秒首次 `TryStartReconcile`，不阻塞 UI。
  - 无 watcher 根时状态 `IncrementalAvailable=false`，仍保留周期对账。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: 全 PASS；watcher 测试在 5 秒内完成。

- [ ] **Step 5: Commit**

```bash
git add Core/FileSearch tests/FileSearchCheck
git commit -m "feat(file-search): add watcher queue and periodic reconcile"
```

---

### Task 8: SearchData、搜索组件 UI、虚拟化结果列表与搜索键

**Files:**
- Create: `Controls/SearchWidget.xaml`
- Create: `Controls/SearchWidget.xaml.cs`
- Create: `Controls/SearchPanelView.xaml`
- Create: `Controls/SearchPanelView.xaml.cs`
- Create: `Controls/IconRenderers/SearchIconStyles.cs`
- Modify: `Core/WidgetKindRegistry.cs`
- Modify: `Controls/WidgetWindowFactory.cs`
- Modify: `Core/PanelSizeMigration.cs`
- Modify: `Controls/IconRenderers/IconRendererBase.cs`
- Modify: `Controls/IslandWindow.xaml.cs`
- Modify: `Core/Localization.cs`
- Modify: `tests/FileSearchCheck/Program.cs`
- Modify: `tests/WidgetKindsCheck/Program.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces:**
- Consumes: `FileSearchIndexService`, `SearchQueryResult`, `SearchEntry`, `FileSearchSettings`, `WidgetWindowBase`, `WidgetChrome`, `Localization`.
- Produces: `SearchData`；`SearchWidget`；`SearchPanelView` 的 `SearchBox`/`ResultsList`/`StatusText`/`EmptyState`/`OnboardingPanel`；`WidgetKindRegistry` 的 `search` 条目；`IconRendererFactory.CreateSearch(style)`；搜索 UI 三语 key。

- [ ] **Step 1: 写失败测试**

`FileSearchCheck` 增加：

```csharp
private static void SearchDataCases()
{
    var data = new Kobold.Core.SearchData();
    Check(data.Kind == "search" && data.Id != null, "search data: kind and id are set");
    var json = Newtonsoft.Json.JsonConvert.SerializeObject(data);
    var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Kobold.Core.SearchData>(json);
    Check(back != null && back.Kind == "search", "search data: round-trips");
}
```

`WidgetKindsCheck` 把 `All.Count == 3` 改为 4，增加 `search` 的 id/name/icon/data type 断言。`XamlLoadCheck` 增加：

```csharp
failures += TryLoad("SearchWidget [dark]", () => LoadSearch("dark"));
failures += TryCheck("factory: search data builds the search widget", FactorySearchDataBuildsSearchWidget);
failures += TryCheck("search: the result list is virtualized", SearchResultListIsVirtualized);
failures += TryCheck("search: the empty/no-index/onboarding states exist", SearchStatesExist);
```

`LoadSearch` 构造 `new SearchWidget(new SearchData())`，设置 `ThemeManager.SetTheme(theme)`，不 Show。`SearchResultListIsVirtualized` 通过 `FindName("ResultsList") as ListBox` 检查 `VirtualizingStackPanel.GetIsVirtualizing(list)` 和 `GetVirtualizationMode(list) == Recycling`。`SearchStatesExist` 检查 `FindName("EmptyState")`、`FindName("NoIndexState")`、`FindName("OnboardingPanel")` 非 null。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/FileSearchCheck -c Release --nologo`
Expected: `SearchData`/`SearchWidget` 未定义。

- [ ] **Step 3: 注册数据与窗口**

确认 Task 3 已创建 `Core/SearchData.cs`；本任务只加注册。

`WidgetKindRegistry` 增加：

```csharp
new WidgetKind
{
    Id = "search",
    NameKey = "UI_DefaultSearchName",
    IconKey = "search",
    CreateDefault = () => new SearchData(),
    DataType = typeof(SearchData),
}
```

`WidgetWindowFactory` 增加 `{ "search", data => new SearchWidget((SearchData)data) }`。
`PanelSizeMigration` 增加 `SearchData` 分支：`PanelWidth == null -> 422`，`PanelHeight == null -> 562`。
`IslandWindow.KindLabelKey` 加 `case "search": return "UI_WidgetKindSearch";`，`HintKey` 加 `case "search": return "Island_NewSearchHint";`。

`SearchIconStyles.cs`：`ClassicSearchIconRenderer` 和 `HandDrawnSearchIconRenderer` 各画一个 64×64 放大镜（圆 + 手柄，颜色用 `IconRendererBase` 的 `BaseColor`/`HighlightColor`）；`IconRendererFactory.CreateSearch(style)`，`CreateByKey` 加 `if (iconKey == "search") return CreateSearch(style);`。

- [ ] **Step 4: 实现 SearchWidget 壳与 SearchPanelView**

`SearchWidget.xaml`：

```xml
<local:WidgetWindowBase x:Class="Kobold.Controls.SearchWidget"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:Kobold.Controls"
        Title="SearchWidget" WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" Topmost="False" ResizeMode="NoResize"
        ShowInTaskbar="False" Width="422" Height="562"
        TextOptions.TextFormattingMode="Display">
    <local:WidgetChrome x:Name="SearchChrome"/>
</local:WidgetWindowBase>
```

`SearchWidget.xaml.cs`：

```csharp
public partial class SearchWidget : WidgetWindowBase
{
    private const double CONTENT_WIDTH = 420;
    private const double CONTENT_HEIGHT = 500;
    private const double MIN_WIDTH = 340;
    private const double MIN_HEIGHT = 320;
    private readonly SearchPanelView _view;

    public SearchWidget(SearchData data) : base(data)
    {
        InitializeComponent();
        AttachChrome(SearchChrome);
        _view = new SearchPanelView();
        SearchChrome.ContentHost.Content = _view;
        _view.Attach(data, this);
        Closing += (s, e) => _view.Detach();
    }

    public SearchPanelView PanelView => _view;
    protected override (double width, double height) GetContentSize() => (CONTENT_WIDTH, CONTENT_HEIGHT);
    protected override (double width, double height) GetMinPanelSize() => (MIN_WIDTH, MIN_HEIGHT);
    public override void ShowWidgetMenu(Action onClosed = null) =>
        WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;
}
```

`SearchPanelView.xaml` 结构（精确 name）：

```xml
<UserControl ...>
  <UserControl.Resources>
    <Style x:Key="SearchResultRowStyle" TargetType="ListBoxItem">
      <Setter Property="Padding" Value="0"/>
      <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    </Style>
  </UserControl.Resources>
  <Grid Background="Transparent">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/><RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <Grid Grid.Row="0" Margin="12,10,12,6">
      <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
      <TextBox x:Name="SearchBox" Height="34" VerticalContentAlignment="Center"
               TextChanged="SearchBox_TextChanged" KeyDown="SearchBox_KeyDown"/>
      <Button x:Name="ClearButton" Grid.Column="1" Content="✕" Width="30" Height="30"
              Margin="6,0,0,0" Click="ClearButton_Click"/>
      <TextBlock x:Name="PlaceholderText" Text="Search files and folders"
                 IsHitTestVisible="False" Foreground="{DynamicResource Kobold.Brush.TextSecondary}"
                 Margin="10,0,0,0" VerticalAlignment="Center"/>
    </Grid>

    <StackPanel Grid.Row="1" Margin="12,0,12,6">
      <TextBlock x:Name="StatusText" FontSize="11" Foreground="{DynamicResource Kobold.Brush.TextSecondary}"/>
      <ProgressBar x:Name="ScanProgress" Height="3" Margin="0,4,0,0" Visibility="Collapsed"/>
    </StackPanel>

    <Grid Grid.Row="2" Margin="8,0,8,4">
      <ListBox x:Name="ResultsList"
               VirtualizingStackPanel.IsVirtualizing="True"
               VirtualizingStackPanel.VirtualizationMode="Recycling"
               ScrollViewer.CanContentScroll="True"
               BorderThickness="0" Background="Transparent"
               ItemContainerStyle="{StaticResource SearchResultRowStyle}"
               MouseDoubleClick="ResultsList_MouseDoubleClick">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <Grid Height="44">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="28"/><ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>
              <TextBlock Text="{Binding Glyph}" FontSize="16" VerticalAlignment="Center"/>
              <StackPanel Grid.Column="1" VerticalAlignment="Center">
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                <TextBlock Text="{Binding Path}" FontSize="11" Foreground="{DynamicResource Kobold.Brush.TextSecondary}"
                           TextTrimming="CharacterEllipsis"/>
              </StackPanel>
              <TextBlock Grid.Column="2" Text="{Binding SizeText}" FontSize="11"
                         Foreground="{DynamicResource Kobold.Brush.TextSecondary}" Margin="8,0"/>
              <TextBlock Grid.Column="3" Text="{Binding ModifiedText}" FontSize="11"
                         Foreground="{DynamicResource Kobold.Brush.TextSecondary}"/>
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>

      <StackPanel x:Name="EmptyState" HorizontalAlignment="Center" VerticalAlignment="Center"
                  Visibility="Collapsed">
        <TextBlock Text="No matches" FontSize="13" Foreground="{DynamicResource Kobold.Brush.TextSecondary}"/>
      </StackPanel>

      <StackPanel x:Name="NoIndexState" HorizontalAlignment="Center" VerticalAlignment="Center"
                  Visibility="Collapsed">
        <TextBlock Text="Search index is not built yet" TextWrapping="Wrap" MaxWidth="300"
                   TextAlignment="Center"/>
        <Button x:Name="StartIndexButton" Content="Start indexing" Margin="0,10,0,0"
                Click="StartIndexButton_Click"/>
      </StackPanel>

      <ScrollViewer x:Name="OnboardingPanel" Visibility="Collapsed"
                    VerticalScrollBarVisibility="Auto" Background="Transparent">
        <StackPanel Margin="16,8">
          <TextBlock Text="Kobold indexes file names and locations only. File contents are not read. Protected folders are skipped."
                     TextWrapping="Wrap" Margin="0,0,0,10"/>
          <TextBlock Text="Drives" FontWeight="SemiBold" Margin="0,0,0,4"/>
          <ItemsControl x:Name="RootsList"/>
          <TextBlock x:Name="ScopeHint" TextWrapping="Wrap" Margin="0,10,0,0"
                     Foreground="{DynamicResource Kobold.Brush.TextSecondary}"/>
          <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
            <Button x:Name="OnboardingStartButton" Content="Start indexing"
                    Click="OnboardingStartButton_Click" Margin="0,0,8,0"/>
            <Button x:Name="OnboardingLaterButton" Content="Later"
                    Click="OnboardingLaterButton_Click"/>
          </StackPanel>
        </StackPanel>
      </ScrollViewer>
    </Grid>
  </Grid>
</UserControl>
```

`SearchPanelView.xaml.cs` 的行模型（放在同一文件/命名空间内）：

```csharp
public sealed class SearchResultRow
{
    public SearchEntry Entry { get; set; }
    public string Glyph { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public string SizeText { get; set; }
    public string ModifiedText { get; set; }
}
```

`SearchPanelView.xaml.cs` 关键行为：
- `Attach(SearchData data, WidgetWindowBase host)`：保存引用；`_debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) }`；`_debounce.Tick += ...`；`Loaded += OnLoaded`；`Unloaded += OnUnloaded`。
- `OnLoaded`：`_service = FileSearchIndexService.Instance; _service.StatusChanged += ...; _service.ProgressChanged += ...;` 初始化 onboarding（若 `WidgetManager.Instance.Config.FileSearch == null || !Configured`），否则刷新状态；`Loaded` 里不做扫描。
- `SearchBox_TextChanged`：占位文本可见性；重启 200ms timer。
- `RunQuery`：空查询清空列表 + 状态；否则取消旧 `_queryCts`，`Task.Run(() => _service.Search(text, 300, token), token)`，`Dispatcher.BeginInvoke` 更新 `ResultsList.ItemsSource = rows`、`StatusText`（`Search_ShowingFirst` 或 `Search_Ready`）、`EmptyState`。
- `UpdateStatus(FileSearchStatus s)`：`Indexing` 显示 `Search_Indexing`（FilesIndexed/DirectoriesScanned），进度条可见；`Ready` 显示 `Search_Ready` + 跳过数；`Paused`、`NoIndex`、`Error`、`Unconfigured` 分别切换状态面板；`NeedsRebuild` 显示 `Search_Rebuild` 按钮。
- `OpenSelected()`：双击/Enter；目录 `Process.Start("explorer.exe", "\"" + path + "\"")`，文件 `Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })`；异常写到 `StatusText`，不弹模态框。
- 右键菜单：`ResultsList.PreviewMouseRightButtonDown` 选中行，`ContextMenu` 三项；`CopyPath` 用 `Clipboard.SetText`。
- `Detach()`：退订事件、停止 timer、取消 query；不在 UI 线程做 IO。

- [ ] **Step 5: 加搜索 UI 三语 key**

在 `Localization.cs` 的 en/zh/ja 三个字典各加（必须同 key 集）：

```text
UI_WidgetKindSearch: Search / 文件搜索 / ファイル検索
UI_DefaultSearchName: My Search / 我的搜索 / マイ検索
Island_NewSearchHint: Find files and folders / 查找文件与文件夹 / ファイルとフォルダーを検索
Search_Placeholder: Search files and folders / 搜索文件与文件夹 / ファイルとフォルダーを検索
Search_Empty: No matches / 没有匹配结果 / 一致する結果はありません
Search_NoIndex: Search index is not built yet / 搜索索引尚未建立 / 検索インデックスはまだ作成されていません
Search_Indexing: Indexing {0:N0} files / {1:N0} / 正在索引 {0:N0} 个文件 / 共 {1:N0} 个 / {0:N0} ファイルを索引中 / {1:N0}
Search_Ready: Ready / 就绪 / 準備完了
Search_Paused: Indexing paused / 索引已暂停 / 索引は一時停止中
Search_Error: Search index error / 搜索索引错误 / 検索インデックスのエラー
Search_Rebuild: Rebuild index / 重建索引 / インデックスを再構築
Search_StartIndexing: Start indexing / 开始索引 / 索引を開始
Search_Later: Later / 稍后 / 後で
Search_Open: Open / 打开 / 開く
Search_OpenLocation: Open location / 打开所在位置 / 場所を開く
Search_CopyPath: Copy path / 复制路径 / パスをコピー
Search_ShowingFirst: Showing first {0:N0} of {1:N0} matches / 显示前 {0:N0} 条，共 {1:N0} 条匹配 / 最初の {0:N0} 件を表示 / 全 {1:N0} 件
Search_SkippedFolders: {0:N0} folders skipped / 已跳过 {0:N0} 个文件夹 / {0:N0} 個のフォルダーをスキップ
Search_AllFiles: All files / 全部文件 / すべてのファイル
Search_Types: File types / 文件类型 / ファイルの種類
Search_Excludes: Excluded folders / 排除文件夹 / 除外フォルダー
Search_Searching: Searching... / 正在搜索... / 検索中...
Search_Clear: Clear / 清除 / クリア
Search_IndexSize: Index size: {0} / 索引大小：{0} / インデックスサイズ: {0}
Search_LastUpdated: Last updated: {0} / 最后更新：{0} / 最終更新: {0}
Search_NotConfigured: Not configured / 尚未配置 / 未設定
Search_ScanCancelled: Indexing cancelled / 索引已取消 / 索引をキャンセルしました
Search_ConfigureHint: Kobold indexes file names and locations only. File contents are not read. Protected folders are skipped. / Kobold 只索引文件名和位置，不读取文件内容；受保护文件夹会被跳过。 / Kobold はファイル名と場所のみを索引し、内容は読みません。保護されたフォルダーはスキップされます。
Search_Drives: Drives / 磁盘 / ドライブ
```

- [ ] **Step 6: 运行测试**

Run:
```bash
dotnet run --project tests/FileSearchCheck -c Release --nologo
dotnet run --project tests/WidgetKindsCheck -c Release --nologo
dotnet run --project tests/LangCheck -c Release --nologo
dotnet run --project tests/XamlLoadCheck -c Release --nologo
```
Expected: 全 PASS；`LangCheck` 三语 key 齐；`XamlLoadCheck` 的 island add menu 因 registry 多一项仍通过（它按 `WidgetKindRegistry.All.Count` 断言）。

- [ ] **Step 7: Commit**

```bash
git add Core/SearchData.cs Core/WidgetKindRegistry.cs Core/PanelSizeMigration.cs Core/Localization.cs
git add Controls/SearchWidget.xaml Controls/SearchWidget.xaml.cs Controls/SearchPanelView.xaml Controls/SearchPanelView.xaml.cs
git add Controls/WidgetWindowFactory.cs Controls/IslandWindow.xaml.cs Controls/IconRenderers
git add tests/FileSearchCheck tests/WidgetKindsCheck tests/XamlLoadCheck
git commit -m "feat(file-search): add search widget and virtualized results"
```

---

### Task 9: 首次引导、设置区、组件菜单、搜索设置三语 key

**Files:**
- Modify: `Controls/SearchPanelView.xaml`
- Modify: `Controls/SearchPanelView.xaml.cs`
- Modify: `Windows/SettingsWindow.xaml`
- Modify: `Windows/SettingsWindow.xaml.cs`
- Modify: `Helpers/WidgetMenuBuilder.cs`
- Modify: `Core/Localization.cs`
- Modify: `tests/XamlLoadCheck/Program.cs`
- Modify: `tests/FileSearchCheck/Program.cs`

**Interfaces:**
- Consumes: Task 8 的 `SearchPanelView`、`SearchWidget`、`SearchData`；Task 6 的 `FileSearchIndexService`；Task 3 的 `AppConfig.FileSearch`。
- Produces: 首次引导开始/稍后；设置区根/类型/全部文件/无扩展名/文件夹/追加排除/Reconcile/状态/大小/最后更新/重建/暂停；组件菜单重建/暂停/继续；设置 key 三语。

- [ ] **Step 1: 写失败测试**

`XamlLoadCheck` 增加：

```csharp
failures += TryCheck("settings: the file search section exists", SettingsFileSearchSectionExists);
failures += TryCheck("search: the onboarding has drive choices and start/later", SearchOnboardingHasActions);
failures += TryCheck("search: the widget menu offers rebuild and pause", SearchMenuOffersRebuildAndPause);
```

`SettingsFileSearchSectionExists`：构造 `SettingsWindow`，`FindName("FileSearchHeader")`、`FileSearchAllFilesCheck`、`FileSearchTypesBox`、`FileSearchExcludesBox`、`FileSearchReconcileBox`、`FileSearchRebuildButton`、`FileSearchPauseButton` 非 null。`SearchOnboardingHasActions`：构造 `SearchWidget`，`FindName("RootsList")` 的 `Items.Count == SearchRoots.FixedNtfsRoots().Count`，`OnboardingStartButton`/`OnboardingLaterButton` 非 null。`SearchMenuOffersRebuildAndPause`：`WidgetMenuBuilder.Build(new SearchWidget(new SearchData()), null)`，菜单里找 `Search_Rebuild`、`Search_Pause` 的 header。

`FileSearchCheck` 增加：

```csharp
private static void SettingsUiCases()
{
    var s = FileSearchSettings.CreateDefault();
    s.Configured = true;
    s.Roots = new[] { @"C:\" };
    var f1 = s.ComputeFingerprint(new[] { @"C:\" });
    s.IndexAllFiles = true;
    var f2 = s.ComputeFingerprint(new[] { @"C:\" });
    Check(f1 != f2, "settings: all-files changes the fingerprint and marks rebuild");
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/XamlLoadCheck -c Release --nologo`
Expected: 新检查项失败（控件/菜单不存在）。

- [ ] **Step 3: 实现首次引导**

`SearchPanelView` 在 `OnLoaded` 调 `RefreshOnboarding()`：
- `bool configured = WidgetManager.Instance.Config.FileSearch?.Configured == true;`
- 未配置：`OnboardingPanel.Visibility = Visible`，`NoIndexState/EmptyState Collapsed`；`RootsList.ItemsSource = SearchRoots.FixedNtfsRoots().Select(r => new CheckBox { Content = r, IsChecked = true, Tag = r }).ToList()`（`System.Linq`；`Items` 里就是 CheckBox）。`RefreshOnboarding()` 设为 `internal`，供 XamlLoadCheck 直接调用。
- 首次引导至少勾选一个盘才允许 `OnboardingStartButton`；全不选时按钮禁用（避免 `Roots` 空数组被解释成“所有固定 NTFS 盘”）。
- `OnboardingStartButton_Click`：
  ```csharp
  var settings = FileSearchSettings.CreateDefault();
  settings.Configured = true;
  settings.Enabled = true;
  var roots = new List<string>();
  foreach (CheckBox cb in RootsList.Items) if (cb.IsChecked == true) roots.Add((string)cb.Tag);
  settings.Roots = roots.ToArray();
  settings.Normalize();
  var config = WidgetManager.Instance.Config;
  config.FileSearch = settings;
  config.Save();
  FileSearchIndexService.Instance.ApplySettings(settings);
  FileSearchIndexService.Instance.StartFullScan(settings, SearchRoots.Resolve(settings.Roots));
  OnboardingPanel.Visibility = Collapsed;
  UpdateStatus(FileSearchIndexService.Instance.Status);
  ```
- `OnboardingLaterButton_Click`：只 `OnboardingPanel.Visibility = Collapsed; NoIndexState.Visibility = Visible;`，不写配置。
- 已配置但 `NeedsRebuild`：`NoIndexState` 显示 `Search_Rebuild` 按钮，点击 `FileSearchIndexService.Instance.RequestRebuild()`；不自动重建。

- [ ] **Step 4: 实现设置区**

`SettingsWindow.xaml` 在 Storage 与 Island 之间加：

```xml
<TextBlock x:Name="FileSearchHeader" Text="🔎 File Search" .../>
<Border ...>
  <StackPanel>
    <TextBlock x:Name="FileSearchStatusText" .../>
    <TextBlock x:Name="FileSearchSizeText" .../>
    <TextBlock x:Name="FileSearchLastUpdatedText" .../>
    <CheckBox x:Name="FileSearchAllFilesCheck" Content="All files" .../>
    <CheckBox x:Name="FileSearchExtensionlessCheck" Content="Extensionless files" .../>
    <CheckBox x:Name="FileSearchFoldersCheck" Content="Index folders" .../>
    <TextBlock Text="File types" .../>
    <TextBox x:Name="FileSearchTypesBox" .../>
    <TextBlock Text="Excluded folders" .../>
    <TextBox x:Name="FileSearchExcludesBox" AcceptsReturn="True" Height="54" .../>
    <TextBlock Text="Reconcile every (minutes)" .../>
    <TextBox x:Name="FileSearchReconcileBox" Width="60" .../>
    <TextBlock Text="Indexed drives" .../>
    <ItemsControl x:Name="FileSearchRootsList" .../>
    <StackPanel Orientation="Horizontal">
      <Button x:Name="FileSearchRebuildButton" Content="Rebuild now" .../>
      <Button x:Name="FileSearchPauseButton" Content="Pause" .../>
    </StackPanel>
  </StackPanel>
</Border>
```

`SettingsWindow.xaml.cs`：
- 字段 `_originalFileSearch`（深拷贝：`JsonConvert.DeserializeObject<FileSearchSettings>(JsonConvert.SerializeObject(_config.FileSearch))`；null -> null）。
- `LoadSettings`：若 `_config.FileSearch != null` 填充；否则默认值（复选框 true/true/true，types 默认逗号串，roots 为空 = 全部固定 NTFS）。
- `UpdateLocalizedText`：设置所有 label/按钮文案；`FileSearchStatusText` 通过服务状态。
- Save：`_config.FileSearch = BuildSettingsFromControls(); _config.FileSearch.Normalize(); _config.Save(); FileSearchIndexService.Instance.ApplySettings(_config.FileSearch);`；若 `Configured` 且 fingerprint 变化，服务置 `NeedsRebuild`；不要自动重建。
- `RevertChanges`：`_config.FileSearch = _originalFileSearch; FileSearchIndexService.Instance.ApplySettings(_config.FileSearch);`
- Rebuild 按钮：`MessageBox.Show(this, Localization.Get("Search_RebuildConfirm"), "Kobold", YesNo)`，确认后 `Service.RequestRebuild()`；Pause 按钮按状态切 `Pause`/`Resume`。
- 订阅服务 `StatusChanged` 更新状态文本；`Closed` 退订。

- [ ] **Step 5: 组件菜单**

`WidgetMenuBuilder.Build` 在 `if (host.Data is NoteData ...)` 后加：

```csharp
if (host.Data is SearchData)
{
    menu.AddMenuItem(new MenuItem { Header = Localization.Get("Search_Rebuild"),
        Click = (s, e) => FileSearchIndexService.Instance.RequestRebuild() });
    menu.AddMenuItem(new MenuItem { Header = Localization.Get(
        FileSearchIndexService.Instance.Status.State == FileSearchState.Paused ? "Search_Resume" : "Search_Pause"),
        Click = (s, e) => { if (FileSearchIndexService.Instance.Status.State == FileSearchState.Paused)
            FileSearchIndexService.Instance.Resume(); else FileSearchIndexService.Instance.Pause(); } });
}
```

（用 `MenuBuilder` 的 `AddMenuItem` 现有 API；若只有 `AddItem(key, action)`，用它并传 key，不要硬编码文案。）

- [ ] **Step 6: 补设置三语 key**

en/zh/ja 三语都加：

```text
UI_DefaultSearchName 已在 Task 8
Settings_FileSearch: File Search / 文件搜索 / ファイル検索
Search_RebuildConfirm: Rebuild the search index? Existing results stay available until the new scan finishes. / 重建搜索索引？新扫描完成前旧结果仍可搜索。 / 検索インデックスを再構築しますか？新しいスキャンが完了するまで既存の結果は検索できます。
Search_Pause: Pause indexing / 暂停索引 / 索引を一時停止
Search_Resume: Resume indexing / 继续索引 / 索引を再開
Search_SettingsStatus: Status: {0} / 状态：{0} / 状態: {0}
Search_SettingsFiles: {0:N0} files indexed / 已索引 {0:N0} 个文件 / {0:N0} ファイルを索引済み
Search_SettingsRoots: Indexed drives / 索引磁盘 / 索引対象ドライブ
Search_SettingsTypes: File types (comma separated) / 文件类型（逗号分隔） / ファイルの種類（カンマ区切り）
Search_SettingsExcludes: Extra excluded folders (one per line) / 追加排除文件夹（每行一个） / 追加の除外フォルダー（1 行に 1 つ）
Search_SettingsReconcile: Reconcile every (minutes, 0 = off) / 对账间隔（分钟，0 = 关闭） / 照合間隔（分、0 = オフ）
Search_SettingsAllFiles: All files / 全部文件 / すべてのファイル
Search_SettingsExtensionless: Extensionless files / 无扩展名文件 / 拡張子なしファイル
Search_SettingsFolders: Index folders / 索引文件夹 / フォルダーを索引
Search_SettingsRebuild: Rebuild now / 立即重建 / 今すぐ再構築
Search_SettingsPause: Pause / 暂停 / 一時停止
Search_SettingsResume: Resume / 继续 / 再開
Search_NeedsRebuild: Settings changed; rebuild the index to apply them. / 设置已更改；重建索引后生效。 / 設定が変更されました。インデックスを再構築すると反映されます。
```

- [ ] **Step 7: 运行测试**

Run:
```bash
dotnet run --project tests/FileSearchCheck -c Release --nologo
dotnet run --project tests/LangCheck -c Release --nologo
dotnet run --project tests/XamlLoadCheck -c Release --nologo
dotnet run --project tests/WidgetKindsCheck -c Release --nologo
```
Expected: 全 PASS。

- [ ] **Step 8: Commit**

```bash
git add Controls/SearchPanelView.xaml Controls/SearchPanelView.xaml.cs
git add Windows/SettingsWindow.xaml Windows/SettingsWindow.xaml.cs
git add Helpers/WidgetMenuBuilder.cs Core/Localization.cs
git add tests/FileSearchCheck tests/XamlLoadCheck
git commit -m "feat(file-search): add onboarding and settings controls"
```

---

### Task 10: 全量测试矩阵、安装器原生 DLL、文档与最终验证

**Files:**
- Modify: `tools/Run-Checks.ps1`
- Modify: `tools/installer-common.ps1`
- Modify: `tools/package.ps1`
- Modify: `tools/tests/installer.Tests.ps1`
- Modify: `tools/uninstall.ps1`（若删除数据清单需要）
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `Kobold.csproj`（Release 0 warning 检查；只有需要时加 `<NoWarn>` 禁止，不能掩盖真实 warning）
- Modify: `tests/FileSearchCheck/Program.cs`（补边界用例）
- Modify: `tests/XamlLoadCheck/Program.cs`（补搜索菜单/设置/三语 pin）

**Interfaces:**
- Consumes: 全部前序任务。
- Produces: `FileSearchCheck` 进入矩阵；安装器携带 SQLite 原生 DLL；卸载清理索引库；README/CHANGELOG 说明；33/33、0 warning。

- [ ] **Step 1: 补 FileSearchCheck 边界用例**

在 `RunAll()` 增加：
- 300 条上限：插入 301 条匹配，`Query(..., 300)` 返回 300、`LimitReached` true、`TotalMatches == 301`。
- 名称优先于路径：`C:\p\zzz\readme.txt` 与 `C:\p\readme\other.txt`，查询 `readme`，名称含 `readme` 的行排在路径匹配前。
- 中文/大小写/无扩展名/文件夹已覆盖；补 `IndexFolders=false` 时不索引文件夹但仍遍历子文件。
- 取消：大量条目中途取消，旧索引行数不变。
- 配置：`FileSearch=null` 安全；`Types=null` 归一化默认；`ExcludeDirectories=null` 为空。

- [ ] **Step 2: 更新 Run-Checks 矩阵**

在 `$checks` 加：

```powershell
@{ Name = 'FileSearchCheck';     Ci = $true  }
```

放在 `WidgetKindsCheck` 之后。运行 `powershell ... -List`，确认 `all` 矩阵为 33 项（30 个旧 check + FileSearchCheck + config-pin + installer = 33）。

- [ ] **Step 3: 安装器支持原生 DLL**

`installer-common.ps1`：

```powershell
$KoboldRuntimeFiles = @(
    'Kobold.exe', 'Kobold.exe.config',
    'Hardcodet.NotifyIcon.Wpf.dll', 'Newtonsoft.Json.dll',
    'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll',
    'System.Buffers.dll', 'System.Memory.dll',
    'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll'
)
```

Task 4 的实际 Release 输出是 flat 布局（`e_sqlite3.dll` 在输出根目录，没有 `runtimes\` 子目录），所以安装器按上面的 flat 列表复制；`Install-KoboldFiles` 仍保留相对目录能力（`$dest = Join-Path $InstallDir $file; New-Item -ItemType Directory -Force (Split-Path $dest)`），`package.ps1` 用同一列表。`tools/tests/installer.Tests.ps1` 的 `New-FakeSource` 创建这 12 个文件；安装断言用 `Test-Path`；文件计数改为 `(Get-ChildItem $installDir -Recurse -File).Count` 并等于 `$runtimeFiles.Count + $uninstallerFiles.Count`（15）。

卸载：`Get-KoboldDataInventory` 增加 `SearchIndexFiles`（`%LocalAppData%\Kobold\search-index.db*`）；`Remove-KoboldData -Choice All` 删除这些文件；`ConfigOnly` 不动索引库。`uninstall.ps1` 的清单文案若需要加一行“搜索索引”并三语。

- [ ] **Step 4: README / CHANGELOG**

README 增加 “File search” 一节：
- 索引位置、只存元数据、默认类型、全部文件开关、无管理员、跳过目录计数、首次引导、重建/暂停、周期对账 10 分钟、无内容搜索。
- Build & test 说明 SQLite 原生 DLL 随包、`Run-Checks` 33 项。
- 手动 smoke checklist：用户手动全盘首扫、搜索中文/长路径/无扩展名、创建/重命名/删除、重启保留、重建、损坏恢复、无 UAC。

CHANGELOG 顶部 `Unreleased` 加：
```text
### Added
- File search widget: full-disk metadata index (fixed NTFS drives), incremental watcher, periodic reconcile, virtualized results, first-run onboarding, settings, en/zh/ja copy. No admin, no content indexing.
- `FileSearchCheck` and installer coverage for the SQLite native runtime.
```

- [ ] **Step 5: 全量验证**

Run:
```bash
dotnet build Kobold.csproj -c Release --nologo
dotnet run --project tests/FileSearchCheck -c Release --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all
```
Expected:
- build 输出 `0 Warning(s)`、`0 Error(s)`；
- FileSearchCheck `ALL FILE-SEARCH CHECKS PASSED`；
- Run-Checks 汇总 `All 33 check(s) passed.`（若本机 `XamlLoadCheck` 需要交互桌面，必须在用户桌面会话运行；失败则记录环境原因，不得改写为通过）。
- `tools\tests\installer.Tests.ps1` 通过。

- [ ] **Step 6: 手动冒烟说明（不代替用户执行）**

在 `docs/superpowers/plans/2026-10-23-file-search-widget.md` 末尾记录“用户手动全盘冒烟”清单和当前未做项；不要声称真实全盘已通过。

- [ ] **Step 7: Commit**

```bash
git add tools README.md CHANGELOG.md tests/FileSearchCheck tests/XamlLoadCheck
git commit -m "test(file-search): wire checks and installer runtime"
```

---

## Self-Review

- **Spec coverage:** §3.1 roots/§3.2 types/§3.3 excludes -> Task 2/5/9；§3.4 results -> Task 8；§4.1 boundaries -> Task 4/5/6/7/8；§4.2 schema/query/rebuild -> Task 4/6；§4.3 full scan -> Task 5；§4.4 incremental/reconcile -> Task 7；§4.5 lifecycle -> Task 6/8/9；§4.6 config -> Task 3/9；§5 UI -> Task 8/9；§5.3 localization -> Task 8/9；§6 no admin -> Global Constraints + Task 5/7；§7 performance -> Task 1/4；§8 tests -> Task 2-10；§9 risks -> Task 1/4/7；§10 open items -> Task 1 decision + Rulings；§11 acceptance -> Task 10 + manual smoke.
- **Placeholder scan:** 没有 TBD/TODO；Task 1 的 `<实测>` 是 spike 必须填的测量值，不是实现占位；Task 4 的 provider 分支由 Task 1 决策记录收敛。
- **Type consistency:** `SearchEntry`、`ISearchIndexStore`、`SearchQueryResult`、`FileSearchStatus`、`FileSearchState`、`ScanProgress`/`ScanResult` 在后续任务中保持同名；`SearchPanelView` 控件名在 Task 8/9/XamlLoadCheck 一致；`FileSearchSettings` 字段名与 spec §4.6 一致。

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-10-23-file-search-widget.md`.

Recommended execution: subagent-driven-development. Task 1 is a spike; do not dispatch Task 2 until the decision record is reviewed and the provider/query backend is fixed.

## 用户手动全盘冒烟清单（未执行）

自动检查只使用仓库内的临时目录；以下项目需要用户在真实机器上手动完成，**当前尚未执行，不得声称全盘冒烟已通过**：

1. 在系统盘有大量文件的机器上首次打开搜索组件，走完引导并点「开始索引」；等待状态变为就绪，记录文件 / 文件夹数量与耗时。
2. 搜索中文文件名、超过 260 字符的长路径、无扩展名的文件，确认结果正确且能打开 / 打开所在位置 / 复制路径。
3. 组件打开时创建、重命名、删除文件与文件夹，确认监听在数秒内更新结果，无需重建。
4. 重启 Kobold，确认索引保留、搜索立即可用且没有自动全盘重扫。
5. 点「重建索引」，确认扫描期间旧结果仍可搜索，完成后状态回到就绪。
6. 手动删除或写坏 `%LocalAppData%\Kobold\search-index.db` 后重启，确认应用隔离 / 重建索引并可再次搜索。
7. 以普通用户运行安装与卸载，全程不出现 UAC；卸载选「全部删除」后确认 `search-index.db*` 消失，而 `%LocalAppData%\Kobold` 目录本身没有被整个删除。
8. 观察空闲时 CPU / 内存，确认监听与 10 分钟对账没有明显占用。
