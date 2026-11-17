# 自动更新（C 档）实施计划

Spec: `docs/superpowers/specs/2026-11-17-auto-update-design.md`（绑定权威，冲突时以 spec 为准）

## Global Constraints

- .NET Framework 4.8 / C# 7.3 / WPF / x64，PowerShell 5.1。**不新增 NuGet 依赖**；`Newtonsoft.Json` 已在项目里。
- 所有用户可见文案走 `Core/Localization.cs`，en / zh / ja 三份都要加，缺一不可。
- 注释与标识符用英文，风格跟随所在文件；不要重排无关代码。
- 编译必须 0 warning / 0 error。本机没有 .NET 工具链，**编译在 Windows 虚拟机里做**：
  `/home/agent/winvm/build-repo.sh /home/agent/dev/Kobold-update`（可加第二个参数跑某个自检项目，
  例如 `... /home/agent/dev/Kobold-update UpdateCheck`）。这个脚本一次只能跑一个，别并发。
- 不要动 `Releases/`、不要改版本号、不要打 tag、不要 push。
- 测试不得访问外网；需要 HTTP 时用 `HttpListener` 监听 `http://127.0.0.1:<随机端口>/`。
- 自检项目沿用 `tests/ShellOpsCheck` 的形态：`internal static class Program`，`Check(bool, string)` 收集失败，失败 exit 1。

## Task 1: manifest 与版本策略（纯逻辑）

新增：

- `Services/Update/UpdateManifest.cs`
  - `class UpdateManifest { string Version; string Url; long Size; string Sha256; string NotesUrl; }`
  - `const string DefaultManifestUrl = "https://api.merrickzq.work/downloads/kobold/latest.json";`
  - `const string DefaultHost = "api.merrickzq.work";`
  - `static bool TryParse(string json, out UpdateManifest manifest, out string error)`：用 `JObject.Parse`；
    字段缺失或类型不对返回 false 并给英文错误串。校验规则：
    - `version` 能按 `System.Version` 解析且至少 3 段（`2.0.1` 合法，`2.0` 非法）
    - `url` 能被 `Uri` 解析、scheme 是 https、host 等于 `DefaultHost`（忽略大小写）
    - `size` 是 1..200000000 之间的整数
    - `sha256` 是 64 位十六进制（大小写都接受，统一转小写保存）
    - `notes_url` 可选；有则必须是 https
  - 额外提供 `static bool TryParse(Uri)` 不需要——URL 由调用方给。
- `Services/Update/UpdateVersion.cs`
  - `static bool TryParse(string text, out Version version)`：接受 `2.0.1` / `2.0.1.0`，允许结尾多余空白。
  - `static bool IsNewer(string local, string remote)`：两边都能解析才比较，任一边非法返回 false。
- `Services/Update/UpdatePolicy.cs`
  - `static bool ShouldOffer(UpdateManifest manifest, string localVersion, string skippedVersion)`
  - `static bool ShouldCheck(bool enabled, string lastCheckUtcIso, DateTime nowUtc, TimeSpan interval)`：
    `lastCheckUtcIso` 为空/解析失败 → true；否则 `nowUtc - last >= interval`。
  - `static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(24);`

新增 `tests/UpdateCheck/UpdateCheck.csproj`（复制 ShellOpsCheck 的结构，`AssemblyName` = `Kobold.UpdateCheck`，
`RootNamespace` = `Kobold.UpdateCheck`，引用 `..\..\Kobold.csproj` 加 `System.IO.Compression` +
`System.IO.Compression.FileSystem`）和 `tests/UpdateCheck/Program.cs`，覆盖：

- 合法 manifest 解析成功，字段值正确
- 缺 `version` / `sha256` 长度不对 / `size` 为 0 / `size` 超上限 / `url` 是 http /
  `url` 主机不是 `api.merrickzq.work` / 版本只有两段 → 全部拒绝，`error` 非空
- `sha256` 大写能接受并统一小写
- 版本比较：`2.0.10` 比 `2.0.9` 新；`2.0.1` 与 `2.0.1.0` 相等；本地非法/远端非法 → false
- `ShouldOffer`：远端更新 → true；相同/更旧 → false；等于 `skippedVersion` → false
- `ShouldCheck`：`enabled=false` → false；从未检查 → true；1 小时前 → false；25 小时前 → true；
  `lastCheckUtcIso` 是垃圾字符串 → true

改 `tools/Run-Checks.ps1`：在 `$checks` 矩阵里加 `@{ Name = 'UpdateCheck'; Ci = $true }`
（位置在 `ShellOpsCheck` 之后），并确认 `.github/workflows/checks.yml` 不需要显式改（它跑 `-Group ci`）。

验收：VM 里 `dotnet build` 0 warning；`build-repo.sh` 带 `UpdateCheck` 跑，全部 PASS、exit 0；`Run-Checks -Group checks` 里能看到它。

## Task 2: 下载、校验与解压

新增 `Services/Update/UpdateDownloader.cs`（依赖 Task 1 的 manifest，不得改动 Task 1 的公开签名）：

- `class UpdateException : Exception`（消息是英文短句，UI 层负责翻译）
- `static async Task DownloadAsync(UpdateManifest manifest, string destinationPath, IProgress<long> progress, CancellationToken token)`
  - `HttpClient`，`Timeout` 120 秒，`User-Agent: Kobold/<当前版本>`
  - 先写 `<dest>.part`，边读边算 SHA-256（`IncrementalHash` 或 `SHA256.Create()` + `TransformBlock`）
  - 收到的字节数 ≠ `manifest.Size` → 删除并抛 `UpdateException("size mismatch")`
  - hash ≠ `manifest.Sha256` → 删除并抛 `UpdateException("checksum mismatch")`
  - 成功后才把 `.part` 移到 `destinationPath`（同目录 `File.Replace`/`File.Move`）
  - `progress` 报累计字节数；取消时清理 `.part`
- `static string Extract(string zipPath, string stageDir, string expectedVersion)`
  - `ZipArchive` 打开；每个 entry：把 `Path.GetFullPath(Path.Combine(stageDir, entry.FullName))`
    规范化后必须仍以 stageDir 为前缀，否则抛 `UpdateException("unsafe entry")`
  - 全部解压后要求存在 `Kobold.exe`，且 `FileVersionInfo.GetVersionInfo(...).FileVersion`
    的前三段等于 `expectedVersion` 的前三段，否则抛 `UpdateException("version mismatch")`
  - 返回 stage 目录绝对路径；失败时删掉 stage 目录再抛
- `static bool TryGetInstalledVersion(string exePath, out string version)`

新增测试（在 `tests/UpdateCheck/Program.cs` 里加，Task 1 的文件直接追加）：

- 用 `ZipArchive` 现造一个含 `Kobold.exe`（随便一个能读 FileVersion 的 exe —— 用当前测试进程的
  `Assembly.GetEntryAssembly().Location` 复制一份改名）的 zip，正常解压成功
- zip 里加一个 `..\evil.txt` entry → `unsafe entry`
- zip 里没有 `Kobold.exe` → 拒绝
- `Kobold.exe` 的 FileVersion 与 manifest 不符 → `version mismatch`
- `HttpListener` 起在本机随机端口：正常下载成功且 hash 正确；改掉 manifest 的 sha256 → 抛
  `checksum mismatch` 且 `.part` 被清理；`Content-Length` 小于 `manifest.Size` → `size mismatch`

验收：VM build 0 warning；`UpdateCheck` 全绿。

## Task 3: 替换脚本 + 应用端 applier

新增 `tools/apply-update.ps1`：

```powershell
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$StageDir,
    [Parameter(Mandatory=$true)][string]$InstallDir,
    [string]$LogPath,
    [int]$WaitSeconds = 30,
    [switch]$DryRun
)
```

行为（每步都写日志，日志默认 `%LOCALAPPDATA%\Kobold\update.log`，追加写）：

1. 等 `$ProcessId` 退出，最多 `$WaitSeconds` 秒；还活着就 `Stop-Process -Id`（只针对这个 PID）。
2. 备份：`$InstallDir\.update-backup-<yyyyMMddHHmmss>`，把 `$InstallDir` 下的文件（不含子目录里的
   `.update-backup-*`）复制过去；备份失败则中止并退出 2（宁可不更新也不能没退路）。
3. 复制 `$StageDir\*` 到 `$InstallDir`，每个文件 5 次重试、每次间隔 1 秒；任何文件最终失败 → 回滚。
4. 启动 `$InstallDir\Kobold.exe`，等 15 秒：进程还活着 → 成功，删掉本次备份，退出 0。
5. 失败/回滚：把备份文件复制回去（同样重试），启动旧 exe，退出 3。
6. `-DryRun`：只做备份和复制到一个临时目标目录，不等待进程、不启动 exe，打印每一步，退出 0。

新增 `Core/UpdateInstall.cs`：

- `static bool TryGetInstallDir(out string installDir)`：读 `HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Kobold`
  的 `InstallLocation`；读不到返回 false。
- `static bool IsSelfUpdatable()`：`installDir` 存在、`AppDomain.CurrentDomain.BaseDirectory`
  规范化后与它相等、目录可写（写一个临时文件再删）、`Kobold.exe` 在该目录里。
- `static string ToolsScriptPath(string fileName)`：`<installDir>\tools\<fileName>`，不存在返回 null。

新增 `Services/Update/UpdateApplier.cs`：

- `static bool Apply(string stageDir, string installDir, int processId, string logPath)`：
  拼 `powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "<script>" -ProcessId ...`
  用 `ProcessStartInfo.CreateNoWindow = true`、`UseShellExecute = false` 启动，**不等待**。
- 脚本不存在 → 返回 false，调用方回退到「打开下载页」。

把 `apply-update.ps1` 加进 `tools/installer-common.ps1` 的 `$KoboldToolFiles`（安装与打包都靠这个列表），
并在 `tools/package.ps1` 无需额外改动的前提下确认它真的进了 zip。

测试：在 `tests/UpdateCheck` 里加 `UpdateInstall` 的纯路径逻辑测试（用临时目录模拟安装目录，
用可注入的 `installDir` 重载：给 `IsSelfUpdatable(string installDir, string baseDir)` 这种签名，
不要在测试里碰真实注册表）。

验收：VM build 0 warning；`UpdateCheck` 全绿；`tools\apply-update.ps1 -DryRun` 在虚拟机里对着一个
临时目录跑通（把输出贴进报告）。

## Task 4: 配置项、设置页与三语文案

- `Core/AppConfig.cs`：新增
  - `public bool UpdateCheckEnabled { get; set; } = true;`
  - `public bool UpdateAutoInstall { get; set; } = true;`
  - `public string SkippedUpdateVersion { get; set; } = "";`
  - `public string LastUpdateCheckUtc { get; set; } = "";`
  照现有字段的写法放进属性区；确认 `Load`/`Save` 往返不丢（默认值来自属性初始化器即可）。
- `Windows/SettingsWindow.xaml`：在「关于」卡片上方加一块「更新」区域，包含
  - 状态行 `UpdateStatusText`（文案由代码填）
  - 按钮 `UpdateCheckButton`「检查更新」
  - 两个开关（照现有 CheckBox 样式）：`UpdateAutoCheckBox`「启动时自动检查」、
    `UpdateAutoInstallBox`「退出时自动安装」
  - 链接按钮 `UpdatePageButton`「打开下载页」（`Process.Start` 打开 `https://api.merrickzq.work/downloads/kobold/`）
- `Windows/SettingsWindow.xaml.cs`：读配置填控件；保存时写回（找到现有 Save 逻辑挂进去）；
  「检查更新」暂时只调用一个占位（Task 5 会接上 `UpdateService`），不要留 TODO 注释——直接调用
  Task 5 之后存在的接口会编译失败，所以本任务**只做 UI 与配置持久化**，按钮先禁用并在 Task 5 接线。
- `Core/Localization.cs`：新增三语 key（值按现有风格，英文简洁、中文准确、日文自然）：
  `Settings_UpdateSection`、`Settings_UpdateAutoCheck`、`Settings_UpdateAutoInstall`、
  `Settings_UpdateCheckNow`、`Settings_UpdateOpenPage`、`Settings_UpdateIdle`、
  `Settings_UpdateChecking`、`Settings_UpdateUpToDate`、`Settings_UpdateAvailableFormat`（`{0}` 版本号）、
  `Settings_UpdateDownloadingFormat`（`{0}` 百分比）、`Settings_UpdateReadyFormat`、
  `Settings_UpdateFailedFormat`、`Settings_UpdateUnsupported`。

验收：VM build 0 warning；设置页能打开（`XamlLoadCheck` 通过）；三语 key 无缺失（`LangCheck` 通过）。

## Task 5: 运行时服务、托盘与启动/退出接线

新增 `Services/Update/UpdateService.cs`：

- 单例 `Instance`；状态枚举 `Idle / Checking / Available / Downloading / Ready / Failed`；
  `event Action StateChanged`
- `void Start()`：启动 30 秒 `DispatcherTimer` 后调用 `CheckAsync()`；之后每 24 小时一次
- `async Task CheckNow()`：读配置与 `UpdatePolicy`，GET manifest（`HttpClient`，8 秒超时），
  更新 `LastUpdateCheckUtc` 并保存；有新版本 → `Available`，`UpdateAutoInstall` 且可自更新时
  自动进入下载
- `async Task DownloadNow()`：调 `UpdateDownloader`，进度更新状态，完成后 `Ready`（记录 stage 路径）
- `void ApplyNow()`：`UpdateApplier.Apply(...)` 成功后 `Application.Current.Shutdown()`
- `void SkipVersion()`、`void Reset()`
- 所有网络/IO 异常都要吞掉并落到 `Failed` + 诊断日志（`AppDiagnostics.Write`），绝不抛到 UI 线程外

接线：

- `App.xaml.cs`：主窗口/管理器就绪后 `UpdateService.Instance.Start()`；`OnExit` 里若状态是 `Ready`
  且 `UpdateAutoInstall` 且可自更新，先 `UpdateApplier.Apply(...)` 再退出。
- `Services/TrayIconService.cs`：菜单最上方加一项（仅 `Available/Downloading/Ready` 时显示）：
  - `Available/Downloading` → 「有更新 <版本>（下载中 42%）」点击触发 `DownloadNow`
  - `Ready` → 「重启并更新到 <版本>」点击触发 `ApplyNow`
  - 第一次变 `Available` 时弹一次气球（`ShowBalloonTip`），同一版本只弹一次
- `Windows/SettingsWindow.xaml.cs`：把 Task 4 禁用的「检查更新」按钮接上 `UpdateService.CheckNow()`，
  订阅 `StateChanged` 刷新状态行与按钮可用性。

验收：VM build 0 warning；`Run-Checks -Group ci` 全绿；在虚拟机里用一份假 manifest
（本机 `HttpListener` + 环境变量 `KOBOLD_UPDATE_MANIFEST_URL` 覆盖，仅当环境变量存在时生效）
手测：状态从 Checking → Available，托盘出现条目。

## Task 6: 文档、CHANGELOG 与打包确认

- `CHANGELOG.md`：`## Unreleased` 下加 `### 新增` 一条（自动检查、后台下载校验、退出时自动安装、
  失败回滚）与 `### 变化` 一条（设置页新增更新区）
- `README.md`：在设置相关小节里加一小段「自动更新」说明（中英各一处，保持现有双语文风）
- `docs/FEATURES.md` 与 `docs/FEATURES.zh.md`：在设置/系统相关条目补上自动更新的行为与边界
- `docs/DEVELOPMENT.md`：加一小节说明怎么在虚拟机里测更新（`-DryRun`、假 manifest、回滚验证）
- 确认 `tools/package.ps1` 打的包里包含 `tools\apply-update.ps1`

验收：文档改动只增不删；`Run-Checks -Group checks` 全绿。

## Task 7: 端到端验证（controller 执行，不派实现者）

- 在虚拟机里造一个 2.0.2 的测试包，本机起 `HttpListener` 供 manifest 与 zip
- 走完 check → download → extract → apply 全链路，确认安装目录里的 exe 版本真的变成 2.0.2
- 再造一个坏包（sha 不符）确认失败路径不破坏现有安装
- 断言：`Run-Checks -Group ci` 28+ 全绿；`-Group installer` 全绿
