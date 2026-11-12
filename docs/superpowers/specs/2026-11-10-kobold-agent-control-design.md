# Kobold Agent Control Plane + `kobold-config` Skill — Design

> **状态**：草案（待用户评审）。本 spec 只定义 v2.0 前的 agent 配置/诊断能力，不包含实现。
> **基线**：`main` @ `aa1efbc`（工作区含未提交的 P2 改动；本 spec 不依赖这些改动）。
> **关系**：新增本地控制面与 agent skill；不改变现有 UI 行为、config 格式或服务语义。
> **代码走查**：`App.xaml.cs`、`Core/AppConfig.cs`、`Core/WidgetManager.cs`、
> `Core/WidgetData.cs`、`Core/WidgetGroup.cs`、`Core/FileSearch/FileSearchIndexService.cs`、
> `Core/FileSearch/SearchDiagnostics.cs`、`Core/FolderWatcher.cs`、`Services/FullscreenWatcher.cs`、
> `Services/MemoryTrimmer.cs`、`tools/install.ps1`、`tools/installer-common.ps1`、`tools/Run-Checks.ps1`。

## 0. 决策记录（2026-11-10 用户确认）

1. **范围**：配置 + 搜索服务 + 诊断；第一版覆盖 `config`、`widget`、`group`、`search`、`diag`。
2. **目标用户**：终端用户 + 已安装的 Kobold，不要求 .NET SDK 或源码。
3. **结构性配置**：使用高层 `widget.*` / `group.*` 命令，agent 不直接拼 `Widgets`/`Groups` 数组。
4. **传输**：当前用户专属命名管道 + `koboldctl.ps1`，不做 HTTP、不做远程访问。
5. **配置安全**：agent 禁止直接编辑运行中的 `config.json`；所有写操作走控制面，带 revision、备份和 dry-run。
6. **结构变更**：高层命令走现有 `WidgetManager` in-place API，直接生效；整份 `config.replace` / `config.restore` 的结构变更要求重启。
7. **不做**：v1 不做 widget/group 任意 JSON 热重载、不做 folder watcher/fullscreen/memory 的变更命令、不做 HTTP API、不做任意 shell/文件写入。
8. **提交**：按工作区规则，未获用户明确要求不提交 git；本 spec 先落盘供评审。
9. **Skill 分发**：不做特定 agent 的路径适配；`skill/kobold-config/` 自带 `SKILL.md` + `README.md`，告诉用户/agent 把这个目录放进自己的 skills 目录即可。不写安装脚本，不猜特定 agent 的路径。
10. **设置窗口**：不提供 `app.showSettings` 控制命令（选项 C）；设置窗口只由用户从 UI 打开。
11. **重启编排**：`config.replace/restore --restart` 使用应用内 restart helper（选项 B）：应用写盘后启动一个等待父进程退出的 helper，helper 再启动新实例；CLI 等待新管道可用。helper 失败时 skill 提示用户手动打开。
12. **系统级设置**：`StartWithWindows` / `PinStorageToQuickAccess` 立即生效（选项 B）；抽成 `StartupRegistryService` / `QuickAccessService`，控制面写入 config 后马上应用，失败返回 warning，不要求重启。
13. **桌面图标保护**：程序退出/重启/系统注销时，如果本次运行处于“隐藏桌面图标”状态，立即显示桌面图标（不保留隐藏状态）；下一次启动按 `HideDesktopIcons` 再自动隐藏。不能让用户退出后面对一个“空桌面”。

## 1. 目标与成功标准

| # | 目标 | 验收口径 |
|---|---|---|
| 1 | agent 能安全读配置 | `koboldctl config get --summary` 返回设置 + widget/group 摘要 + revision；默认不读 note/todo 正文 |
| 2 | agent 能安全改设置 | `config.patch` 支持 theme/language/island/icon/item scale/file search 等；`--dry-run` 不写盘；`baseRevision` 冲突拒绝 |
| 3 | agent 能改 widget/group 结构 | `widget.add/update/delete`、`group.merge/detach/reorder/dissolve` 直接生效，不需要重启，不重建无关视图 |
| 4 | agent 能调试搜索服务 | `search status/query/rebuild/cancel/pause/resume/reconcile` + `diag logs --name search` 可用 |
| 5 | agent 能看整体状态 | `diag snapshot` 返回 app/config/island/search/memory/fullscreen/folders/logs 快照 |
| 6 | 不破坏现有行为 | 现有 34 项 checks 全绿；新增 `ControlCheck` 进 CI；0 警告 |
| 7 | 安全边界 | 管道 ACL 仅当前用户/SYSTEM；方法白名单；无网络监听；破坏性操作必须 `confirm` |
| 8 | 终端用户可用 | 安装器带 `koboldctl.ps1`；skill 可从安装目录工作，无需源码/SDK |
| 9 | 退出不吓人 | 退出/重启/注销时恢复桌面图标原状态；下次启动按配置自动隐藏；强制杀进程是已知边界 |
| 10 | 系统设置即时生效 | `StartWithWindows` / `PinStorageToQuickAccess` 写入后立即应用，失败返回 warning，不要求重启 |

## 2. 非目标（v1 不做）

- 不做远程/HTTP/WebSocket API；不监听 TCP 端口。
- 不做任意 shell、任意路径读写、任意进程启动。
- 不做 `Widgets`/`Groups` 的任意 JSON 热重载；高层命令覆盖常用结构操作，整份替换要求重启。
- 不做 folder watcher / fullscreen / memory trim 的变更命令；只做只读状态。
- 不做 note/todo 正文的默认读取；`config.get --full` 必须显式请求。
- 不做自动执行；skill 每次写入前必须让用户确认。
- 不做多用户/多会话共享；管道按当前用户 + 会话隔离。
- 不做 UI 设置页里的控制开关；v1 默认启用，靠 ACL 限制（后续可加 `ControlEnabled`）。

## 3. 总体架构

```text
Agent skill (kobold-config)
  └─ koboldctl.ps1
       └─ \\.\pipe\Kobold.Control.v1.<userSid>.<sessionId>
            └─ Kobold.exe ControlServer (background thread)
                 ├─ ConfigControlService
                 ├─ WidgetGroupControlService
                 ├─ DiagnosticsService
                 └─ FileSearchIndexService
```

### 3.1 进程内组件

| 组件 | 职责 | 位置建议 |
|---|---|---|
| `ControlServer` | 管道生命周期、ACL、单请求读取、UI Dispatcher 调度、响应/错误、审计日志 | `Services/Control/ControlServer.cs` |
| `ControlProtocol` | 请求/响应 DTO、版本、错误码、JSON 设置、管道名计算 | `Core/Control/ControlProtocol.cs` |
| `ConfigControlService` | `config.*`：get/describe/validate/patch/replace/backup/restore | `Core/Control/ConfigControlService.cs` |
| `WidgetGroupControlService` | `widget.*` / `group.*`：调用 `WidgetManager` 现有 API | `Core/Control/WidgetGroupControlService.cs` |
| `DiagnosticsService` | `diag.*`：快照、日志 tail、folder/fullscreen/memory 只读状态 | `Core/Control/DiagnosticsService.cs` |
| `AppDiagnostics` | 轮转 app 日志，控制面审计 | `Core/AppDiagnostics.cs` |
| `koboldctl.ps1` | CLI 客户端、JSON 输出、dry-run/confirm/restart 编排 | `tools/koboldctl.ps1` |
| `kobold-config` skill | agent 工作流、安全规则、示例 | `skill/kobold-config/` |

### 3.2 生命周期

- `App.OnStartup` 在 `WidgetManager.Instance.Initialize()` 之后启动 `ControlServer`；`OnExit` 先停止 server，再走现有退出路径。
- 控制请求全部通过 `Application.Current.Dispatcher.Invoke` 在 UI 线程执行，避免 WPF 线程亲和问题。
- 一个连接只处理一个请求：读一行 JSON → 执行 → 写一行 JSON → 关闭。CLI 每次命令新建连接，简单且无长连接状态。
- 管道名：`Kobold.Control.v1.<userSid>.<sessionId>`；SID 来自 `WindowsIdentity.GetCurrent().User.Value`，session 来自 `Process.GetCurrentProcess().SessionId`。
- ACL：`PipeSecurity` 只允许当前用户 SID（FullControl）和 SYSTEM（FullControl）；不使用 `Everyone` / `Authenticated Users`。
- 超时：读 10 秒、执行 30 秒（`search.rebuild` 只排队，不等待扫描完成）；消息上限 1 MB。

### 3.3 退出保护与 restart helper

**桌面图标保护**（决策 13）：

- 启动时 `ApplyDesktopIconsOnStartup` 记录 `HideIconsOriginal`；`HideDesktopIcons` 为 true 时隐藏图标。
- 正常退出、`SessionEnding`、restart helper 路径、`ProcessExit` / `DispatcherUnhandledException`（best-effort）都调用统一的 `DesktopIconGuard.RestoreOnExit()`：
  - 如果本次运行隐藏了桌面图标（`HideDesktopIcons == true`），立即显示桌面图标；
  - 不恢复“原始隐藏”状态；这是有意的安全行为，避免用户退出后面对空桌面；
  - 幂等，重复调用不改变结果。
- 下一次启动仍按 `HideDesktopIcons` 自动隐藏；用户不会因为程序退出而面对一个空桌面。
- 强制杀进程（安装器/任务管理器）无法执行 `OnExit`，这是已知边界；文档提示用户重新启动 Kobold 或手动显示图标。若以后要求“强杀也保证恢复”，需要常驻 watchdog 进程，v1 不做。

**restart helper**（决策 11）：

- `config.replace/restore --restart` 写盘成功后，应用启动一个 detached helper（同一 `Kobold.exe` 的 `--restart-helper <parentPid> <exePath>` 模式，不创建窗口、不抢单实例 mutex），然后 `Application.Current.Shutdown()`。
- helper 轮询父进程退出，退出后 `Start-Process` 启动正常实例；helper 自身写 `app.log`，失败时退出码非 0。
- helper 启动前先执行 `DesktopIconGuard.RestoreOnExit()`，避免重启窗口期桌面图标处于隐藏状态。
- CLI 只轮询新管道；不自己启动进程。helper 失败时 CLI 返回 `restart_failed`，skill 提示用户手动打开。

## 4. 协议

### 4.1 请求 / 响应

```json
// request
{
  "v": 1,
  "id": "01J...",
  "method": "config.patch",
  "params": { "patch": { "Theme": "light" }, "dryRun": true },
  "baseRevision": "sha256:..."
}

// success
{
  "v": 1,
  "id": "01J...",
  "ok": true,
  "result": { "wouldChange": true, "restartRequired": false, "changes": ["Theme"] },
  "revision": "sha256:..."
}

// error
{
  "v": 1,
  "id": "01J...",
  "ok": false,
  "error": {
    "code": "conflict",
    "message": "Config changed since the supplied revision.",
    "details": { "currentRevision": "sha256:..." }
  }
}
```

### 4.2 错误码

| code | 含义 |
|---|---|
| `invalid_request` | JSON 格式/字段类型错误 |
| `unsupported_protocol` | `v` 不匹配 |
| `unknown_method` | 方法不在白名单 |
| `validation_failed` | config 校验失败；`details.errors` 给出字段/原因 |
| `conflict` | `baseRevision` 与当前 revision 不一致 |
| `revision_required` | 写操作缺少 `baseRevision` |
| `restart_required` | 结构变更需要重启；`details.restartRequired = true` |
| `app_not_running` | CLI 连不上管道（应用未启动） |
| `control_unavailable` | 应用版本不支持控制面 |
| `forbidden` | 方法/路径/参数被安全策略拒绝 |
| `internal` | 未预期异常；日志有 correlation id |

### 4.3 revision

- revision = 当前内存 config 的规范化 JSON 的 SHA-256（UTF-8，固定序列化设置）。
- `config.get` 返回 revision；所有写操作必须带 `baseRevision`。
- 写操作在 UI 线程先比较 revision，再执行；不一致直接 `conflict`，不写盘。
- 写成功后返回新 revision；`widget.*` / `group.*` / `search.*` 也返回 revision（search 不改变 config 时返回当前值）。
- 应用内正常用户操作（改主题、拖面板等）也会改变 revision；agent 必须重新读取后重试。

## 5. 配置接口

### 5.1 `config.get`

参数：

```json
{ "view": "summary" }   // summary | full，默认 summary
```

`summary` 返回：

- 顶层设置（Language、Theme、PanelOpacity、IslandX、IslandCollapseDelay、DefaultGridColumns、IconStyle、ItemScale、StartWithWindows、HideDesktopSourceOnStore、HideDesktopIcons、PinStorageToQuickAccess、SchemaVersion）。`HideIconsOriginal` 是内部记录，summary 不返回。
- `FileSearch` 设置（不含用户文件内容）。
- `Widgets` 摘要：`Id`、`Kind`、`Name`、`Color`、`PosX/PosY`、`PanelX/Y/Width/Height/DpiScale`、`IsExpanded/IsLocked/IsPanelPinned`、`GroupId/GroupOrder`。**不含** `Items`、`Notes`、`BrowseStack`。
- `Groups` 摘要：`Id`、`ActiveWidgetId`、几何、`IsExpanded/IsLocked/IsPanelPinned/IslandExpanded`、成员 id 顺序。
- `path`、`schemaVersion`、`revision`。

`full` 返回完整 `AppConfig` JSON（包含 note/todo/folder 数据）；skill 必须显式征得用户同意后才能使用。

### 5.2 `config.describe`

返回反射生成的字段/类型元数据：

```json
{
  "schemaVersion": 1,
  "topLevel": [
    { "name": "Theme", "type": "string", "allowed": ["dark", "light", "high contrast"] },
    { "name": "PanelOpacity", "type": "number", "range": [0.2, 1.0] }
  ],
  "widgetKinds": [
    { "id": "folder", "fields": ["GridColumns", "BrowseStack", "Items"] },
    { "id": "todo", "fields": ["Items", "SelectedDate", "HolidayRegion", "CalendarCellHeight"] }
  ],
  "groups": ["Id", "ActiveWidgetId", "PanelX", "PanelY", "PanelWidth", "PanelHeight", "IsExpanded", "IsLocked", "IsPanelPinned", "IslandExpanded"],
  "fileSearch": ["Enabled", "Configured", "Roots", "IndexAllFiles", "Types", "IndexFolders", "IndexExtensionless", "ExcludeDirectories", "ReconcileMinutes"]
}
```

- 用反射从 `AppConfig`、`WidgetData` 子类、`WidgetGroup`、`FileSearchSettings` 生成；新增字段自动出现。
- 不返回用户数据；只返回字段名/类型/枚举/范围。
- 新增 `ControlCheck` 断言每个 `AppConfig` 公共属性、每个注册 kind 都出现在 describe 结果里，防止 schema 漂移。

### 5.3 `config.validate`

参数：

```json
{ "document": { ... } }   // 或 { "patch": { ... } }
```

- 解析到 `AppConfig` 的临时副本，执行现有 `RepairGroups` / `FileSearchSettings.Normalize` / 类型转换；不写盘、不改内存。
- 返回 `{ "ok": true, "warnings": [...], "normalizedSummary": {...} }` 或 `validation_failed` + `details.errors`。
- 未知 widget kind 保留为 `UnknownData`，不报错；缺失/重复 Id、无效 group membership 给出 warning，并按现有 repair 规则修复。

### 5.4 `config.patch`

- JSON Merge Patch（RFC 7386）语义；只允许顶层**非结构**字段：
  `Language`、`Theme`、`PanelOpacity`、`IslandX`、`IslandCollapseDelay`、`DefaultGridColumns`、`IconStyle`、`ItemScale`、`StartWithWindows`、`HideDesktopSourceOnStore`、`HideDesktopIcons`、`PinStorageToQuickAccess`、`FileSearch`。
- 出现 `Widgets` / `Groups` / `Folders` / `FrequentFiles` / `SchemaVersion` → `forbidden`，提示改用 `widget.*` / `group.*` / `config.replace`。
- `FileSearch` 允许对象合并；校验后调用 `FileSearchIndexService.ApplySettings`。
- 返回 `{ wouldChange, changes: ["Theme", ...], restartRequired, revision }`。
- `dryRun: true` 只返回 changes/restartRequired，不写盘、不应用。

**热应用**：Language、Theme、PanelOpacity、IslandX、IslandCollapseDelay、IconStyle、ItemScale、FileSearch、DefaultGridColumns（影响后续新建）、HideDesktopIcons、StartWithWindows、PinStorageToQuickAccess。

- `HideDesktopIcons` 立即调用 `WidgetManager.SetHideDesktopIcons`；退出保护见 3.3。
- `StartWithWindows` 立即调用新的 `StartupRegistryService.Apply(bool)`（从 `App.SyncStartupRegistry` 抽出）；注册表写入失败返回 `warnings`，config 仍保存，下一次启动重试。
- `PinStorageToQuickAccess` 立即调用新的 `QuickAccessService.Apply(bool)`（包装现有 `QuickAccess.Pin/Unpin/TryIsPinned` 和轮询）；Explorer 拒绝时返回 `warnings`，config 仍保存。
- 这三个字段都不再返回 `restartRequired`。

### 5.5 `config.replace`

- 接受完整 `AppConfig` JSON；`config.validate` 同样校验/修复。
- 若 `Widgets`/`Groups`/`Folders`/`FrequentFiles` 与当前不同 → 结构变更，返回 `restartRequired`；只有 `restart: true` 才写盘并启动应用内 restart helper（见 3.3），helper 等父进程退出后启动新实例。
- 若只有热字段变化 → 与 `config.patch` 相同的热应用路径。
- 写盘前自动备份当前 config 到 `backups/`。

### 5.6 `config.backup` / `config.restore`

- `config.backup { label?: "before-agent" }` → 写 `%AppData%\Kobold\backups\config-YYYYMMDD-HHmmss-<label>.json`，返回路径；保留最近 20 个，自动清理最旧。
- `config.restore { backup: "<path>" }` 只允许 `backups/` 目录下的文件；校验后按 `config.replace` 路径应用；结构变更要求 `restart: true`。
- 现有 `config.json.backup` 保持不变；显式备份是额外的可读快照。

## 6. Widget / Group 高层命令

所有命令在 UI 线程执行，调用现有 `WidgetManager` API；成功后 `SaveConfig()`，返回新 revision。除 `widget.delete` / `group.dissolve` 外不要求 `confirm`，但 CLI/skill 仍会在写入前询问用户。

### 6.1 `widget.add`

```json
{ "kind": "note", "name": "会议记录", "color": "#788C5D", "open": false, "pinned": false }
```

- 新增 `WidgetManager.CreateWidgetForControl(kind, ...)`（或给 `CreateWidget` 加 `open` 参数）：不打开面板、不抢焦点、默认不 pinned。
- 位置默认与现有 `CreateWidget` 相同的屏幕中央错位；可传 `posX/posY`。
- 返回 `{ "id": "...", "kind": "note", "revision": "..." }`。
- 未知 kind → `validation_failed`。

### 6.2 `widget.update`

```json
{
  "id": "widget-id",
  "name": "新名字",
  "color": "#6A9BCC",
  "locked": false,
  "pinned": true,
  "posX": 100,
  "posY": 100,
  "panelWidth": 600,
  "panelHeight": 480,
  "folder": { "gridColumns": 4 },
  "todo": { "holidayRegion": "cn" },
  "note": { "fontScale": 1.0 }
}
```

- 只改传入字段；类型/范围校验。
- 调用 host.UpdateUI；grouped member 的 pin 变化后重新派生 `group.IsPanelPinned`。
- 不直接改 `GroupId/GroupOrder/IsExpanded`；结构用 group 命令。
- 返回 `{ "changed": ["Name", ...], "revision": "..." }`。

### 6.3 `widget.delete`

```json
{ "id": "widget-id", "confirm": true }
```

- 调用 `WidgetManager.RemoveWidget(id)`；沿用现有 folder eject-all、group 自动解散/活跃 tab 回落逻辑。
- 缺 `confirm` → `forbidden`。

### 6.4 `group.merge`

```json
{ "targetId": "target-widget-or-group", "memberIds": ["a", "b"] }
```

- `targetId` 是 standalone widget 或已有 group；`memberIds` 必须是 standalone widget（不支持 group 作为 source，现有 manager 也不支持）。
- 调用 `WidgetManager.MergeWidgets`；多个 member 按顺序合并；返回 `{ "groupId": "...", "revision": "..." }`。
- 目标/来源不存在、已 locked、group source → `validation_failed`。

### 6.5 `group.detach`

```json
{ "groupId": "...", "widgetId": "..." }
```

- 调用 `WidgetManager.DetachWidget`；保留 live view；最后 pair 自动解散。
- 返回 `{ "standaloneId": "...", "dissolved": true|false, "revision": "..." }`。

### 6.6 `group.reorder`

```json
{ "groupId": "...", "from": 1, "to": 0 }
```

- 调用 `WidgetManager.ReorderGroupMember`；locked/越界拒绝。
- 返回 `{ "revision": "..." }`。

### 6.7 `group.dissolve`

```json
{ "groupId": "...", "confirm": true }
```

- 依次 detach 除最后一个成员外的所有成员；最后 pair 自动解散。
- 缺 `confirm` → `forbidden`；返回每个成员的 standalone id。

## 7. 诊断与搜索

### 7.1 `diag.snapshot`

返回（字段可增，不可删）：

```json
{
  "app": { "version": "1.4.0", "pid": 1234, "startedUtc": "...", "installPath": "..." },
  "config": { "path": "...", "backupPath": "...", "schemaVersion": 1, "revision": "sha256:...", "widgetCount": 4, "groupCount": 1 },
  "island": { "visible": true, "expanded": false },
  "search": {
    "state": "Ready", "message": "", "filesIndexed": 120000, "directoriesScanned": 5000,
    "skippedDirectories": 12, "incrementalAvailable": true, "scanCancelled": false,
    "needsRebuild": false, "databasePath": "...", "reconcileMinutes": 30,
    "index": { "hasIndex": true, "lastScanUtc": "...", "lastReconcileUtc": "...", "schemaVersion": 1 }
  },
  "memory": { "workingSetBytes": 0, "privateBytes": 0, "lastTrimUtc": null, "trimmedThisIdlePeriod": false },
  "fullscreen": { "avoiding": false },
  "folders": [
    { "widgetId": "...", "name": "...", "browsePath": "...", "watcherActive": true, "itemCount": 12, "active": true }
  ],
  "logs": { "app": "...", "search": "..." }
}
```

实现补充：

- `FullscreenWatcher` 增加只读 `IsAvoiding`；`WidgetManager` 保存 watcher 引用。
- `WidgetManager` 记录 last trim UTC / result；`IdleTrimPolicy` 增加只读 `TrimmedThisIdlePeriod`。
- `FolderPanelView` 增加 `FolderDiagnosticsSnapshot()`（browse path、watcher active、item count、active）；`WidgetManager` 遍历 standalone/group views。
- `IndexStatus` 直接来自 `FileSearchIndexService.ReadStatus()`。

### 7.2 `diag.logs.tail`

```json
{ "name": "app" | "search", "lines": 200 }
```

- 只允许 `app` / `search`（以及轮转 `.1`）；路径固定，不接受任意路径。
- 用 `FileShare.ReadWrite` 读，最多 1000 行；返回 `{ "path": "...", "lines": [...] }`。

### 7.3 `diag.search.status` / `diag.search.query`

- `status` 返回 `FileSearchStatus` + `IndexStatus` + `ReconcileIntervalMinutes`。
- `query { "text": "...", "limit": 50 }` 调用 `FileSearchIndexService.Search`，返回 `{ totalMatches, limitReached, items: [{path,name,isDirectory,extension,sizeBytes,modifiedUtc}] }`。
- `limit` 默认 50，最大 200；空 query 返回空结果，不触发全量扫描。

### 7.4 `search.*`

| 方法 | 映射 |
|---|---|
| `search.rebuild` | `FileSearchIndexService.RequestRebuild()` |
| `search.cancel` | `CancelScan()` |
| `search.pause` | `Pause()` |
| `search.resume` | `Resume()` |
| `search.reconcile` | `RequestReconcile()` |

- 这些命令只排队/切换状态，不等待扫描完成；返回当前 status。
- `search.rebuild` 需要 `confirm: true`（CLI `--yes`），因为会触发全盘扫描。

### 7.5 `app.showIsland`

- `app.showIsland` → `WidgetManager.Instance.ShowIsland()`。
- 不提供 `app.showSettings`（决策 10）；设置窗口只由用户从 UI 打开。
- 不做 `app.shutdown`；结构变更的重启由应用内 restart helper 在 `config.replace/restore --restart` 中编排（见 3.3、决策 11）。

## 8. `AppDiagnostics`

新增 `Core/AppDiagnostics.cs`，与 `SearchDiagnostics` 同风格：

- 路径：`%LocalAppData%\Kobold\logs\app.log`；超过 1 MB 轮转到 `app.log.1`。
- 线程安全、异常吞掉、UTF-8、每行带时间/线程/级别。
- 记录：
  - 控制请求：`control method=config.patch id=... ok=true ms=12`（不记录完整 config）。
  - 配置应用：`config apply fields=Theme,Language revision=... restartRequired=false`。
  - widget/group：`widget.add id=... kind=note`、`group.merge target=... members=2`。
  - 服务：`search.rebuild requested`、状态变化、异常。
  - 启动/退出、控制面启动/停止、管道错误。
- 不记录 note/todo 正文、完整路径列表或用户文件内容；路径只在必要时记录单个。
- `diag.logs.tail` 读这个文件；`SearchDiagnostics` 继续写 `search.log`。

## 9. `koboldctl.ps1`

### 9.1 定位应用

优先级：

1. `-Exe <path>` 参数。
2. `%LocalAppData%\Programs\Kobold\Kobold.exe`。
3. 当前目录/仓库 `bin\Debug\net48\Kobold.exe`（开发回退）。

管道名用当前用户 SID + session 计算，与应用一致。

### 9.2 命令

```powershell
koboldctl.ps1 status [--json]
koboldctl.ps1 config get [--summary|--full] [--json]
koboldctl.ps1 config describe [--json]
koboldctl.ps1 config validate --file <json> [--json]
koboldctl.ps1 config patch --file <patch.json> [--base-revision <r>] [--dry-run] [--yes]
koboldctl.ps1 config replace --file <config.json> [--base-revision <r>] [--dry-run] [--yes] [--restart]
koboldctl.ps1 config backup [--label <name>] [--json]
koboldctl.ps1 config restore --backup <path> [--yes] [--restart]
koboldctl.ps1 widget add --kind <kind> [--name <n>] [--color <hex>] [--json]
koboldctl.ps1 widget update --id <id> [--name <n>] [--color <hex>] [--locked <bool>] [--pinned <bool>] [--json]
koboldctl.ps1 widget delete --id <id> --yes
koboldctl.ps1 group merge --target <id> --member <id> [--member <id>...] [--json]
koboldctl.ps1 group detach --group <id> --widget <id> [--json]
koboldctl.ps1 group reorder --group <id> --from <n> --to <n>
koboldctl.ps1 group dissolve --group <id> --yes
koboldctl.ps1 search status [--json]
koboldctl.ps1 search query --text <text> [--limit 50] [--json]
koboldctl.ps1 search rebuild|cancel|pause|resume|reconcile [--yes]
koboldctl.ps1 diag snapshot [--json]
koboldctl.ps1 diag logs --name app|search [--lines 200] [--json]
koboldctl.ps1 diag folders [--json]
```

- 默认人类可读；`--json` 输出原始响应，供 agent 解析。
- 写入类命令默认不写；`--dry-run` 只显示 changes；`--yes` 才发送 `confirm: true`。
- `--base-revision` 缺省时 CLI 先 `config get` 取当前 revision，再写入；agent 显式传入更安全。
- `--restart`：发送 `config.replace/restore` 的 `restart: true`；应用启动 restart helper 后退出，helper 负责拉起新实例；CLI 轮询 `status` 直到新管道可用或超时（不自己启动进程）。
- 未运行：默认返回 `app_not_running`；`--start` 启动应用并等待管道（skill 先询问用户）。
- `--pipe <name>`：可选管道名覆盖，仅用于测试/多会话调试；默认按当前 SID/session 计算。
- 退出码：0 成功；1 校验/冲突/业务错误；2 连接/协议错误；3 需要重启但未加 `--restart`。

### 9.3 输出约定

- 所有 `--json` 输出为单行/缩进 JSON，不混入 PowerShell 日志；错误也输出 JSON 到 stdout，stderr 只放 PowerShell 异常。
- 人类输出只显示摘要：路径、revision 前 12 位、changes、restartRequired、错误信息。

## 10. `kobold-config` Skill

### 10.1 目录

```text
skill/kobold-config/
  SKILL.md
  README.md
  references/config-schema.md
  references/troubleshooting.md
  scripts/find-koboldctl.ps1
```

`README.md` 用通用话术告诉用户/agent：“把整个 `kobold-config` 目录放进你的 agent skills 目录；用 `koboldctl.ps1` 操作”。不写特定 agent 的安装脚本，也不假设任何 agent 的路径。

`scripts/find-koboldctl.ps1` 只负责按顺序找 `koboldctl.ps1`（安装目录 → 仓库 `tools/`），不复制 CLI 实现，避免双份漂移。

### 10.2 触发

`SKILL.md` 的 description 覆盖：

- “配置 Kobold / 改主题/语言/岛/图标/搜索设置”
- “新增/删除/合并/拆分组件或组”
- “搜索索引不工作 / 重建索引 / 看搜索日志”
- “检查 Kobold 状态 / config.json / 诊断”

### 10.3 工作流

1. **连接**：`koboldctl status --json`；未运行则询问用户是否 `--start`。
2. **读取**：`config describe --json`、`config get --summary --json`、`diag snapshot --json`；需要搜索问题时 `search status` + `diag logs --name search --lines 200`。
3. **诊断**：先解释现状，不直接改；给出最小变更方案。
4. **生成变更**：设置用 `config patch`；结构用 `widget.*`/`group.*`；先 `--dry-run`（或 `--json` 预览）。
5. **确认**：展示 diff/命令/影响（是否重启、是否删除、是否扫描）；用户明确同意后才加 `--yes`。
6. **应用**：执行命令；`conflict` 时重新读取 revision 并重新生成；`restart_required` 时询问后 `--restart`。
7. **复查**：`config get --summary`、`diag snapshot`、必要时 `search query`；报告 revision 和结果。
8. **失败**：保留错误 JSON；不要盲目重试；需要时 `config backup`/`config restore`。

### 10.4 安全规则（写死在 SKILL.md）

- 禁止直接编辑运行中的 `%AppData%\Kobold\config.json`；只能通过 `koboldctl`。
- 所有写操作必须带 `--base-revision`；冲突不得覆盖。
- `widget delete`、`group dissolve`、`config replace`、`config restore`、`search rebuild`、`--restart` 必须二次确认。
- 默认 `--summary`；读取 `--full`（note/todo 正文）前必须征得用户同意。
- 不执行任意 shell/文件写入；只调用 `koboldctl` 白名单。
- 不把完整 config/日志粘贴到公开渠道；只引用必要片段。
- 应用未运行时，先询问是否启动；不静默启动/重启。

### 10.5 参考文档

- `config-schema.md`：顶层字段、widget kind、group、FileSearch 设置、常见 patch 示例。
- `troubleshooting.md`：
  - 搜索一直 Indexing：`search status` → `diag logs --name search` → `search cancel`/`rebuild`。
  - 搜索无结果：`search query`、`IndexStatus`、根目录/类型设置。
  - config 损坏：`config validate`、`config.json.failed-*`、`config restore`。
  - widget 消失/组异常：`config get --summary`、`diag snapshot`、`group detach`。
  - 主题/语言不生效：`config patch` + `restartRequired` 检查。

## 11. 安全与失败模式

| 风险 | 处理 |
|---|---|
| 其他本地用户连接管道 | 管道 ACL 仅当前用户/SYSTEM；名称含 SID/session |
| 恶意本地进程调用 | 方法白名单、参数校验、破坏性操作 `confirm`、审计日志 |
| agent 覆盖用户改动 | `baseRevision` + `conflict`；写前备份 |
| 结构变更导致数据丢失 | 高层命令走 manager；`config.replace/restore` 备份 + 重启 + 用户确认 |
| note/todo 隐私 | 默认 summary；full 需明确同意 |
| 日志泄漏 | 只记 ID/字段名/状态，不记正文；固定日志路径 |
| 管道卡死 | 读/执行超时；单请求连接；UI 线程异常返回 `internal` |
| 应用未运行 | `app_not_running`；`--start` 显式启动 |
| 重启失败 | helper 等待父进程退出 + 启动 + 轮询新管道；helper 失败时 CLI 报 `restart_failed`，保留 config 备份 |
| 旧版本应用 | 管道不存在/版本不匹配 → `control_unavailable`，提示升级 |

## 12. 打包与分发

- `tools/koboldctl.ps1` 作为唯一 CLI 实现；`installer-common.ps1` 增加 `KoboldToolFiles = @('koboldctl.ps1')`，`Install-KoboldFiles` 复制到安装目录。
- `skill/kobold-config/` 作为可移植目录随仓库/发布包提供；`README.md` 说明“复制到你的 agent skills 目录”，不写特定 agent 的安装脚本，不猜任何 agent 的路径。
- `README`/`CHANGELOG` 增加“本地 agent 控制 / skill”段落。
- 不新增 NuGet 依赖；PowerShell 5.1+、.NET Framework 4.8。
- restart helper 复用同一个 `Kobold.exe` 的 `--restart-helper` 模式，不新增可执行文件。
- 协议版本 `1` 写入管道名和请求；应用版本在 `diag.snapshot` 返回。

## 13. 测试计划

### 13.1 新增 `tests/ControlCheck`（CI）

- 协议 DTO 序列化/反序列化、`v` 校验、错误码映射。
- 管道名生成（SID/session 格式）。
- revision 计算稳定性；`baseRevision` 冲突。
- `config.patch` merge patch、禁止结构字段、dryRun。
- `config.validate` 修复/警告；`config.describe` 覆盖所有 `AppConfig` 字段和注册 kind。
- `config.backup` 命名/保留 20 个；`config.restore` 只允许 backups 路径。
- 命令白名单、`confirm` 要求、参数校验。
- `StartupRegistryService` / `QuickAccessService` 的纯逻辑（成功/失败/warning）；`DesktopIconGuard` 策略（本次隐藏→退出显示、下一次启动再隐藏、幂等）。
- restart helper 参数解析/父进程等待/失败退出码（可注入 process 探针）。
- 不依赖 WPF/UI；加入 `Run-Checks.ps1` 的 `ci` 组（27 → 28）。

### 13.2 `tests/XamlLoadCheck`（本地 UI 集成）

- 启动 `ControlServer`（或直接调用 service），通过测试客户端发送：
  - `widget.add` 不打开面板/不抢焦点；返回 id；island 更新。
  - `widget.update` 改名/颜色/pin；grouped member 的 group pin 派生正确。
  - `widget.delete` folder 成员 eject；group 自动解散。
  - `group.merge/detach/reorder/dissolve` 保持 live view、不重建无关视图。
  - `config.patch` 热应用 theme/language/island/icon/file search/HideDesktopIcons；`StartWithWindows`/`PinStorageToQuickAccess` 走 fake service 并返回 warning 路径。
  - `diag.snapshot` 字段存在；`diag.search.status/query` 可用。
- 所有测试在现有 config sandbox 中运行，不碰真实 config。

### 13.3 `tests/ConfigSafetyCheck`

- revision 变化、备份/恢复、结构变更 `restartRequired`、写失败不破坏原文件。
- 恢复后 group/widget 修复规则与 `AppConfig.Load` 一致。

### 13.4 `koboldctl.ps1` 集成

- 用测试命名管道 server（PowerShell 或 C#）验证 CLI 参数、JSON 输出、退出码、`--dry-run`、`--yes`、`--base-revision`。
- 真实应用手动验证：app running/not running、`--start`、`--restart`、管道 ACL、冲突、日志 tail。
- 手动：把 `skill/kobold-config` 放进任一支持 `SKILL.md` 的 agent skills 目录，按示例改主题/加 note/合并组/调试搜索；不依赖特定 agent 路径。

### 13.5 回归

- 现有 `Run-Checks -Group ci`、`-Group checks`、`GroupUiSpike --check` 全绿。
- 0 警告；受保护文件不变；真实 config pin 干净。

## 14. 实施顺序（供 writing-plans 使用）

1. 协议 + 管道 + `ControlServer` + `AppDiagnostics` 骨架；`status`/`diag.snapshot` 只读。
2. `ConfigControlService`：get/describe/validate/patch + revision + backup；热应用；`StartupRegistryService` / `QuickAccessService` / `DesktopIconGuard`。
3. `WidgetGroupControlService`：widget.add/update/delete + group.*，复用 manager API。
4. `config.replace/restore` + 应用内 restart helper；搜索服务命令 + 日志 tail。
5. `koboldctl.ps1` 完整命令/JSON/退出码/启动等待。
6. `kobold-config` skill（`SKILL.md` + `README.md` + references）+ README/CHANGELOG。
7. `ControlCheck` + XamlLoadCheck/ConfigSafety 集成 + 全量验证。
8. 手动冒烟：真实安装目录、agent skill、管道 ACL、重启/恢复。

## 15. 已确认的实现决定

1. **Skill 安装**：不做特定 agent 路径适配；`skill/kobold-config/README.md` 告诉用户/agent 把目录放进自己的 skills 目录。不写安装脚本。
2. **`app.showSettings`**：不提供该控制命令（选项 C）；设置窗口只由用户从 UI 打开。
3. **重启编排**：使用应用内 restart helper（选项 B）；应用写盘后启动 helper，helper 等父进程退出再拉起新实例；CLI 只等待新管道。
4. **系统级设置**：`StartWithWindows` / `PinStorageToQuickAccess` 立即生效（选项 B）；`StartupRegistryService` / `QuickAccessService` 应用，失败返回 warning，不要求重启。
5. **`diag.folders`**：返回每个 folder 的 `active` 与 `watcherActive`；group 内只有 active tab 的 folder watcher 为 true，实现时遍历 `WidgetGroupWindow.Views` 并在 UI 线程取快照。
6. **桌面图标保护**：本次运行隐藏了图标时，退出/重启/注销立即显示；下一次启动按配置自动隐藏；强制杀进程是已知边界。
