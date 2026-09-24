# Kobold 工程化闭环（Engineering Workflow）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把"手工跑 20 个自检、手工传版本号、手工写 Release 说明"闭环化：一个共享自检入口、版本单一真源、CHANGELOG 驱动的发布说明、LICENSE、最小 CI。

**Architecture:** `tools/Run-Checks.ps1` 作为本地/CI 共用的检查入口（显式矩阵 + 分组 + 汇总 + 非零退出）；`tools/release-notes.ps1` 从 `CHANGELOG.md` 抽取版本小节（缺小节直接失败）；`tools/package.ps1` 默认版本改读 `Kobold.csproj`；新增 `CHANGELOG.md`、`LICENSE`、`THIRD-PARTY-NOTICES.md`；`.github/workflows/checks.yml` 由 HEAD commit 标记 `[ci]` 门控，跑 `-Group ci`。

**Tech Stack:** PowerShell 5.1 兼容（用户默认 shell）、net48 SDK 项目、GitHub Actions windows-latest + .NET 8 SDK。

**Spec:** `docs/paper-todo-learnings.md` 第 4、5 节（PaperTodo 侧参考：`tools/testing/Run-Checks.ps1`、`.github/scripts/release_notes.py`、`release.yml`）。

## Global Constraints

- 脚本必须兼容 **Windows PowerShell 5.1**（不用 `$IsWindows`、不用 `??`、不用 `-Parallel`）。
- 版本号唯一真源 = `Kobold.csproj` 的 `<Version>`；任何脚本都不得再写死版本默认值。
- CHANGELOG 规则（照搬 PaperTodo 的纪律）：只写用户可感知差异；`Unreleased` 只放已完成且将进入下一版的用户变化；纯内部文档/测试/CI/重构不写；同功能增强合并进原条目。
- 失败必须可判定：Run-Checks 与 release-notes 失败时**非零退出**，不把未执行/失败报告为通过。
- 每个任务结束后构建 0 警告 0 错误；提交需用户许可。

## File Structure

| 文件 | 责任 | 动作 |
| --- | --- | --- |
| `tools/Run-Checks.ps1` | 本地/CI 共用检查入口（分组、`-List`、汇总） | 新建 |
| `CHANGELOG.md` | 用户可感知差异的唯一来源（含历史回填） | 新建 |
| `tools/release-notes.ps1` | 从 CHANGELOG + csproj 生成 Release 正文 | 新建 |
| `tools/tests/release-notes.Tests.ps1` | release-notes 的纯逻辑自检（临时目录） | 新建 |
| `LICENSE` | MIT 全文（含 FoldRa 上游版权行） | 新建 |
| `THIRD-PARTY-NOTICES.md` | Hardcodet / Newtonsoft / FoldRa / 光标素材声明 | 新建 |
| `tools/package.ps1` | `-Version` 缺省读 csproj | 修改 |
| `.github/workflows/checks.yml` | `[ci]` 标记门控的 Windows 检查 | 新建 |
| `README.md` | 构建/测试/发布章节改为脚本化流程 | 修改 |

---

### Task 1: `tools/Run-Checks.ps1` 检查入口

**Files:**
- Create: `tools/Run-Checks.ps1`

**Interfaces:**
- Produces: `Run-Checks.ps1 [-Group checks|ci|installer|all] [-Configuration Debug|Release] [-List]`
  - 逐项运行，失败不中断；结尾打印汇总表；任一失败 → `exit 1`；全通过 → `exit 0`。
  - `-List` 只打印矩阵（不要求 dotnet），供文档与排障。

- [ ] **Step 1: 编写脚本**

矩阵说明：`checks` 组 = 全部 20 个 console 自检；`ci` 组 = `checks` 中标记 `Ci = $true` 的保守子集（当前排除需要真实桌面 shell 的 `DesktopIconsCheck` 与离屏渲染依赖较重的 `MenuRenderCheck`，可随 CI 稳定后放宽）；`installer` 组 = `tools\tests\installer.Tests.ps1`。

```powershell
#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('checks', 'ci', 'installer', 'all')] [string]$Group = 'checks',
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$List
)
$ErrorActionPreference = 'Stop'

# 显式矩阵：项目名 = tests\<Name>\<Name>.csproj；Ci 标记决定 ci 组是否包含。
$checks = @(
    @{ Name = 'AtomicFileCheck';       Ci = $true  }   # Plan A 新增
    @{ Name = 'ConfigSafetyCheck';     Ci = $true  }   # Plan A 新增
    @{ Name = 'LangCheck';             Ci = $true  }
    @{ Name = 'WidgetItemsCheck';      Ci = $true  }
    @{ Name = 'ScreenGeometryCheck';   Ci = $true  }
    @{ Name = 'IslandLayoutCheck';     Ci = $true  }
    @{ Name = 'StorageOpsCheck';       Ci = $true  }
    @{ Name = 'UiTokensCheck';         Ci = $true  }
    @{ Name = 'FolderListingCheck';    Ci = $true  }
    @{ Name = 'ShellOpsCheck';         Ci = $true  }
    @{ Name = 'MemoryTrimCheck';       Ci = $true  }
    @{ Name = 'DragOutCheck';          Ci = $true  }
    @{ Name = 'FolderIconCheck';       Ci = $true  }
    @{ Name = 'FolderWatchCheck';      Ci = $true  }
    @{ Name = 'StartupPanelsCheck';    Ci = $true  }
    @{ Name = 'XamlLoadCheck';         Ci = $false }   # 需要真实桌面会话
    @{ Name = 'MenuRenderCheck';       Ci = $false }   # 依赖 WPF 离屏渲染细节
    @{ Name = 'WindowSwitcherCheck';   Ci = $false }   # 依赖真实窗口管理
    @{ Name = 'DesktopIconsCheck';     Ci = $false }   # 依赖 Explorer 桌面图标 ListView
    @{ Name = 'FolderColorCheck';      Ci = $false }   # 端到端写 desktop.ini
    # Plan C/D 落地后在此追加 MonitorPlacementCheck / FullscreenPolicyCheck / HighContrastCheck（Ci = $true）
)

$selected = switch ($Group) {
    'checks'    { $checks }
    'ci'        { $checks | Where-Object { $_.Ci } }
    'installer' { @() }
    'all'       { $checks }
}
if ($List) {
    $selected | ForEach-Object { "{0}  Ci={1}" -f $_.Name, $_.Ci }
    if ($Group -in @('installer', 'all')) { Write-Host 'tools\tests\installer.Tests.ps1' }
    return
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK not found.' }

$results = @()
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    foreach ($check in $selected) {
        $project = Join-Path 'tests' (Join-Path $check.Name "$($check.Name).csproj")
        Write-Host "`n=== $($check.Name) [$Configuration] ==="
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $passed = $true
        try {
            & dotnet run --project $project -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet exited with $LASTEXITCODE" }
        } catch { $passed = $false; Write-Host "FAIL $($check.Name): $($_.Exception.Message)" }
        finally {
            $clock.Stop()
            $results += [pscustomobject]@{ Check = $check.Name; Passed = $passed
                Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1) }
        }
    }
    if ($Group -in @('installer', 'all')) {
        Write-Host "`n=== installer.Tests.ps1 ==="
        $passed = $true
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File 'tools\tests\installer.Tests.ps1'
            if ($LASTEXITCODE -ne 0) { throw "installer tests exited with $LASTEXITCODE" }
        } catch { $passed = $false; Write-Host "FAIL installer: $($_.Exception.Message)" }
        $results += [pscustomobject]@{ Check = 'installer'; Passed = $passed; Seconds = 0 }
    }
} finally { Pop-Location }

$results | Format-Table -AutoSize | Out-Host
$failed = @($results | Where-Object { -not $_.Passed }).Count
if ($failed) { Write-Host "$failed check(s) failed."; exit 1 }
Write-Host "All $($results.Count) check(s) passed."
```

- [ ] **Step 2: 验证 `-List` 与分组**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -List`
Expected: 打印 20 行矩阵（不进 dotnet）。
Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group installer`
Expected: 安装器自检通过，退出码 0。

- [ ] **Step 3: 验证失败可判定（临时把某个项目的期望弄错一次）**

把矩阵中 `LangCheck` 临时改名成不存在的项目运行 `-Group checks`，确认汇总表出现 FAIL 且退出码为 1；随后还原。**还原后必须重跑一次确认全绿。**

---

### Task 2: `CHANGELOG.md`（含历史回填）

**Files:**
- Create: `CHANGELOG.md`

**Interfaces:**
- Produces: 稳定小节标题 `## v<版本>`（可带尾随后缀，如 `## v1.0.0 正式版`）；`Unreleased` / `计划 / 待办` / `评估` 三块按 PaperTodo 纪律维护。

- [ ] **Step 1: 写文件**（内容以此为准，可微调措辞）

```markdown
# Kobold 版本记录

只记录**用户可感知的变化**；内部重构、测试与 CI 不写入。
`Unreleased` 是距上一个正式版的累积；发布时原样成为对应版本小节。

## 计划 / 待办

- 多显示器工作区几何与面板恢复（物理坐标 + 目标屏 DPI）。
- 全屏应用时岛屿自动避让；高对比度主题回退。
- 配置读写安全加固的后续观察（失败副本的自动清理策略）。

## 评估

- PaperTodo 式插件体系、LMDB 图片资产、遥测：与当前规模不匹配，暂不引入。
- 双 CHANGELOG 文件（中/英分离）：维护成本高于收益，保持单文件。

## Unreleased

### 修复

- 配置损坏时不再静默重置：读不出的文件会先保留为 `config.json.failed-<时间戳>`，
  再退回默认设置（此前会直接覆盖，且备份也可能被污染）。
- 配置写入改为原子替换：写入中断不再留下半截 JSON；保存失败会通过托盘提示一次。
- Windows 注销/关机时同步保存最终设置（此前只覆盖托盘退出路径）。

## v1.2.0 (2026-09-23)

- 文件夹颜色：右键文件夹条目可换 9 种颜色，图标在资源管理器等处全局生效，可恢复默认。
- 颜色菜单显示本地化的颜色名。
- 退出时开着的面板会在下次启动时恢复，且不抢焦点。
- 岛与面板不再出现在 Alt+Tab / 任务视图中。

## v1.1.0 (2026-09-23)

- 面板内浏览文件夹：下钻、返回（按钮/Backspace）、整条路径菜单。
- 面板内文件操作：新建、重命名、删除（回收站）、当前目录打开终端、完整系统右键菜单（含「显示更多选项」）。
- 选中条目按空格用 QuickLook 预览；面板尺寸按内容精确计算。
- 浏览目录时列表实时跟随文件夹变化。
- 拖拽完善：拖出面板交给系统移动文件；两个面板之间可直接移动条目。
- 空闲两分钟无操作后把工作集交还系统（任务管理器内存降到个位数 MB）。
- 修复：第二个实例启动不再影响正在运行实例的状态。

## v1.0.1 (2026-09-14)

- 面板靠屏幕边缘时，右键菜单自动调整位置保持可见。
- README 更新为实际截图。

## v1.0.0 (2026-09-11)

首个正式版：

- 顶部居中的灵动岛启动器：悬停展开桌面入口 / 组件 tile / 新建 / 设置，可拖动。
- 可拖动的文件夹面板：记忆位置、锁定、置顶、重命名、网格列数与条目大小。
- 引用 / 收纳 / 移出三种文件语义，失效引用有角标提示。
- 设计令牌主题（深/浅色），菜单与 Tooltip 跟随主题与组件颜色。
- 三语界面（English / 简体中文 / 日本語）。
- FoldRa 配置与存储的一次性迁移工具；面板透明度设置；单实例与开机自启。
```

- [ ] **Step 2: 验证小节可被脚本定位**

Run: `findstr /R /C:"^## v1.2.0" CHANGELOG.md`
Expected: 匹配到一行。

---

### Task 3: `tools/release-notes.ps1` + 自检（TDD）

**Files:**
- Create: `tools/tests/release-notes.Tests.ps1`（先写）
- Create: `tools/release-notes.ps1`

**Interfaces:**
- Produces: `release-notes.ps1 [-Version <x.y.z>] [-Changelog CHANGELOG.md] [-OutFile <path>]`
  - `-Version` 省略时读 `Kobold.csproj` 的 `<Version>`；
  - 抽取 `## v<Version>`（行尾允许后缀）到下一个 `## ` 之间的正文；缺失/空 → `exit 1`；
  - 追加 `## Downloads / 下载` 小节（zip 资产链接，指向 `releases/download/v<Version>/...`）；
  - `-OutFile` 省略时写 stdout。

- [ ] **Step 1: 写失败的自检脚本**

`tools/tests/release-notes.Tests.ps1` 结构照 `tools/tests/installer.Tests.ps1`（分区 + `Check`/`Check-Throws` + 结尾 `passed/failed` + `exit 1`）。全部在 `%TEMP%\kobold-release-notes-test-<guid>` 内进行，调用真实脚本：

```powershell
$script = Join-Path (Split-Path $PSScriptRoot -Parent) 'release-notes.ps1'
$tmp = Join-Path $env:TEMP ("kobold-release-notes-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$changelog = Join-Path $tmp 'CHANGELOG.md'
@'
# Log

## Unreleased

- work in progress

## v1.2.0 (2026-09-23)

- folder colors
- quieter windows

## v1.20.0 (2027-01-01)

- future release
'@ | Set-Content -LiteralPath $changelog -Encoding UTF8

function Run-Notes([string[]]$Arguments) {
    $out = Join-Path $tmp 'notes.md'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script @Arguments -OutFile $out | Out-Null
    return @{ Code = $LASTEXITCODE; Text = (Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue) }
}

# 1. 精确抽取 v1.2.0，不吞掉 v1.20.0 的内容
$r = Run-Notes @('-Version', '1.2.0', '-Changelog', $changelog)
Check($r.Code -eq 0 -and $r.Text -match 'folder colors' -and $r.Text -notmatch 'future release',
    'extracts the exact version section (v1.2.0 must not swallow v1.20.0)')

# 2. 追加下载链接
Check($r.Text -match 'releases/download/v1.2.0/Kobold-v1.2.0-win-x64.zip',
    'appends the download link')

# 3. 缺小节 → 非零退出
$r = Run-Notes @('-Version', '9.9.9', '-Changelog', $changelog)
Check($r.Code -ne 0, 'missing version section fails')

# 4. 空小节 → 非零退出
@'
# Log

## v0.0.1

## v1.0.0

- real
'@ | Set-Content -LiteralPath $changelog -Encoding UTF8
$r = Run-Notes @('-Version', '0.0.1', '-Changelog', $changelog)
Check($r.Code -ne 0, 'empty version section fails')

# 5. 省略 -Version 时读取 csproj 的 <Version>
$csproj = Join-Path $tmp 'Kobold.csproj'
'<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>' |
    Set-Content -LiteralPath $csproj -Encoding UTF8
# 脚本用 -Csproj 参数指向测试文件（实现接口的一部分）
$r = Run-Notes @('-Csproj', $csproj, '-Changelog', $changelog)
Check($r.Code -ne 0 -or $r.Text -match 'real', 'reads the version from the csproj when -Version is omitted')
```

> 第 5 条为接口的一部分：`release-notes.ps1` 增加 `-Csproj`（默认 `Kobold.csproj`），便于自检不依赖仓库状态。

- [ ] **Step 2: 运行并确认失败**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\release-notes.Tests.ps1`
Expected: 失败（脚本不存在），退出码 1。

- [ ] **Step 3: 实现 `tools/release-notes.ps1`**

要点（完整实现按此逐行落地）：

```powershell
param(
    [string]$Version,
    [string]$Csproj = 'Kobold.csproj',
    [string]$Changelog = 'CHANGELOG.md',
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath $Csproj
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (-not $Version) { Write-Error "No <Version> found in $Csproj"; exit 1 }
}
$lines = Get-Content -LiteralPath $Changelog
$escaped = [regex]::Escape($Version)
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^##\s+v$escaped(\s|$)") { $start = $i; break }
}
if ($start -lt 0) { Write-Error "CHANGELOG: no section '## v$Version'"; exit 1 }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { $end = $i; break }
}
$body = @($lines[($start + 1)..($end - 1)] | Where-Object { $_ -notmatch '^\s*---\s*$' })
while ($body.Count -gt 0 -and $body[0].Trim() -eq '') { $body = $body[1..($body.Count - 1)] }
while ($body.Count -gt 0 -and $body[-1].Trim() -eq '') { $body = $body[0..($body.Count - 2)] }
if ($body.Count -eq 0) { Write-Error "CHANGELOG section 'v$Version' is empty"; exit 1 }

$notes = @"
$($body -join "`n")

## Downloads / 下载

- [Kobold-v$Version-win-x64.zip](https://github.com/zzrqq-worklux/Kobold/releases/download/v$Version/Kobold-v$Version-win-x64.zip)
"@
if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $notes -Encoding UTF8 }
else { Write-Output $notes }
```

- [ ] **Step 4: 运行并确认通过**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\release-notes.Tests.ps1` → `exit 0`。
Run（真实数据）: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-notes.ps1 -Version 1.2.0` → 输出 v1.2.0 小节 + 下载链接。

---

### Task 4: `LICENSE` + `THIRD-PARTY-NOTICES.md`

**Files:**
- Create: `LICENSE`、`THIRD-PARTY-NOTICES.md`

- [ ] **Step 1: LICENSE（MIT，含上游）**

标准 MIT 全文，版权行两段：

```text
MIT License

Copyright (c) 2026 zzrqq-worklux (Kobold)
Copyright (c) 2025 Yusuf Eren Seyrek and Mehmet Delin (FoldRa)

Permission is hereby granted, free of charge, ...
```

- [ ] **Step 2: THIRD-PARTY-NOTICES.md**

列出四段：FoldRa（MIT，上游基础）、Hardcodet.NotifyIcon.Wpf（MIT）、Newtonsoft.Json（MIT）、光标素材（见 `Resources/cursors.LICENSE.txt`）。每段给出项目名、用途、许可名与获取地址。

- [ ] **Step 3: README 链接**

`README.md` 的 Credits & License 小节加 `[LICENSE](LICENSE)` 与 `[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)` 链接（中英两处）。

---

### Task 5: `tools/package.ps1` 版本接线 + README 流程更新

**Files:**
- Modify: `tools/package.ps1:1-5`
- Modify: `README.md`（Build & test / 打包章节，中英两处）

**Interfaces:**
- Produces: `package.ps1 [-Version <x.y.z>]`；省略时读 `Kobold.csproj`；zip 名仍为 `Kobold-v<version>-win-x64.zip`。

- [ ] **Step 1: 修改脚本**

```powershell
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 [-Version 1.2.0]
# Version defaults to the <Version> in Kobold.csproj - one source of truth.
param(
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'Kobold.csproj')
    $Version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No <Version> in Kobold.csproj; pass -Version explicitly.' }
}
```

（其余步骤沿用现有实现，仅删除旧默认值 `'1.0.0'`。）

- [ ] **Step 2: 验证**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1`
Expected: `Packaged: ...\Releases\Kobold-v1.2.0-win-x64.zip`（读 csproj）。
清理：删除本次生成的 zip（它属于发布产物，不进工作区变更）。

- [ ] **Step 3: README 更新**

- Build & test 章节：命令清单替换为
  `powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group all`（分组表：`checks`/`ci`/`installer`）；
- 打包章节：示例去掉手工 `-Version`（说明默认读 csproj）；
- 新增发布步骤说明：`release-notes.ps1 -Version <x>` 生成 Release 正文。
- 中文说明同步。

---

### Task 6: `.github/workflows/checks.yml`（marker 门控）

**Files:**
- Create: `.github/workflows/checks.yml`

- [ ] **Step 1: 写 workflow**

```yaml
name: Checks

on:
  push:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  checks:
    name: Build and check (Windows)
    # HEAD commit 含 [ci] 才跑，避免每次 push 都消耗 runner；手动触发不受限。
    if: >-
      github.event_name == 'workflow_dispatch' ||
      contains(github.event.head_commit.message, '[ci]')
    runs-on: windows-latest
    timeout-minutes: 30
    concurrency:
      group: checks-${{ github.ref }}
      cancel-in-progress: true
    defaults:
      run:
        shell: pwsh
    env:
      DOTNET_CLI_TELEMETRY_OPTOUT: "1"
      DOTNET_NOLOGO: "true"
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"
      - name: Build (Release)
        run: dotnet build Kobold.csproj -c Release --nologo
      - name: Run checks
        run: ./tools/Run-Checks.ps1 -Group ci -Configuration Release
```

- [ ] **Step 2: 验证 YAML 语法**

Run: `python -c "import yaml,io; yaml.safe_load(io.open(r'.github/workflows/checks.yml', encoding='utf-8'))"`（若无 PyYAML，`python -m pip install pyyaml --quiet` 后重试；仍不可用时人工核对缩进与键名）。
Expected: 无异常输出。

- [ ] **Step 3: 本地等价验证**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group ci -Configuration Release`
Expected: `ci` 组全绿（window/桌面依赖项目不在此组）。

---

## Self-Review

- **Spec 覆盖**：run-all=Task 1；版本真源=Task 5；CHANGELOG=Task 2；发布说明脚本=Task 3；LICENSE=Task 4；CI=Task 6；README 同步=Task 5/4。
- **风险**：`ci` 组在 GitHub runner 上的 WPF 可用性未实测（保守子集）；首次推 `[ci]` 后观察，必要时把项目降级为本地组。
- **回滚**：全部为新增文件 + 两处小改（package.ps1、README）。
- **明确不做**：Debug 测试包、发布 workflow、Pages、issue 模板（按需再加）。

---

## 执行记录（2026-09-24）

**状态：全部 6 个任务完成并验证。**

- 交付：`tools/Run-Checks.ps1`（20 项矩阵 + `ci` 子集 + installer 组，`-List`）、`CHANGELOG.md`（回填 v1.0.0–v1.2.0 + Unreleased）、`tools/release-notes.ps1` + `tools/tests/release-notes.Tests.ps1`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`.github/workflows/checks.yml`、`tools/package.ps1`（默认读 csproj）、README（构建/测试/发布章节 + 许可链接）。
- 验证：`Run-Checks -Group checks` 20/20、`-Group ci -Configuration Release` 15/15、installer 自检 60/60、release-notes 自检 11/11、`package.ps1` 生成 `Kobold-v1.2.0-win-x64.zip`（验证后已删除）。
- **环境适配（本机安全软件限制，已记录，CI 不受影响）**：
  1. `release-notes.Tests.ps1` 改为**同进程**调用被测脚本（子进程 `powershell.exe` 被拦截）；`release-notes.ps1` 失败统一用 `throw`（`-File` 运行仍退出码 1）。
  2. PS 5.1 按 ANSI 解析无 BOM 文件：`release-notes.ps1` 与 `release-notes.Tests.ps1` 以 **UTF-8 带 BOM** 保存；脚本内读 CHANGELOG 显式 `-Encoding UTF8`，notes 以无 BOM UTF-8 写出（TDD 抓到过乱码 bug）。
  3. `installer.Tests.ps1` sandbox 改为探测式：`%TEMP%` 不可写时回退到 `tools/tests/.sandbox/`（已 gitignore）。原因：本机安全软件拒绝在用户 TEMP/AppData 写 `<exe>.config`。
  4. `checks.yml` 无法离线用 YAML 解析器校验（python/node 子进程同样被拦截），已做缩进/键名文本级检查；首次推送后由 GitHub 校验。
- 待人工：首次推送含 `[ci]` 的提交后确认 workflow 在 runner 上跑通。
