# Kobold 配置数据安全（Config Safety）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除配置读写中"用户数据可能被静默毁掉"的路径：原子写、读失败留证据、不静默重置、保存失败可见、关机同步落盘。

**Architecture:** 新增 `Core/AtomicFile.cs`（临时文件 + `Flush(true)` + 原子替换 + 有界重试，失败清理且不碰旧文件）。`AppConfig` 改为实例持有配置路径，`Load` 主/备都失败时先把失败文件副本存为 `*.failed-<时间戳>` 再退回默认配置；从备份恢复后首次保存不覆盖恢复源。保存增加 10s 强制上限与 `SaveFailed` 事件（托盘气球提示一次）。`App.SessionEnding` 触发一次同步收尾保存并禁止后续新写入。

**Tech Stack:** net48 / C# 7.3 / Newtonsoft.Json 13.0.3 / WPF；自检为独立 console 项目（`tests/AtomicFileCheck`、`tests/ConfigSafetyCheck`）。

**Spec:** `docs/paper-todo-learnings.md` 第 3 节（PaperTodo 侧参考：`src/DurableAtomicFileWriter.cs`、`src/StateStore.cs`、`src/AppController.PersistencePolicy.cs`）。

## Global Constraints

- 目标框架 net48，`LangVersion 7.3`；不使用 C# 8+ 语法（无 using 声明、无 switch 表达式、无 `??=`）。
- 不新增 XAML、不新增十六进制颜色字面量（`UiTokensCheck` 守护）。
- 新增用户可见文案必须走 `Localization`（en/zh/ja 三语，`tests/LangCheck` 守护 key 集合一致）。
- `Load()` 在"全新安装（两文件都不存在）"时仍自动创建并写出默认配置（保持现有行为）。
- 每次任务结束后：相关自检失败→实现→通过；收尾必须 `dotnet build Kobold.csproj -c Debug` 0 警告 0 错误。
- **提交前必须获得用户明确许可**（延续仓库既有约定）。

## File Structure

| 文件 | 责任 | 动作 |
| --- | --- | --- |
| `Core/AtomicFile.cs` | 原子写入原语 | 新建 |
| `tests/AtomicFileCheck/AtomicFileCheck.csproj` + `Program.cs` | 原子写的故障行为自检 | 新建 |
| `Core/AppConfig.cs` | 路径归属、失败留证据、保护位、强制上限、`SaveFailed`、收尾保存 | 修改 |
| `tests/ConfigSafetyCheck/ConfigSafetyCheck.csproj` + `Program.cs` | 配置安全行为自检 | 新建 |
| `Core/Localization.cs` | 新增 2 个失败提示词条 ×3 语言 | 修改 |
| `Services/TrayIconService.cs` | 订阅 `SaveFailed`，气球提示一次 | 修改 |
| `App.xaml.cs` | `SessionEnding` 收尾保存 | 修改 |
| `README.md` | 数据目录小节补充 `*.failed-*` 说明 | 修改 |

---

### Task 1: 原子写入原语 `Core/AtomicFile.cs`

**Files:**
- Create: `Core/AtomicFile.cs`
- Test: `tests/AtomicFileCheck/AtomicFileCheck.csproj`、`tests/AtomicFileCheck/Program.cs`

**Interfaces:**
- Produces: `public static class AtomicFile { public static void WriteAllText(string path, string contents); }`
  - 成功：`path` 内容为新值，无 `.tmp` 残留。
  - 失败：抛异常，旧 `path` 原样保留，无 `.tmp` 残留。
  - 目录不存在时自动创建；目标被占用时重试 5 次 × 100ms（`IOException`/`UnauthorizedAccessException`）。

- [ ] **Step 1: 写失败的自检项目**

`tests/AtomicFileCheck/AtomicFileCheck.csproj` 照 `tests/StorageOpsCheck/StorageOpsCheck.csproj` 模板（`OutputType=Exe`、`net48`、`LangVersion 7.3`、`AssemblyName=Kobold.AtomicFileCheck`、`ProjectReference ..\..\Kobold.csproj`）。

`Program.cs` 骨架照 `tests/StorageOpsCheck/Program.cs`（`Errors` 列表 + `Check(bool,string)` + 结尾 `ALL ... PASSED`/`FAILURES` + 退出码）。用例：

```csharp
// 1. 成功写入：内容替换、无 .tmp 残留
string dir = Path.Combine(_root, "case1");
Directory.CreateDirectory(dir);
string target = Path.Combine(dir, "config.json");
File.WriteAllText(target, "old");
AtomicFile.WriteAllText(target, "new");
Check(File.ReadAllText(target) == "new", "write replaces contents");
Check(!File.Exists(target + ".tmp"), "no temp file left after success");

// 2. 首次创建（目标不存在 + 目录不存在）
string deep = Path.Combine(_root, "case2", "sub", "config.json");
AtomicFile.WriteAllText(deep, "fresh");
Check(File.Exists(deep) && File.ReadAllText(deep) == "fresh", "creates directory and file");

// 3. 目标被独占锁定时：抛异常、旧内容完好、无 .tmp 残留
string lockedPath = Path.Combine(_root, "case3", "config.json");
Directory.CreateDirectory(Path.GetDirectoryName(lockedPath));
File.WriteAllText(lockedPath, "precious");
using (var hold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
{
    bool threw = false;
    try { AtomicFile.WriteAllText(lockedPath, "lost"); } catch (IOException) { threw = true; }
    Check(threw, "write fails when target is locked");
}
Check(File.ReadAllText(lockedPath) == "precious", "old contents survive a failed write");
Check(!File.Exists(lockedPath + ".tmp"), "no temp file left after failure");

// 4. 临时文件位置被目录占用：失败且不碰目标
string blocked = Path.Combine(_root, "case4", "config.json");
Directory.CreateDirectory(Path.GetDirectoryName(blocked));
File.WriteAllText(blocked, "keep");
Directory.CreateDirectory(blocked + ".tmp");
bool threw2 = false;
try { AtomicFile.WriteAllText(blocked, "nope"); } catch (Exception) { threw2 = true; }
Check(threw2 && File.ReadAllText(blocked) == "keep", "temp collision fails without touching target");
Check(Directory.Exists(blocked + ".tmp"), "pre-existing temp path is not deleted");
```

- [ ] **Step 2: 运行并确认失败（编译失败也算失败）**

Run: `dotnet run --project tests/AtomicFileCheck`
Expected: 编译错误 `AtomicFile 不存在` → 退出码非 0。

- [ ] **Step 3: 实现 `Core/AtomicFile.cs`**

```csharp
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Kobold.Core
{
    /// <summary>
    /// All-or-nothing text writes: the target is only ever replaced after the
    /// new contents are fully written and flushed to disk, so a crash, a full
    /// disk or a lock mid-write leaves the previous file exactly as it was.
    /// </summary>
    public static class AtomicFile
    {
        private const int ReplaceAttempts = 5;
        private const int ReplaceRetryDelayMs = 100;

        public static void WriteAllText(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            Utils.EnsureDirectoryExists(directory);
            string temp = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                    FileShare.None, 16 * 1024, FileOptions.SequentialScan))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true); // flush to disk before the old file is replaced
                }

                ReplaceWithRetry(temp, path);
            }
            catch
            {
                TryDelete(temp); // never leave a half-written or stale temp behind
                throw;
            }
        }

        private static void ReplaceWithRetry(string temp, string target)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(temp, target, null);
                    else File.Move(temp, target);
                    return;
                }
                catch (IOException) when (attempt < ReplaceAttempts) { }
                catch (UnauthorizedAccessException) when (attempt < ReplaceAttempts) { }
                Thread.Sleep(ReplaceRetryDelayMs);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
```

注意 `catch (...) when (...)` 的放行写法：预算耗尽时必须抛出（上面循环的最后一次尝试不再满足 `when`，异常自然抛出）。

- [ ] **Step 4: 运行并确认通过**

Run: `dotnet run --project tests/AtomicFileCheck`
Expected: `ALL ATOMIC-FILE CHECKS PASSED`，退出码 0。

- [ ] **Step 5: 记录（不提交）**

用户许可前不提交；在计划执行记录里记下本任务完成。

---

### Task 2: `AppConfig` 路径归属 + 失败留证据 + 保护恢复源

**Files:**
- Modify: `Core/AppConfig.cs:47-123`（`Load`/`Save`/`SaveDebounced` 改由实例路径驱动）
- Test: `tests/ConfigSafetyCheck/...`（本任务先写 Load/Save 部分）

**Interfaces:**
- Produces:
  - `public static AppConfig Load()`（行为不变：读 `Utils.GetConfigPath()`）。
  - `public static AppConfig Load(string configPath, string backupPath)`；返回实例记住这两个路径。
  - `public void Save()` 使用实例路径（未 Load 过的实例回退 `Utils.GetConfigPath()`）。
  - 失败副本命名：`<原文件名>.failed-<yyyyMMdd-HHmmss>`（同一秒重名时加 `-2`、`-3`…）。

- [ ] **Step 1: 写失败的自检**

`tests/ConfigSafetyCheck` 模板同 Task 1。用例（全部使用 `%TEMP%\kobold-configsafety-<guid>`，`finally` 清理）：

```csharp
// 1. 主损坏 + 备份有效：用备份、损坏主留证据
string dir = ...;
string main = Path.Combine(dir, "config.json");
string backup = main + ".backup";
File.WriteAllText(main, "{ not json");
File.WriteAllText(backup, "{\"Language\":\"zh\"}");
var cfg = AppConfig.Load(main, backup);
Check(cfg.Language == "zh", "falls back to backup when main is unreadable");
Check(File.Exists(main) && File.ReadAllText(main) == "{ not json", "unreadable main is left untouched");
Check(Directory.GetFiles(dir, "config.json.failed-*").Length == 1, "unreadable main gets a timestamped evidence copy");

// 2. 从备份恢复后的第一次保存不得覆盖恢复源
cfg.Save();
Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(backup)).Language == "zh",
    "backup (the recovery source) is not overwritten by the first save");
Check(File.ReadAllText(main).Contains("\"zh\""), "main holds the recovered config after save");

// 3. 第二次保存后备份才滚动到新主文件
cfg.Language = "en";
cfg.Save();
Check(JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(backup)).Language == "en",
    "backup rolls forward on the next save");

// 4. 主/备都损坏：退回默认，两者都留证据，且不静默覆盖
string dir2 = ...;
File.WriteAllText(main2, "{ broken");
File.WriteAllText(backup2, "also broken");
var cfg2 = AppConfig.Load(main2, backup2);
Check(cfg2.Folders.Count == 1, "both unreadable => default config");
Check(Directory.GetFiles(dir2, "config.json.failed-*").Length == 1 &&
      Directory.GetFiles(dir2, "config.json.backup.failed-*").Length == 1,
    "both unreadable files get evidence copies");
cfg2.Save();
Check(Directory.GetFiles(dir2, "config.json.backup.failed-*").Length == 1 &&
      !File.ReadAllText(backup2).Contains("\"Folders\""),
    "first save after total failure does not clobber the evidence backup");

// 5. 全新安装（两文件都不存在）：默认配置并写出主文件
string dir3 = ...;
var cfg3 = AppConfig.Load(main3, backup3);
Check(cfg3 != null && File.Exists(main3), "fresh install writes the default config");

// 6. 次要但仍要保真：主有效时直接使用、不留证据文件
string dir4 = ...;
File.WriteAllText(main4, "{\"Language\":\"ja\"}");
var cfg4 = AppConfig.Load(main4, backup4);
Check(cfg4.Language == "ja", "valid main is used as-is");
Check(Directory.GetFiles(dir4, "*.failed-*").Length == 0, "no evidence copies when load succeeds");
```

- [ ] **Step 2: 运行并确认失败**

Run: `dotnet run --project tests/ConfigSafetyCheck`
Expected: 编译失败（`Load(string,string)` 不存在）→ 非 0。

- [ ] **Step 3: 实现**

`Core/AppConfig.cs` 修改要点：

```csharp
// 实例记住配置去哪读、去哪写（Load 时绑定；直接 new 的实例回退默认路径）
private string _configPath;
private string _backupPath;
private bool _skipBackupRefreshOnce; // 恢复来源未确认前不拿主文件覆盖它

public static AppConfig Load() => Load(Utils.GetConfigPath(), Utils.GetConfigPath() + ".backup");

public static AppConfig Load(string configPath, string backupPath)
{
    bool mainExisted = File.Exists(configPath);
    bool backupExisted = File.Exists(backupPath);
    AppConfig loaded = TryRead(configPath, out bool mainUnreadable) ?? TryRead(backupPath, out bool backupUnreadable);

    if (loaded == null)
    {
        // 先把读不出来的文件留一份证据，再允许退回默认配置（绝不静默覆盖）。
        if (mainExisted) PreserveFailedFile(configPath);
        if (backupExisted) PreserveFailedFile(backupPath);
        loaded = new AppConfig();
        loaded.Folders.Add(FolderData.Create(Localization.Get("UI_DefaultFolderName"),
            UiTokens.DefaultFolderColor, 100, 100, loaded.DefaultGridColumns));
    }

    loaded._configPath = configPath;
    loaded._backupPath = backupPath;
    loaded._skipBackupRefreshOnce = mainUnreadable || (loadedWasFromBackup);
    if (mainExisted && mainUnreadable || backupExisted && backupUnreadable) loaded.Save(); // 只写主文件，不动证据
    return loaded;
}
```

- `TryRead(path, out bool unreadable)`：内部做原来的反序列化 + 旧配置 `Name` 补齐，`unreadable = path 存在但解析失败或返回 null`。
- `PreserveFailedFile`：`Copy(path, path + ".failed-" + 时间戳[+序号])`。
- `Save()`：改用 `_configPath ?? Utils.GetConfigPath()` / `_backupPath ?? (configPath + ".backup")`；备份滚动条件改为
  `if (File.Exists(configPath) && !_skipBackupRefreshOnce) File.Copy(configPath, backupPath, true);`；
  主文件写入改用 `AtomicFile.WriteAllText(configPath, json)`；成功写出后 `_skipBackupRefreshOnce = false;`。

要点：`Save()` 在 `_skipBackupRefreshOnce` 为真时跳过备份滚动（此时主文件要么损坏、要么刚被证据副本保护，备份是恢复源）；第一次成功写入后清位，下一次保存备份正常滚动。

- [ ] **Step 4: 运行并确认通过**

Run: `dotnet run --project tests/ConfigSafetyCheck`
Expected: 全部 PASS，退出码 0。

- [ ] **Step 5: 回归：现有检查仍全绿**

Run: `dotnet run --project tests/LangCheck; dotnet run --project tests/StorageOpsCheck; dotnet run --project tests/StartupPanelsCheck`
Expected: 三个都退出码 0。

---

### Task 3: 强制保存上限 + `SaveFailed` 可见化

**Files:**
- Modify: `Core/AppConfig.cs`（`SaveDebounced` 双定时器、`SaveFailed` 静态事件）
- Modify: `Core/Localization.cs`（新增 `UI_SaveFailedTitle`、`UI_SaveFailedBody` ×3 语言）
- Modify: `Services/TrayIconService.cs`（订阅 + 一次气球）
- Test: `tests/ConfigSafetyCheck`（追加用例）

**Interfaces:**
- Produces:
  - `public static event Action<string> SaveFailed;`（保存失败时触发，参数为面向调试的消息）。
  - `public int ForceSaveIntervalMs { get; set; }`（默认 10000，`[JsonIgnore]`，测试可调小）。
  - `SaveDebounced(int delayMs = 1000)`：每次调用同时重启"空闲 debounce"和"强制上限"两个定时器。

- [ ] **Step 1: 追加失败的自检**

```csharp
// 7. 强制上限：debounce 被无限推迟时，强制定时器仍然落盘
string dir5 = ...; var cfg5 = AppConfig.Load(main5, backup5);
cfg5.ForceSaveIntervalMs = 200;
cfg5.SaveDebounced(60000); // 空闲 debounce 远未到
bool written = SpinWait.SpinUntil(() => File.Exists(main5) &&
    JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(main5)) != null, 3000);
Check(written, "force-save cap writes even while the debounce timer is pushed back");

// 8. 保存失败触发 SaveFailed
string badPath = Path.Combine(_root, "blocked", "config.json");
File.WriteAllText(Path.Combine(_root, "blocked"), "a file, not a directory");
var cfg6 = new AppConfig(); cfg6.BindPathsForTest(badPath); // 或 Load(badPath,...) 失败后使用
string reported = null;
Action<string> handler = msg => reported = msg;
AppConfig.SaveFailed += handler;
try { cfg6.Save(); } finally { AppConfig.SaveFailed -= handler; }
Check(reported != null, "SaveFailed is raised when the config cannot be written");
```

> 若 `BindPathsForTest` 显得多余：可改为把 Task 2 的 `Load` 绑定路径用于本用例（`Load(badPath, badPath+".backup")` 在没有可写目录时会抛？——`Load` 不写文件时安全；保存才失败）。优先用 `Load` 路径，不新增测试专用 API。

- [ ] **Step 2: 运行并确认失败**

Run: `dotnet run --project tests/ConfigSafetyCheck`
Expected: `ForceSaveIntervalMs`/`SaveDebounced` 行为失败（强制写未发生、事件未触发）。

- [ ] **Step 3: 实现**

- `AppConfig` 增加 `_forceSaveTimer`（`System.Timers.Timer`，`AutoReset=false`，Elapsed → `Save()`）与 `_timerLock`；`SaveDebounced` 在锁内同时重启两个定时器；`Save()` 在写完后停止两个定时器；`catch` 中 `Debug.WriteLine` + `SaveFailed?.Invoke(...)`。
- `Localization.cs`：en = `"Could not save settings"` / `"Kobold could not save to {0}. Your changes will be kept in memory and retried."`；zh = `"设置保存失败"` / `"Kobold 无法写入 {0}，更改会保留在内存中并稍后重试。"`；ja = `"設定を保存できませんでした"` / `"{0} に書き込めません。変更はメモリに保持され、後で再試行されます。"`。
- `TrayIconService`：构造函数订阅 `AppConfig.SaveFailed`；处理器 `Application.Current?.Dispatcher.BeginInvoke` 切 UI 线程；`_saveFailureReported` 为 false 时 `_trayIcon.ShowBalloonTip(title, string.Format(body, path), BalloonIcon.Warning)` 并置位；`Dispose` 中退订。

- [ ] **Step 4: 运行并确认通过**

Run: `dotnet run --project tests/ConfigSafetyCheck` → 全 PASS。
Run: `dotnet run --project tests/LangCheck` → PASS（新词条三语齐全）。

---

### Task 4: 关机/注销同步收尾保存

**Files:**
- Modify: `Core/AppConfig.cs`（`BeginClosing()`：置位禁新写 + 同步 `Save()`）
- Modify: `Core/WidgetManager.cs`（`BeginExitSave()` 转发）
- Modify: `App.xaml.cs`（`SessionEnding` 挂钩）
- Test: `tests/ConfigSafetyCheck`（追加用例）

**Interfaces:**
- Produces:
  - `public void AppConfig.BeginClosing()`：同步落盘一次；此后 `SaveDebounced` 不再调度写入。
  - `public void WidgetManager.BeginExitSave()`。

- [ ] **Step 1: 追加失败的自检**

```csharp
// 9. BeginClosing 同步落盘，并阻止后续 debounce 写入
string dir6 = ...; var cfg7 = AppConfig.Load(main6, backup6);
cfg7.Language = "ja";
cfg7.BeginClosing();
Check(File.Exists(main6) && File.ReadAllText(main6).Contains("\"ja\""),
    "BeginClosing writes synchronously before returning");

cfg7.Language = "en";
cfg7.SaveDebounced(50);
Thread.Sleep(300);
Check(!File.ReadAllText(main6).Contains("\"en\""),
    "no new debounced writes start after BeginClosing");
```

- [ ] **Step 2: 运行并确认失败**

Run: `dotnet run --project tests/ConfigSafetyCheck` → `BeginClosing` 不存在 → 编译失败。

- [ ] **Step 3: 实现**

- `AppConfig`：`private bool _closing; public void BeginClosing() { _closing = true; SafeSave(); }`；`SaveDebounced` 首行 `if (_closing) return;`；`Save()` 内部同样短路（但 `BeginClosing` 本身要能写一次——用私有 `SaveCore()` 绕过，或先写再置位：`_closing = true; Save();` 且 `Save` 不检查 `_closing`。选择后者，简单：只有 `SaveDebounced` 检查 `_closing`）。
- `WidgetManager`：`public void BeginExitSave() { _config.BeginClosing(); }`。
- `App.xaml.cs`：`OnStartup` 末尾（`_trayService` 创建后）：

```csharp
// Windows 注销/关机：落一次最终配置，且不再接受新的延迟写入。
Current.SessionEnding += (s, e) =>
{
    try { WidgetManager.Instance.BeginExitSave(); }
    catch (Exception ex) { Debug.WriteLine($"[Kobold] session-end save failed: {ex.Message}"); }
};
```

- OS 事件的接线（`SessionEnding` 挂钩）不做自动化测试，由构建 + 手动验证覆盖（见验证节）。

- [ ] **Step 4: 运行并确认通过**

Run: `dotnet run --project tests/ConfigSafetyCheck` → 全 PASS。

---

### Task 5: 文档 + 全量验证

**Files:**
- Modify: `README.md`（数据目录小节：说明 `config.json.failed-<时间戳>` 证据副本；测试清单加入两个新自检）

- [ ] **Step 1: README 更新**（英文 + 中文两处保持一致）
- [ ] **Step 2: 构建 0 警告 0 错误**

Run: `dotnet build Kobold.csproj -c Debug` → `0 个警告 0 个错误`。

- [ ] **Step 3: 全量自检（手工版，Plan B 会把它脚本化）**

Run（逐个，任一非 0 即失败）：
`tests/AtomicFileCheck`、`tests/ConfigSafetyCheck`、`tests/LangCheck`、`tests/StorageOpsCheck`、`tests/StartupPanelsCheck`、`tests/UiTokensCheck`、`tests/XamlLoadCheck`。

- [ ] **Step 4: 手动冒烟（可选，建议）**

1. 删除/改名 `%AppData%\Kobold\config.json` 为无效 JSON → 启动 → 应正常起来且出现 `config.json.failed-<ts>`；
2. 正常启动 → 拖动面板 → 关闭 → 重启 → 面板位置仍在（回归）。

---

## Self-Review

- **Spec 覆盖**：原子写=Task 1；失败留证据/保护恢复源=Task 2；强制上限+失败可见=Task 3；关机保存=Task 4；net48 兼容写法已内联（`File.Replace`、`Flush(true)`）。
- **风险**：`Save()` 新增路径逻辑影响所有保存调用点；Task 2 的 5 个回归自检 + Task 5 全量自检兜底。
- **回滚**：任务级小提交（经用户许可后），任一任务不满意可停。
- **明确不做**：6 小时备份定时器、恢复文件全分类、版本号防倒退（无异步写）、LMDB/插件分域。

---

## 执行记录（2026-09-24）

**状态：全部 5 个任务完成并验证。**

- Task 1-4 全部落地：`Core/AtomicFile.cs`、`Core/AppConfig.cs`（路径归属 / `*.failed-<时间戳>` 证据副本 / 恢复源保护位 / `ForceSaveIntervalMs` + `SaveFailed` / `BeginClosing`）、`Core/Localization.cs`（`Tray_SaveFailed*` ×3 语言）、`Services/TrayIconService.cs`（气球提示一次）、`App.xaml.cs`（`SessionEnding`）、`README.md`（数据目录说明）。
- 自检：`tests/AtomicFileCheck`（9 例）、`tests/ConfigSafetyCheck`（17 例）全绿；回归 `LangCheck` / `StorageOpsCheck` / `StartupPanelsCheck` / `UiTokensCheck` / `XamlLoadCheck` 全绿；构建 0 警告 0 错误。
- 相对计划的实现细节：证据副本在主文件"存在但读不出"时即保留（含备份可用、恢复成功的情况），由自检第 1 组保护；`RepairLegacyItems` 抽成独立方法。
