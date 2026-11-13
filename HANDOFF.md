# Kobold 交接文档

最后更新：2026-11-12  
交接基线：`e1649de`（`feat(control): add local agent control plane and kobold-config skill`）  
远端：`https://github.com/zzrqq-worklux/Kobold.git`，分支 `main`  
当前工作树：只有未跟踪的 `desktop.ini`（本地 Windows 文件夹元数据，不要提交）

> 这份文档面向“换一台 Windows 机器继续开发”的场景。先读第 1、3、4、5 节即可跑起来；控制面、配置位置和已知问题分别在第 7、8、10 节。

## 1. 项目概览

Kobold 是一个 Windows 10/11 桌面整理工具：

- WPF / .NET Framework 4.8 / C# 7.3 / x64
- 动态岛（island）+ 浮动的 folder / todo / note / search 面板
- 文件夹面板、待办、Markdown 笔记、文件搜索索引
- 支持 widget 合并为标签组（P2 功能）
- 新增本地 agent 控制面：`koboldctl.ps1` + `skill/kobold-config`

核心入口：

- `App.xaml.cs`：应用启动/退出、单实例 mutex、托盘、控制面启动
- `Core/WidgetManager.cs`：widget/group 生命周期、island、桌面图标、内存 trim
- `Core/AppConfig.cs`：配置读写、修复、schema
- `Controls/IslandWindow.xaml(.cs)`：动态岛和收起/展开动画
- `Core/FileSearch/FileSearchIndexService.cs`：文件搜索索引
- `Services/Control/ControlServer.cs`：本机命名管道控制服务
- `tools/koboldctl.ps1`：控制面 CLI
- `skill/kobold-config/`：可移植 skill

## 2. 当前状态

已完成并已推送：

- P2 widget group 工作树（合并/分离/重排/解散、island 折叠组、组内 live view 保留）
- 本地 agent 控制面（config / widget / group / search / diag）
- `koboldctl.ps1` 与安装器/打包集成
- `kobold-config` skill + references
- 桌面图标退出保护
- `StartWithWindows` / `PinStorageToQuickAccess` 立即生效
- island 收起/展开改为围绕中心，不再向左缩
- 本机已用 `tools/deploy.ps1` 部署到 `%LocalAppData%\Programs\Kobold`，当前进程运行正常

最近一次完整验证（2026-11-12）：

- build：0 warnings / 0 errors
- `Run-Checks -Group ci`：28/28
- `Run-Checks -Group checks`：36/36（35 个 check + config-pin）
- `GroupUiSpike -- --check`：GREEN 15/15
- `ControlCheck`：183 PASS
- `ControlScriptCheck`：24 PASS
- `ConfigSafetyCheck`：100 PASS
- `DesktopIconsCheck`：PASS
- `XamlLoadCheck`：452 PASS，`ALL XAML LOAD CHECKS PASSED`

## 3. 新机器环境要求

- Windows 10/11 x64（UI/WPF 检查需要真实交互式桌面）
- .NET Framework 4.8 Developer Pack
- .NET SDK（能构建 `net48` 的版本即可；仓库当前用 SDK 风格项目）
- Windows PowerShell 5.1+
- Git
- 建议安装 Visual Studio 2022（.NET 桌面开发 workload），但不是必需

首次搭建：

```powershell
git clone https://github.com/zzrqq-worklux/Kobold.git
cd Kobold
dotnet restore Kobold.csproj
dotnet build Kobold.csproj -c Debug
```

如果 `dotnet build` 报 `net48` targeting pack 缺失，先安装 .NET Framework 4.8 Developer Pack。

网络注意：本机 `git@github.com:...` 的 SSH 22 端口超时，HTTPS 可用。建议新机器直接使用：

```powershell
git clone https://github.com/zzrqq-worklux/Kobold.git
```

或给 origin 设置 HTTPS URL。

## 4. 构建、运行、部署

### 开发运行

```powershell
dotnet build Kobold.csproj -c Debug
# 直接运行构建产物，或从 Visual Studio 启动
.\bin\Debug\net48\Kobold.exe
```

注意：应用有单实例 mutex。测试/部署前先确认没有旧进程，否则第二个实例只会激活已有实例然后退出。

### 部署到本机安装目录

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy.ps1
```

行为：

1. `dotnet build -c Release`
2. 停止从 `%LocalAppData%\Programs\Kobold` 运行的旧进程
3. 复制运行时文件、`koboldctl.ps1`、卸载器到安装目录
4. 创建开始菜单快捷方式、写 HKCU 卸载项
5. 启动新实例

不想自动启动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy.ps1 -NoLaunch
```

用户安装入口是 `tools\install.cmd` / `tools\install.ps1`；部署脚本只是开发便利入口。

## 5. 测试矩阵

在仓库根目录执行。**UI/config 检查必须隔离 APPDATA**，不要读写真实 `%AppData%\Kobold\config.json`：

```powershell
$env:APPDATA = Join-Path $env:TEMP ("kobold-checks-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $env:APPDATA | Out-Null

powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group ci
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group checks
dotnet run --project tests\GroupUiSpike -- --check
```

**重要环境经验**：只隔离 `APPDATA`，保持真实 `LOCALAPPDATA`。把 `LOCALAPPDATA` 也指向临时目录时，`XamlLoadCheck` 会间歇触发 WPF `LineServicesCallbacks.GetCharExpansionInfoFullMixed` 的 `Environment.FailFast`（`0x80131623`），这是 WPF 文本栈/字体缓存问题，不是控制面逻辑错误。最终全绿矩阵用的就是“隔离 APPDATA、真实 LOCALAPPDATA”。

常用聚焦检查：

```powershell
dotnet run --project tests\ControlCheck
dotnet run --project tests\ControlScriptCheck
dotnet run --project tests\ConfigSafetyCheck
dotnet run --project tests\DesktopIconsCheck
dotnet run --project tests\XamlLoadCheck
dotnet run --project tests\IslandLayoutCheck
```

`XamlLoadCheck` 较慢（约 2.5–10 分钟，取决于机器/UI 状态），不要用 30 秒超时判断失败。

安装器测试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\installer.Tests.ps1
```

预期当前为 83 passed / 0 failed。

## 6. 配置、数据与日志位置

真实用户数据：

- `%AppData%\Kobold\config.json`：当前配置
- `%AppData%\Kobold\config.json.backup`：应用原子写保留的上一代备份
- `%AppData%\Kobold\config.json.failed-*`：无法读取时的证据副本
- `%AppData%\Kobold\backups\config-*.json`：控制面显式备份（保留最近 20 个）
- `%AppData%\Kobold\Storage\`：folder widget 的收纳文件
- `%AppData%\Kobold\FolderIcons\`：folder 自定义图标
- `%LocalAppData%\Kobold\search-index.db*`：文件搜索 SQLite 索引
- `%LocalAppData%\Kobold\logs\app.log`：应用/控制面审计日志（1 MB 轮转 `.1`）
- `%LocalAppData%\Kobold\logs\search.log`：搜索服务日志

绝不要让 agent 直接编辑运行中的 `config.json`。所有写操作走 `koboldctl`，带 revision、备份和 dry-run。

## 7. Agent 控制面

### 架构

```text
koboldctl.ps1
  -> \\.\pipe\Kobold.Control.v1.<userSid>.<sessionId>
       -> ControlServer (background thread)
            -> ConfigControlService
            -> WidgetGroupControlService
            -> DiagnosticsService
            -> FileSearchIndexService
```

- 管道只允许当前用户 SID + LocalSystem；无 HTTP、无网络监听。
- 一个连接一个 JSON 行请求/响应。
- 所有变更通过 `Application.Current.Dispatcher` 在 UI 线程执行。
- 写操作必须带 `baseRevision`（CLI 未显式传时会先 `config.get` 读取一次）。
- 破坏性命令需要 `confirm`；CLI 用 `--yes`。
- 写前自动备份；`config.replace/restore` 结构变更要求 `--restart`。
- restart helper 由应用自身以 `--restart-helper <pid> <exe>` 模式启动，等旧进程退出后再拉起新实例；CLI 只轮询新管道。

### 常用命令

```powershell
$ctl = ".\tools\koboldctl.ps1"

& $ctl status --json
& $ctl config get --summary --json
& $ctl config describe --json
& $ctl diag snapshot --json
& $ctl diag folders --json
& $ctl search status --json
& $ctl diag logs --name search --lines 200 --json

# 预览/应用设置
& $ctl config patch --file .\patch.json --base-revision $rev --dry-run --json
& $ctl config patch --file .\patch.json --base-revision $rev --yes --json

# widget / group
& $ctl widget add --kind note --name "会议记录" --json
& $ctl widget update --id <id> --name "新名字" --color "#6A9BCC" --json
& $ctl widget delete --id <id> --yes
& $ctl group merge --target <id> --member <id> --member <id> --json
& $ctl group detach --group <id> --widget <id> --json
& $ctl group reorder --group <id> --from 1 --to 0 --json
& $ctl group dissolve --group <id> --yes
```

`config replace/restore`：

```powershell
& $ctl config replace --file .\full-config.json --base-revision $rev --dry-run --json
& $ctl config replace --file .\full-config.json --base-revision $rev --yes --restart --json
& $ctl config backup --label before-agent --json
& $ctl config restore --backup "$env:AppData\Kobold\backups\config-....json" --base-revision $rev --yes --restart --json
```

退出码：

- `0` 成功
- `1` 校验/冲突/业务错误/restart_failed
- `2` 连接/协议错误（`app_not_running` 等）
- `3` 结果说 `restartRequired:true` 但没有 `--restart`（`--dry-run` 时为 0）

### 安装目录 CLI 与 skill

部署后 `koboldctl.ps1` 会出现在：

```text
%LocalAppData%\Programs\Kobold\koboldctl.ps1
```

`skill/kobold-config/` 是独立可移植目录。用法：

1. 把整个 `kobold-config` 目录复制到你的 agent 的 skills 目录。
2. 让 agent 读取其中的 `SKILL.md`。
3. 用 `scripts/find-koboldctl.ps1` 找 CLI；它优先安装目录，其次仓库 `tools\`。

skill 不做特定 agent 路径适配，不写安装脚本，也不假设任何 agent 的名字/目录。

## 8. 关键设计决策

- 2C：不提供 `app.showSettings`；设置窗口只由用户从 UI 打开。
- 3B：结构变更重启用应用内 restart helper，不新增可执行文件。
- 4B：`StartWithWindows` / `PinStorageToQuickAccess` 立即生效，失败返回 warning，不要求重启。
- 5A：`diag.folders` 返回所有 folder 的 `active` + `watcherActive`，包括 group 内非 active tab。
- 桌面图标：本次运行隐藏了图标时，退出/重启/注销立即显示；下一次启动按 `HideDesktopIcons` 再隐藏。
- island：内容在窗口中居中，窗口以 `IslandX` 中心定位；收起/展开围绕中心，不再左对齐。
- 配置补丁：RFC 7386 merge patch；`Widgets/Groups/Folders/FrequentFiles/SchemaVersion` 不允许通过 `config.patch` 改。
- `config.replace/restore` 的结构变更：`--restart` 才写盘并启动 helper。

## 9. 代码地图

```text
App.xaml.cs                 应用启动/退出、控制面、restart helper 模式
Core/
  AppConfig.cs              config schema/load/save/repair
  WidgetManager.cs          widget/group/window/island/desktop icons/trim
  WidgetData.cs             widget 基类 + UnknownData
  WidgetGroup.cs            group 数据
  GroupHostPolicy.cs        group 成员/active/pin 规则
  GroupDragPolicy.cs        merge/detach/reorder 规则
  IdleTrimPolicy.cs         idle trim 策略
  IslandLayout.cs           island 布局纯数学
  FileSearch/               搜索索引/scan/store/settings
  Control/                  协议、config、widget/group、诊断、restart helper
Controls/
  IslandWindow.*            island + 收起/展开动画 + 组 tile
  WidgetGroupWindow.*       group 标签窗口/成员 live view
  FolderPanelView.*         folder 内容、浏览 watcher、图标
  FolderWidget.* / TodoWidget.* / NoteWidget.* / SearchWidget.*
Services/
  Control/ControlServer.cs  命名管道服务
  Control/ControlRequestRouter.cs
  FullscreenWatcher.cs
  MemoryTrimmer.cs
tools/
  koboldctl.ps1             控制面 CLI（唯一实现）
  Run-Checks.ps1            本地/CI 检查矩阵
  installer-common.ps1      安装/卸载共享逻辑（含 koboldctl 拷贝）
  deploy.ps1                开发部署入口
tests/
  ControlCheck              协议/config/helper/guard 等 CI 检查
  ControlScriptCheck        CLI + 假管道（local checks）
  ConfigSafetyCheck         config 恢复/覆盖安全
  DesktopIconsCheck         Explorer 图标列表定位
  XamlLoadCheck             WPF/XAML/组/控制面 UI 集成
  GroupUiSpike              交互式 group UI 冒烟
skill/kobold-config/        可移植 agent skill
```

## 10. 已知问题 / 后续待办

这些不阻塞当前使用，但换机器继续开发时应知道：

1. 【已在 Linux 端修复，待 Windows 矩阵验证】`config.replace/restore` 现在由服务端强制 `confirm:true`：真正写入前检查，`dry-run` 和 `restartRequired` 预览不要求；缺失时返回 `forbidden`（CLI 退出码 1）。绕过 CLI 直接说协议也写不动。
2. 【已修复，待验证】`RestartHelper.Run` 等父进程 15 秒；父进程仍未退出时不再启动新实例，helper 直接返回 false（进程退出码 1）并写日志，避免新实例撞 mutex 后“什么都没发生”。
3. 【已修复，待验证】`ControlServer` 现在有界：等待连接 15 秒、单个请求 30 秒看门狗（超时直接丢弃连接）、消息上限按 UTF-8 字节在读取过程中限制 1 MiB（超限回 `invalid_request`）、`Stop()` 会 join listener 线程 2 秒。注：30 秒是“读+派发+写”的总预算，UI 线程被模态框卡住超过 30 秒时 CLI 会收到断连而不是一直挂住。
4. 【已补覆盖，待验证】`diag.search.query` 现在覆盖非空查询的整条映射（path/name/isDirectory/extension/sizeBytes/modifiedUtc、limit 截断与 limitReached），行数据通过 `DiagnosticsService.QueryOverrideForChecks` 注入；真实索引的查询语义仍由 FileSearchCheck 覆盖。
5. 【已修，待验证】`config.replace` 拒绝更新 `SchemaVersion` 的文档（`validation_failed`）；`config.patch` 现在有服务端语义范围校验（`ConfigLimits`）：`PanelOpacity` 0.2–1、`ItemScale` 0.5–3、`IslandCollapseDelay` 0–60、`DefaultGridColumns` 1–12，`Language`/`Theme`/`IconStyle` 只接受已知枚举，`IslandX` 必须是有限数；只校验本次 patch 提到的字段，`config.validate` 同样返回 `ok:false`。
6. 【已修，待验证】`MenuRenderCheck` / `WindowSwitcherCheck` 现在和 `XamlLoadCheck` 一样先设 `App.SuppressStartupForChecks`；`WindowSwitcherCheck` 同时补了 `InternalsVisibleTo`。
7. `tests/XamlLoadCheck` 的 glyph 扫描排除 `.superpowers` 证据目录；这是测试工作区产物，不是生产代码。
8. `desktop.ini` 是当前工作树里的未跟踪本地文件，未提交；新机器不需要它。
9. `LOCALAPPDATA` 隔离会触发 WPF 文本栈 fail-fast（见第 5 节）；不要把它当作产品回归。

## 11. 另一台机器开发清单

1. 安装 .NET Framework 4.8 Developer Pack + .NET SDK + Git。
2. `git clone https://github.com/zzrqq-worklux/Kobold.git`
3. `git log -1 --oneline` 确认在 `main`，基线至少 `e1649de`。
4. `dotnet build Kobold.csproj -c Debug`，确认 0 warnings / 0 errors。
5. 跑 CI：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group ci`。
6. 跑本地完整矩阵：隔离 `APPDATA` 后 `Run-Checks -Group checks` + `GroupUiSpike -- --check`。
7. 部署：`tools\deploy.ps1`（会停掉本机旧安装实例再启动新实例）。
8. 装好后验证：`%LocalAppData%\Programs\Kobold\koboldctl.ps1 status --json` 返回 `Ok:true`。
9. 若需要 agent 能力：把 `skill\kobold-config` 整个目录复制到你的 agent skills 目录。
10. 先不要直接改真实 `%AppData%\Kobold\config.json`；用 `koboldctl config patch/backup/restore`。

## 12. 发布/部署检查

- `dotnet build Kobold.csproj -c Debug`：0/0
- `Run-Checks -Group ci`：28/28
- 隔离 APPDATA 后 `Run-Checks -Group checks`：36/36（config-pin True）
- `GroupUiSpike -- --check`：GREEN
- `tools\deploy.ps1` 成功，安装目录含 `koboldctl.ps1`
- `koboldctl status --json`：`Ok:true`
- 真实 config 不要被检查改写；UI 检查只隔离 `APPDATA`

## 13. Git / 交接

- 分支：`main`
- 交接基线 commit：`e1649de9bc29b824f7d9d8f6b8c4d503ed52c10c`
- 远端：`https://github.com/zzrqq-worklux/Kobold.git`
- 本交接文档会作为该 commit 之后的独立文档提交推送。
- 工作树中除 `desktop.ini` 外不应有未提交改动；`desktop.ini` 保持未跟踪。
- 不要 reset/stash/删除用户数据；不要提交 `%AppData%` / `%LocalAppData%` 下的真实配置。

## 14. 保护文件

按项目约定，以下文件不要随意改：

- `Core/FolderColor.cs`
- `Helpers/FolderIconSource.cs`
- `tests/FolderColorCheck/Program.cs`

如需改它们，先确认有明确需求和对应回归检查。
