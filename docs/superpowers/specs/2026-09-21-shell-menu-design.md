# Kobold 面板内 shell 菜单 + 文件管理动作（设计草案）

> **状态**：设计草案（2026-09-21）。方向已与用户确认：**方案 C**（常用动作自建 + "显示更多选项…" 调原生
> shell 菜单）、**v1 动作集已确认**。本文档里的少量"默认值"是我拟定的、尚未逐条确认的细节，已在各节标出
> `默认`，复核时可直接改。
>
> **前置**：v1 的浏览模式（`docs/superpowers/specs/2026-09-21-folder-browse-design.md`）已合并进 `main`，
> 并已部署。本设计建立在它之上。

## 1. 目标

面板内浏览目录时，不用再跳回 Explorer 就能完成日常工作：**在终端中打开**、**新建文件夹/文本文档**、
**重命名**、**删除到回收站**，以及需要时调用**完整的资源管理器右键菜单**（Git Bash Here、7-Zip、属性、
发送到等第三方动词）。

## 2. 规则变更（本设计最重要的前提）

v1 的硬约束是"浏览态只读"。本设计**把它改成**：

> **浏览态不隐式改动任何东西；用户显式选择的动作可以写盘。**

- 之前被禁掉的是"面板空白右键 → 新建文件/文件夹"（它当时写的是组件存储并追加条目，语义错位，见 v1 裁决 R4）；
  现在浏览态空白处右键**应当**提供针对**当前目录**的动作，新建就是其中一个。
- 写操作的安全边界见第 6 节。

## 3. 范围：v1 动作集

**浏览态 · 条目右键**（支持多选）：

| 动作 | 适用范围 | 说明 |
|---|---|---|
| 打开 | 全部 | 文件夹 → 进入；文件 → shell 打开（沿用现有 `OpenWithShell`） |
| 在终端中打开 | 仅文件夹 | 文件条目取**所在目录** |
| 在资源管理器中打开 | 全部 | 现有 `OpenContainingFolder`（文件为 `/select,`） |
| 复制路径 | 全部 | 现有 `CopyPathToClipboard` |
| 重命名 | 仅单选、且路径存在 | 复用 `DialogFactory.ShowInput` |
| 删除到回收站 | 全部（可多选） | 二次确认，多选显示数量 |
| 显示更多选项… | 仅单选（v1） | 原生 `IContextMenu` 菜单 |

**浏览态 · 空白右键**（作用于当前目录）：新建文件夹 · 新建文本文档 · 在终端中打开 · 在资源管理器中打开 ·
复制路径 · 刷新 · 显示更多选项…

**根态 · 条目右键**：现有项保持不变，新增 在终端中打开（仅文件夹）· 在资源管理器中打开。

**键盘通道**：`F5` 刷新（浏览态重新枚举当前目录，根态刷新组件条目）· `Shift+F10` 与菜单键打开对应位置的菜单 ·
`Enter` 打开选中项。`默认：三者都做`（各约 5–10 行；如觉得多余可去掉 `Enter`）。

**明确不做（v1）**：剪切/复制/粘贴、发送到、属性、拖拽移动、内联重命名编辑器、多选的原生菜单
（`IShellItemArray` 版本留后续）——前四项由"显示更多选项…"的原生菜单兜底。

## 4. 架构与文件划分

沿用既有分层（`Services/` 与 `TrayIconService` 同层；面板动作在 `Controls/` 的 partial 里）：

| 文件 | 职责 |
|---|---|
| `Services/TerminalLauncher.cs` | 终端解析（`wt.exe` → `powershell.exe` → `cmd.exe`）与启动；解析顺序是**纯函数**，可自检 |
| `Services/ShellFileOperations.cs` | 新建（文件/目录）、重命名、**回收站删除**；名称校验（`Path.GetInvalidFileNameChars` + 重名检查）为纯函数，可自检 |
| `Services/ShellContextMenuService.cs` | `IContextMenu` 互操作 + 隐藏消息窗口 |
| `Controls/FolderWidget.MenuActions.cs` | 两个位置的菜单组装、动作入口、键盘通道（新 partial） |
| `tests/FolderListingCheck/`（扩展） | 终端解析顺序、名称校验、删除参数构造的检查 |

## 5. 互操作设计（原生菜单）

1. 路径 → `SHParseDisplayName` → `IShellItem` → `BindToHandler(BHID_SFUIObject)` 取得 `IContextMenu`；
   **空白处的"当前目录菜单"**同样走这条路：对目录自身取 `IContextMenu`（即资源管理器空白处那份）。
2. 隐藏宿主窗口：`HwndSource`（不可见、`WS_POPUP`）用于接收 `WM_DRAWITEM` / `WM_MEASUREITEM` /
   `WM_INITMENUPOPUP`，按序转发给 `IContextMenu2` / `IContextMenu3::HandleMenuMsg(2)`——**7-Zip 这类自绘项
   没有它就画不出来**。
3. `QueryContextMenu` 带 `CMF_NORMAL`（按住 Shift 时带 `CMF_EXTENDEDVERBS`）→ `TrackPopupMenuEx`（坐标用
   **物理像素**，宿主窗口作为 owner）→ 返回的 command id → `InvokeCommand`。
4. 失败回退：取不到 `IShellItem`/`IContextMenu`、或 `QueryContextMenu` 返回空 → **不弹任何东西**，只写
   `Debug.WriteLine`；我们的自建动作与菜单不受影响。
5. 进程内运行（与资源管理器相同的暴露面）。spec 记录升级路径：把第 2–3 步放进一个独立小宿主进程，
   以防某些第三方扩展挂起拖住界面。

## 6. 各动作实现要点与安全边界

- **终端**：`wt.exe -d "<dir>"`（存在则优先）→ `powershell.exe -NoExit -Command "Set-Location -LiteralPath '<dir>'"` →
  `cmd.exe /K cd /d "<dir>"`。含空格/单引号路径要正确引用（实测覆盖）。
- **新建**：复用 `DialogFactory.ShowInput`。**根态保持现状**（建到 `%AppData%\Kobold\Storage` 并追加为"已收纳"条目）；
  **浏览态建到 `CurrentBrowsePath`，不追加组件条目**。重名 → 提示且**不覆盖**；非法字符 → 提示并留在对话框。
- **重命名**：`File.Move` / `Directory.Move`；空名、非法字符、目标已存在 → 提示不执行。
- **删除**：`SHFileOperation(FO_DELETE, FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT)`（我们自己已确认过）→
  **只进回收站**；**失败绝不回退为永久删除**，报错并保持原状。多选按 `GetSelectedItems()` 批量。
- **刷新**：任何写操作后重新枚举；组件里指向被改名/删除文件的条目由既有 `IsMissing` 机制显示 `!`（预期行为）。
- **一致性**：写操作只作用于磁盘；浏览态依旧**不改动 `_data.Items`**（只有根态的"新建"按现状追加条目）。

## 7. 测试与验收

**可自检（进 `tests/FolderListingCheck`）**：终端解析顺序（wt → powershell → cmd，缺失时逐级回退）、
名称校验（非法字符、空名、重名）、回收站参数构造（`FO_DELETE` + `FOF_ALLOWUNDO` 且无 `FOF_NOCONFIRMATION` 之外的破坏性标志）。

**手动验收清单**：
1. 浏览态空白右键 → 新建文件夹/文本文档，出现在**当前目录**（资源管理器里可见）；重名被拒绝。
2. 条目右键 → 重命名（非法字符/重名被拒）、删除进回收站（**能从回收站还原**）、多选删除显示数量。
3. 在终端中打开：文件夹条目 → 进入该目录；文件条目 → 进入其所在目录；含空格与中文的路径正确。
4. 空白/条目"显示更多选项…" → 出现本机 shell 菜单（含 **Git Bash Here、7-Zip**）；无 Verb 的路径不报错。
5. `Shift+F10`、菜单键、`F5`、`Enter` 行为正确；`Esc` 仍收面板。
6. 200% 缩放、深/浅主题、多显示器下菜单位置正确。
7. 根态：现有项行为不变 + 新的"在终端中打开/在资源管理器中打开"可用。
8. 回归：既有九项自检全绿；v1 浏览功能（进入/返回/路径菜单/空格预览）不受影响。

## 8. 风险

| 风险 | 处置 |
|---|---|
| 第三方 shell 扩展挂起 → 界面被拖住 | v1 接受（与资源管理器同款暴露面）；升级路径＝独立宿主进程 |
| 原生菜单深浅色不跟随我们的主题 | 未公开 `uxtheme` API 可实测（`SetPreferredAppMode`），**不承诺**；我们的自建菜单不受影响 |
| Win11 新式菜单 vs 旧式 | 我们拿到"显示更多选项"层；高频动作自建，不依赖系统菜单 |
| 回收站 API 在特殊路径（网络/可移动介质）失败 | 报错并保持原状，绝不永久删除 |
| 浏览态此刻可写 → 误操作 | 删除只进回收站 + 二次确认；新建/重命名不覆盖 |

## 9. 建议的实施顺序（供 writing-plans 展开）

1. 纯逻辑与自检：终端解析、名称校验、回收站参数（TDD）。
2. 写操作接入：浏览态"新建文件夹/文本文档" + 重命名 + 删除到回收站（含确认与刷新）。
3. 终端/资源管理器/复制路径：浏览态与根态两处接入。
4. 自建菜单装配：空白右键 + 条目右键（多选规则）+ 键盘通道。
5. 原生菜单互操作：隐藏消息窗口 + `IContextMenu` 全链路 + "显示更多选项…"。
6. 手动验收 + README/三语文案补齐。

## 附录：上一轮（v1 浏览）遗留的小项，与本次无关，仅备查

页脚高度估算 20 vs 实际 ~23（被 `PADDING` 吸收）；`tests/FolderListingCheck` 缺 ACL 类用例（需造无权限目录）；
`DirectoryNotFoundException` 注释本身准确；页脚点击热区为文字宽度而非整行；头部/页脚拖放目标未标记拒绝
（锁定面板同样如此，属既有不对称）；面板关闭回调可加代际护栏；QuickLook 解析可显式指定 `RegistryView`；
`TextFormattingMode=Display` 在 1.2 缩放下字形先整数 hint 再放大（若观感发虚即源于此）。
