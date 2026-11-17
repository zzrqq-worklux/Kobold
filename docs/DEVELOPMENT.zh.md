# Kobold — 开发说明

面向贡献者的构建、测试、打包、数据目录与升级兼容说明；用户手册在
[README](../README.md)。

> 用户手册：[README](../README.md) · 完整功能清单：[FEATURES.zh](FEATURES.zh.md)

## 构建与测试

需要 Windows + .NET SDK（8.x）：

```powershell
dotnet build Kobold.csproj -c Release
# 产物：bin\Release\net48\Kobold.exe
```

`tests/` 下是控制台自检，通过一个入口运行（逐项执行、汇总结果、失败非零退出）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -List   # 只看矩阵
```

`checks` = 全部控制台自检；`ci` = 可在 GitHub runner 上运行的保守子集（依赖真实桌面
会话的检查留在本地）；`installer` = 安装器纯逻辑自检（只在临时目录，不写注册表）；
`all` = 两者之和。

正常交互式 Windows 桌面上的矩阵：

| 命令 | 检查数 |
|---|---|
| `Run-Checks.ps1 -Group ci` | **28**（GitHub Actions 子集） |
| `Run-Checks.ps1 -Group checks` | **36**（本地完整控制台矩阵） |
| `dotnet run --project tests\GroupUiSpike -- --check` | **15** 项交互 UI 检查 |
| `Run-Checks.ps1 -Group installer` | 仅安装器逻辑 |

`GroupUiSpike` 需要真实桌面会话，单独运行；`FileSearchCheck` 覆盖 SQLite
索引、扫描器、监听与服务，`XamlLoadCheck` 加载全部 XAML 界面。

Release 输出是平铺布局：`Kobold.exe` 加 11 个运行时文件，其中包括 SQLite 原生
运行时（`e_sqlite3.dll`、`Microsoft.Data.Sqlite.dll`、`SQLitePCLRaw.*`）和
`System.Buffers` / `System.Memory` / `System.Numerics.Vectors` /
`System.Runtime.CompilerServices.Unsafe` 支持程序集；`tools\package.ps1` 与
安装器复制同一份列表。

GitHub Actions 工作流在 HEAD 提交信息含 `[ci]`（或手动触发）时运行 `ci` 组，
避免每次 push 都消耗 runner。

端到端安装/卸载由发布前的一次性人工演练覆盖（会写真实的卸载注册表项与快捷方式）。

打包发布 zip，版本号默认读 `Kobold.csproj`（单一真源）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1
```

Release 正文从 `CHANGELOG.md` 生成；缺少对应版本小节会直接报错——不让没有更新
日志的版本发布：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-notes.ps1 -Version 1.2.0
# 输出正文；用 -OutFile notes.md 写入文件
```

## 在虚拟机里测更新

更新器只请求固定的 manifest，
`https://api.merrickzq.work/downloads/kobold/latest.json`。本机测试检查流程时用
`KOBOLD_UPDATE_MANIFEST_URL` 覆盖该地址；除固定的 HTTPS 主机外，它只接受一种
形式——回环接口上的 `http://` URL。没有任何面向用户的设置暴露它。manifest 里的
下载 `url` 仍固定在 `api.merrickzq.work` 主机（解析器强制校验），所以这个覆盖
只用于检查 / 提示流程；要完全本地地跑下载，需要把该主机名映射到测试服务器。

```powershell
# 1. 在临时目录造一个发布目录和一个安装目录：发布目录里要有 FileVersion 与
#    manifest 一致的 Kobold.exe，以及包的其余内容（包括 tools\）。
$root = Join-Path $env:TEMP 'Kobold-apply-test'
$stage = Join-Path $root 'stage'
$install = Join-Path $root 'install'
New-Item -ItemType Directory -Force $stage, $install | Out-Null
Copy-Item C:\work\bin\Debug\net48\Kobold.exe $stage
Copy-Item C:\work\bin\Debug\net48\Kobold.exe $install   # 即将被替换的那份

# 2. 不等待进程、不启动 exe 地演练替换：备份与复制都落在临时目录，只读取
#    安装目录。每次改完脚本都跑一遍。
powershell -NoProfile -ExecutionPolicy Bypass -File C:\work\tools\apply-update.ps1 `
    -ProcessId 0 -StageDir $stage -InstallDir $install -DryRun
# -> "dry run: success"，退出码 0

# 3. 把检查指向回环上的 manifest：第一次自动检查在启动 30 秒后进行。先关掉
#    「退出时自动安装」，否则发现新版本会立刻从固定主机开始下载。
$env:KOBOLD_UPDATE_MANIFEST_URL = 'http://127.0.0.1:8765/latest.json'
```

manifest 的字段（version、url、size、sha256）可以照抄 `tests\UpdateCheck`
里的示例，下载服务用同样的 `HttpListener` 写法；manifest 的下载 `url` 必须是
`https://api.merrickzq.work/...`，安装包必须与 size 和 SHA-256 一致。

**回滚验证。** 对一个临时复制的安装目录跑不带 `-DryRun` 的脚本，stage 里的
`Kobold.exe` 故意做成启动不了的样子（把文本文件改名为 `Kobold.exe` 即可）：
`-ProcessId 0` 跳过等待，脚本备份、复制、启动失败、还原备份、重新启动旧 exe，
最后以退出码 3 结束。`%LocalAppData%\Kobold\update.log` 记录每一步；成功的一轮
会删掉自己的备份，只保留最新的 `.update-backup-*` 目录。强杀路径也能同样验证：
把一个别的可执行文件复制成 `Kobold.exe` 并启动（它会忽略等待），用它的 PID 配合
`-WaitSeconds 1` 运行脚本，日志里应先出现 `--restore-desktop-icons` 行，再出现
`stopping it`。

`Run-Checks.ps1 -Group checks` 会连同其余控制台矩阵一起运行
`tests\UpdateCheck`（共 36 项）；脚本本身由上面这轮虚拟机手动验证覆盖。

## 数据目录

- 配置：`%AppData%\Kobold\config.json`（自动备份为 `config.json.backup`）。
  读不出的配置文件会先保留为 `config.json.failed-<时间戳>`，再退回默认设置。
- 组件以新的 `Widgets` 格式保存，旧版本读不出该格式：回退到 v1.3.0 / v1.3.1
  会以默认设置启动，并把原配置文件保留为 `config.json.failed-<时间戳>`；更早的
  版本没有这一保护。格式里的未知类型不会被丢掉：v1.4.0 读到含笔记的配置会按
  未知类型原样保留，回到新版本时笔记仍在。
- 每个组件的**面板尺寸随组件保存**，重启后恢复；升级时旧的组件级「列数 / 最大
  行数」会**一次性换算**为初始面板尺寸（旧值仍保留在配置文件里但不再使用）；
  **图标大小**继续生效。
- 收纳文件：`%AppData%\Kobold\Storage`
- 文件夹图标：`%AppData%\Kobold\FolderIcons`
- 显式配置备份：`%AppData%\Kobold\backups`（控制面保留最近 20 个）
- 搜索索引：`%LocalAppData%\Kobold\search-index.db`（只存元数据——文件名与位置，
  从不读取内容）。可以安全删除，之后会自动重建。
- 日志：`%LocalAppData%\Kobold\logs\app.log` 与 `search.log`（1 MB 轮转）。

## 配置安全

不要直接编辑运行中的 `%AppData%\Kobold\config.json`：配置由应用自己持有、
校验并保存，手改可能在下次保存时被覆盖。改设置请用设置窗口，或用带 revision
校验的 `koboldctl.ps1` 控制面（非结构性字段用 `config patch`，组件结构用
`widget.*` / `group.*`）。结构不兼容的 `config replace` / `config restore`
会返回 `restartRequired` 且不写盘，需要加 `--restart` 重跑。

源码结构：`Core/`（模型、配置、本地化、设计令牌、主题管理）、`Controls/`（岛与组件窗口）、
`Windows/`（设置）、`Helpers/`（弹窗、菜单、动画、光标）、`Themes/`（XAML 控件样式）、
`Services/`（托盘、Shell 集成、终端、内存回收）、`tests/`（控制台自检）。
