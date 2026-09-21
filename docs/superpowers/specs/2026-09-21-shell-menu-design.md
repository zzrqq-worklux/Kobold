# Kobold 面板内 Shell 菜单 + 文件管理动作（Shell Menu & File Actions）设计

> **状态**：设计已复核（2026-09-21），待用户确认后进入实施计划。方向（方案 C）与 v1 动作集已确认。
> **前置**：v1 浏览模式（`docs/superpowers/specs/2026-09-21-folder-browse-design.md`）已合并 `main` 并部署；
> 本设计建立在它之上，并修改它的一条硬约束（见 §2）。
> **复核依据**：代码走查（`FolderWidget.*`、`MenuBuilder`、`DialogFactory`、`StorageOps`）、
> Microsoft 文档（`IContextMenu` / `IShellFolder::CreateViewObject` / `SHFileOperation` / `IFileOperation`）、
> 本机注册表实测（2026-09-21，见 §5 第 9 条）。

## 复核修正（相对 2026-09-21 草案的实质变化）

| # | 草案 | 正式方案 | 原因 |
|---|---|---|---|
| 1 | 空白处"当前目录菜单"对**目录自身**取 `IShellItem` → `BindToHandler(BHID_SFUIObject)` | `IShellFolder::CreateViewObject(IID_IContextMenu)` | 前者得到的是"文件夹**条目**"菜单（打开/固定到快速访问），不是资源管理器空白处那份；后者才是 `Directory\Background` 注册项的宿主（Git Bash Here、新建、粘贴等） |
| 2 | 删除承诺"**失败绝不回退为永久删除**" | 承诺"**绝不静默永久删除**"：本机固定卷预检（拒绝网络/可移动/subst）+ 逐项调用 + `FOF_WANTNUKEWARNING` 兜底 | `SHFileOperation`/`IFileOperation` 在"文件超出回收站配额"时由系统永久删除；`FOF_WANTNUKEWARNING` 是唯一能让用户显式确认的开关。原承诺在 OS 层无法做到，写进 spec 就是空头支票 |
| 3 | 隐藏消息窗口作为原生菜单宿主 | **面板窗口自身** + `HwndSource.AddHook` 转发菜单消息 | 隐藏窗口若成为前台窗口，未固定面板会触发 `Deactivated` 而收起（`HidePanel` 还会 `ResetBrowse` 打断浏览态） |
| 4 | 多选仅限制重命名/原生菜单 | 明确多选矩阵（§3.1）：多选只开删除 + 复制路径，其余灰显 | 多目标的"打开/定位/终端"语义有歧义，v1 不值得引入 |
| 5 | 根态新增"在终端中打开 · 在资源管理器中打开" | 根态**只**新增"在终端中打开"（仅文件夹） | 根态已有"打开文件位置"（`/select,`），与"在资源管理器中打开"重复，不再加同义项 |
| 6 | 纯逻辑自检扩展 `tests/FolderListingCheck` | 新建 `tests/ShellOpsCheck` | `FolderListingCheck` 只该管目录枚举/路径标签；终端/名称/删除预检放进专属项目 |
| 7 | `SHParseDisplayName` | `SHCreateItemFromParsingName`（Vista+） | 等价且少一步 PIDL 处理；`IShellItem` 与 `IShellFolder` 两种绑定都要走它 |

另有两处补充（草案未提，复核发现）：§3.4 键盘通道的锚点定义、§6.6 模态对话框护栏。

## 1. 目标

面板内浏览目录时，不用再跳回资源管理器就能完成日常工作：**在终端中打开**、**新建文件夹/文本文档**、
**重命名**、**删除到回收站**，以及需要时调用**完整的资源管理器右键菜单**（Git Bash Here、7-Zip、属性、发送到等第三方动词）。

成功标准：

- 浏览态下"新建 / 重命名 / 删除 / 终端 / 复制路径"全部可用，且每个写动作都有明确的"取消即保持原状"路径。
- 原生菜单在**条目**与**空白**两处都能弹出并执行（本机：条目菜单含 7-Zip/发送到/属性，空白菜单含 Git Bash Here/新建/粘贴/属性）。
- 删除只会进回收站；**不存在静默永久删除的路径**（唯一永久删除入口是系统弹出的 nuke 警告，用户显式确认才生效）。
- 浏览动作不改动 `_data.Items`；既有 v1 浏览能力（进入/返回/路径菜单/空格预览）与根态行为无回归。

## 2. 规则变更（本设计最重要的前提）

v1 的硬约束是"浏览态只读"。本设计**把它改成**：

> **浏览态不隐式改动任何东西；用户显式选择的动作可以写盘。**

- 之前被禁掉的是"面板空白右键 → 新建文件/文件夹"（它当时写的是组件存储并追加条目，语义错位，见 v1 裁决 R4）；
  现在浏览态空白处右键**应当**提供针对**当前目录**的动作，新建就是其中一个（写到磁盘，不追加组件条目）。
- 写操作的安全边界见 §6；**浏览态依旧不改动 `_data.Items`**（只有根态的"新建/重命名"按现状追加/更新条目）。
- 根态（组件自身条目）的既有菜单与行为保持不变，只增加一项（§3.3）。

## 3. 范围：v1 动作集

### 3.1 浏览态 · 条目右键（多选矩阵明确）

| 动作 | 可用条件 | 多选 | 行为 |
|---|---|---|---|
| 打开 | 路径存在 | 仅单选 | 文件夹 → 进入；文件 → shell 打开（复用 `OpenWithShell`） |
| 在终端中打开 | 路径存在 | 仅单选 | 文件夹 → 该目录；文件 → 所在目录（§6.1） |
| 在资源管理器中打开 | 路径存在 | 仅单选 | 文件夹 → `explorer.exe "<dir>"`；文件 → `/select,`（复用 `OpenContainingFolder` 的文件语义）；文案用新 key `Menu_OpenInExplorer`（对两者都成立） |
| 复制路径 | ≥1 项 | **可用** | 单选原样；多选按列表顺序以 CRLF 连接，不加引号（与现有单选一致） |
| 重命名 | 仅单选、且路径存在 | 仅单选 | 复用 `DialogFactory.ShowInput`（§6.3） |
| 删除到回收站 | ≥1 项 | **可用** | 二次确认，多选显示数量（§6.4） |
| 显示更多选项… | 路径存在 | 仅单选 | 原生 `IContextMenu` 菜单（§5） |

- 多选（≥2 项）时**灰显**：打开 / 在终端中打开 / 在资源管理器中打开 / 重命名 / 显示更多选项…。
- 右键一个**未选中**的条目：先单选它（清除其他选择）再弹菜单；右键已选中条目则保持多选集合（Explorer 语义）。
- 菜单顺序：`打开 · 在终端中打开 · 在资源管理器中打开 · 复制路径 · ─ · 重命名 · 删除到回收站 · ─ · 显示更多选项…`

### 3.2 浏览态 · 空白右键（作用于当前目录）

`新建文件夹 · 新建文本文档 · ─ · 在终端中打开 · 在资源管理器中打开 · 复制路径 · 刷新 · ─ · 显示更多选项…`

- 空白命中判定沿用现有 `ExpandedPanel_MouseRightButtonDown` 的"向上找条目 Border"逻辑；找不到即空白。
- 空白菜单没有"打开"（无目标）；"刷新"= `F5`；"在资源管理器中打开"= `explorer.exe "<当前目录>"`。
- 根态的空白右键仍是现有的"新建文件/新建文件夹（进存储）"，不套用本表。

### 3.3 根态 · 条目右键

现有项全部不变（打开 / 打开文件位置 / 重命名 / 收纳 / 移出 / 移除 / 复制路径），**只新增**：
**在终端中打开**（仅文件夹且非 `IsMissing`）→ 用该文件夹路径启动终端。

### 3.4 键盘通道

| 键 | 行为 |
|---|---|
| `F5` | 浏览态：重新枚举当前目录；根态：重建条目（刷新 `IsMissing` 与图标）。刷新后清空选择（v1 约定） |
| `Enter` | 有且仅有一个选中项 → 打开（文件夹进入 / 文件 shell 打开）；未选或多选 → 无操作 |
| `Shift+F10` / 菜单键（`Key.Apps`） | 有选中项 → 该条目的条目菜单，锚在该条目左下角；无选中项 → 空白菜单，锚在内容区左上角（+8px 内边距） |
| `Backspace` | 返回上一级（现状不变） |
| `Esc` | 收起面板（现状不变，不改肌肉记忆） |

- `Shift+F10` 在 WPF 里是 `Key.F10` + `ModifierKeys.Shift`；菜单键是 `Key.Apps`。
- 锚点一律用**物理像素**换算（`PointToScreen`），自建菜单用 WPF `Placement/PlacementTarget`，原生菜单用该坐标。

### 3.5 明确不做（v1）

剪切/复制/粘贴/发送到/属性作为**自建**项（交给"显示更多选项…"的原生菜单兜底）；拖拽移动/从资源管理器拖入
（浏览态继续禁止）；内联重命名编辑器；多选的原生菜单（`IShellItemArray` 版本）；面包屑/前进历史；
`FileSystemWatcher` 实时刷新；命令面板；进程外菜单宿主（记录为升级路径）。

## 4. 架构与文件划分

沿用既有分层（`Services/` 与 `TrayIconService` 同层；面板动作在 `Controls/` 的 partial 里）：

| 文件 | 职责 | 动作 |
|---|---|---|
| `Services/TerminalLauncher.cs` | 终端解析（wt → powershell → cmd）与启动；解析与三种转义均为纯函数 | 新建 |
| `Services/ShellFileOperations.cs` | 新建（文件/目录）、重命名、回收站删除；名称校验、删除预检、参数构造为纯函数 | 新建 |
| `Services/ShellContextMenuService.cs` | `IContextMenu` 互操作（条目 + 目录背景）、菜单消息转发、命令调用 | 新建 |
| `Controls/FolderWidget.MenuActions.cs` | 两个模式的菜单装配、动作入口、多选规则、键盘通道、模态护栏 | 新建 partial |
| `Controls/FolderWidget.Browse.cs` | 浏览态条目菜单改走新入口；空白右键不再早退 | 修改 |
| `Controls/FolderWidget.Interactions.cs` | 根态条目菜单 +在终端中打开；`Deactivated` 模态护栏；`Window_KeyDown` 扩展 | 修改 |
| `Helpers/ContextMenuBuilder.cs` | `MenuBuilder.AddItem` 增加 `isEnabled` 可选参数（多选灰显用） | 修改 |
| `Helpers/DialogFactory.cs` | `ShowInput` 增加可选校验回调（错误显示在对话框内，不关闭） | 修改 |
| `Core/Localization.cs` | 新增 key × 三语（§9） | 修改 |
| `tests/ShellOpsCheck/`（csproj + Program.cs） | 终端解析/转义、名称校验、删除预检与参数构造 | 新建 |
| `README.md` | 功能表 + 用法表（中英两节） | 修改 |

不改动：`FolderWidget.xaml`（菜单全部代码构造）、`DisplayItem`、`FolderListing`、`Core/WidgetConstants.cs`
（本次没有需要集中的常量；删除标志属于 `ShellFileOperations` 的实现细节）。

**接口草案**（供 writing-plans 细化，C# 7.3）：

```csharp
// Services/TerminalLauncher.cs
public enum TerminalKind { WindowsTerminal, PowerShell, Cmd }
public sealed class TerminalCommand { public TerminalKind Kind; public string FileName; public string Arguments; }

public static class TerminalLauncher
{
    // 纯函数：按 wt -> powershell -> cmd 顺序取第一个 isAvailable 命中的候选；全不命中返回 null。
    public static TerminalCommand Resolve(string directory, Func<TerminalKind, bool> isAvailable);
    public static string QuoteForWindowsTerminal(string path);  // "..."，结尾反斜杠加倍
    public static string QuoteForPowerShell(string path);       // '...'，内部单引号写两遍
    public static string QuoteForCmd(string path);              // "..."
    public static bool TryLaunch(string directory, out string error); // 启动失败依次降级；全失败返回 false
}

// Services/ShellFileOperations.cs
public enum NameCheck { Ok, Invalid, Exists }   // Invalid 含：空、非法字符、结尾点/空格、保留设备名
public static class ShellFileOperations
{
    public static NameCheck ValidateName(string name, string directory, Func<string, bool> exists, string selfPath = null);
    public static bool CreateFolder(string directory, string name, out string error);
    public static bool CreateTextFile(string directory, string name, out string error); // 按输入原样命名（不自动补扩展名）
    public static bool Rename(string path, string newName, out string error);           // 失败保持原状
    public static bool CanRecycle(string path, Func<string, DriveType> driveType, Func<string, bool> isSubst);
    public static int Recycle(IList<string> paths, IntPtr ownerHwnd);                   // 返回失败项数；逐项调用
    internal const uint DeleteFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI;
    internal static string ToDoubleNullTerminated(IEnumerable<string> paths);           // 纯函数，可自检
}

// Services/ShellContextMenuService.cs（不引用 WPF 控件类型，只收 HWND 与物理坐标）
public static class ShellContextMenuService
{
    public static bool ShowForItem(IntPtr ownerHwnd, string path, int xPx, int yPx);
    public static bool ShowForFolderBackground(IntPtr ownerHwnd, string folder, int xPx, int yPx);
    public static bool HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
}
```

调用方（`MenuActions`）在会话期间给面板的 `HwndSource` 挂 hook，把菜单消息转给 `HandleMenuMessage`；会话结束摘除。

## 5. 互操作设计（原生菜单）

1. **条目菜单**：`SHCreateItemFromParsingName(path)` → `IShellItem::BindToHandler(null, BHID_SFUIObject, IID_IContextMenu)`。
2. **目录背景菜单**：`SHCreateItemFromParsingName(folder)` → `BindToHandler(null, BHID_SFObject, IID_IShellFolder)`
   → `IShellFolder::CreateViewObject(ownerHwnd, IID_IContextMenu)`。这是资源管理器空白处那份菜单，
   包含 `Directory\Background` 的静态动词（Git Bash Here、新建、粘贴）与背景 `shellex` handler。
3. **宿主与消息转发**：面板窗口自身为 owner；会话期间 `HwndSource.FromHwnd(handle).AddHook(...)` 把
   `WM_DRAWITEM`(0x002B) / `WM_MEASUREITEM`(0x002C) / `WM_INITMENUPOPUP`(0x0117) 交给
   `IContextMenu3::HandleMenuMsg2`（拿不到 `IContextMenu3` 时退 `IContextMenu2::HandleMenuMsg`；都没有则丢弃）。
   **7-Zip 这类自绘子菜单没有它就画不出来**；不用隐藏窗口（复核修正 3）。
4. **显示**：`QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | (Shift ? CMF_EXTENDEDVERBS : 0))`
   → `SetForegroundWindow(panelHwnd)` → `TrackPopupMenuEx(hwnd, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_TOPALIGN, xPx, yPx, hwnd, null)`。
   坐标用物理像素：**鼠标唤出用光标位置**（`Cursor.Position`，与资源管理器一致）；**键盘唤出**用条目左下角 / 内容区左上角锚点（§3.4）。
   时序：原生菜单必须在上一个 WPF 菜单的 `Closed` 事件之后才启动 `TrackPopupMenuEx`——否则它的模态消息循环会把 WPF 菜单的关闭动画冻在原地（表现为"卡一下"且两个菜单重叠）。
5. **执行**：返回的 command id → `CMINVOKECOMMANDINFOEX`，`lpVerbW = MAKEINTRESOURCEW(cmd - 1)`、
   `fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE`、`nShow = SW_SHOWNORMAL`，调 `InvokeCommand`。
6. **失败回退**：取不到 `IShellItem`/`IShellFolder`/`IContextMenu`、`QueryContextMenu` 返回空、或 `TrackPopupMenuEx`
   返回 0 → **不弹任何东西**，只写一行 `Debug.WriteLine`；我们的自建动作与菜单完全不受影响。
7. **资源**：`HMENU`、COM 指针、hook 全部在 `finally` 中释放/摘除；同一时刻只有一个会话（菜单是模态的）。
   COM 对象用 `Marshal.FinalReleaseComObject` **显式释放**——RCW 太小不会触发 GC，靠终结器回收会让每次打开
   累积 shell 扩展的句柄与线程（2026-09-21 实测：稳态 +25 handles/次；显式释放后为 0）。
8. **进程内运行**：与资源管理器相同的暴露面。升级路径记录在案：把 3–5 步放进独立小宿主进程，防止第三方扩展挂起拖住界面（v1 不做）。
9. **本机注册表实测（2026-09-21，验收口径据此）**：
   - Git Bash Here / GUI Here：`Directory\Background\shell` **与** `Directory\shell`（命令 `git-bash.exe "--cd=%v."`，依赖背景菜单提供 `%v` 文件夹上下文）→ 空白与文件夹条目两处都应出现。
   - 7-Zip：`*\shellex\ContextMenuHandlers` **与** `Directory\shellex\ContextMenuHandlers`（**不在** `Directory\Background`）→ 只在**条目**菜单出现；空白菜单中不出现（与资源管理器一致）。
   - 其他：`Directory\Background\shellex\ContextMenuHandlers` 有 `New`/`Sharing`/`FileSyncEx`/`WorkFolders` → 空白菜单应出现"新建/共享/属性"等。
10. **已知限制（v1 记录，不实现）**：不实现 `IObjectWithSite`/`IServiceProvider`/`IFolderView` site，个别依赖 site 的动词
    （如"共享"的部分行为）可能灰显或无效；原生动词弹出的对话框按"面板失去焦点"处理（未固定面板会收起）。

## 6. 各动作实现要点与边界

### 6.1 在终端中打开（`TerminalLauncher`）

- 候选顺序与探测：`%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe` → `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`
  → `%SystemRoot%\System32\cmd.exe`。探测函数可注入，解析是纯函数。
- 参数（纯函数，全部覆盖转义）：
  - wt：`-d "<path>"`；路径以 `\` 结尾时再补一个 `\`（命令行解析会把 `\"` 当转义）。
  - powershell：`-NoExit -Command "Set-Location -LiteralPath '<path>'"`；内层单引号写两遍。
  - cmd：`/K cd /d "<path>"`。
- 目标目录：文件夹条目 → 自身；文件条目 → 所在目录；空白 → 当前浏览目录；根态文件夹条目 → 自身。
- `UseShellExecute = true`；启动抛异常 → 依次降级到下一候选；全部失败 → `Dialog_TerminalNotFound`。
- 已知限制：cmd 回退对含 `%` 的目录名会做变量展开（wt/ps 不受影响），记录不特殊处理。

### 6.2 新建（浏览态）

- 目标目录 = `CurrentBrowsePath`，**在弹对话框前取快照**（模态期间面板状态可能变化）。
- 复用 `DialogFactory.ShowInput`（新增可选校验回调，错误显示在对话框内、不关闭；根态旧调用保持兼容）。
- 名称校验（`ShellFileOperations.ValidateName`，纯函数）：trim；拒绝空、`Path.GetInvalidFileNameChars()`、
  结尾 `.` 或空格、Windows 保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9，忽略扩展名）、目标已存在
  （大小写不敏感，文件与目录都算）。
- 新建文件夹：`Directory.CreateDirectory`。新建文本文档：`File.WriteAllText(path, "")`；
  **按用户输入原样命名**（不自动补 `.txt`，对话框默认值已含 `.txt`）。
- 成功后刷新浏览列表；**不追加组件条目**；键盘/菜单入口都走同一函数。
- 根态新建保持现状（写 `%AppData%\Kobold\Storage` 并追加为"已收纳"条目）。

### 6.3 重命名（浏览态）

- 仅单选、且 `File.Exists || Directory.Exists`；预填当前文件名（保留原始大小写）。
- 校验同 §6.2；`selfPath` 传入自身路径：**与自身仅大小写不同的改名允许**（`File.Move` 支持），不算"已存在"。
- 执行 `Directory.Move` / `File.Move`；成功后刷新列表、清空选择；**不改 `_data.Items`**（组件里指向旧路径的条目自然显示 `!`，预期行为）。
- 失败 → `Dialog_RenameFailed`，保持原状。
- 根态重命名保持现状（含它更新 `_data.Items` 的行为）。

### 6.4 删除到回收站（浏览态，多选）

- **前置预检**（纯函数，探测可注入）：每个路径都存在、位于本机固定卷（`DriveType.Fixed`）、且不是 subst/映射虚拟盘
  （`QueryDosDevice` 目标以 `\??\` 开头）。任一不满足 → **整体拒绝**并提示 `Dialog_RecycleNotLocal`（不删任何项）。
- **确认**：`MessageBox`（与现有确认一致），单选用 `Dialog_RecycleItem`（名字），多选用 `Dialog_RecycleItems`（数量）。
- **执行**：逐项调用 `SHFileOperationW(FO_DELETE, pFrom = path + "\0\0", hwnd = 面板 HWND,
  fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI)`。
  - 逐项（而非一次批量）：任何一项失败/不可回收都不影响其他项，也避开 shell 批量操作的混合边界。
  - `FOF_ALLOWUNDO` = 进回收站；`FOF_WANTNUKEWARNING` = 超出回收站配额时由系统弹"永久删除？"警告
    （**唯一可能的永久删除路径，必须用户显式确认**）；`FOF_NOERRORUI` = 不弹系统错误框，由我们提示。
  - 失败判定：返回码非 0、`fAnyOperationsAborted`、或调用后路径仍存在 → 计为失败。
- **结果**：刷新浏览列表；有失败项 → `Dialog_RecycleFailed`（数量）；`_data.Items` 不动。
- **承诺**：绝不静默永久删除；网络/可移动/subst 位置直接拒绝。升级路径（后续版本）：
  `IFileOperation` + 每项 `IFileOperationProgressSink` 回调。

### 6.5 其余动作

- **复制路径**：单选原样；多选 `string.Join(Environment.NewLine, paths)`，按列表顺序，不加引号。
- **打开**：单选；文件夹 → `EnterFolder`；文件 → `OpenWithShell`（现有）。
- **在资源管理器中打开**：单选；文件夹 → `explorer.exe "<path>"`；文件 → 现有 `OpenContainingFolder`（`/select,`）。
- 浏览态条目在枚举时必然存在；点击瞬间消失的条目由各动作的存在性校验/失败提示兜底，不特判。

### 6.6 模态护栏（新增；同时修复既有潜在收起问题）

- `Window_Deactivated`（未固定 → `HidePanel`）必须在**我们自己的模态对话框**打开期间被禁止。
  否则新建/重命名/删除确认一弹出，未固定面板就会收起，且 `HidePanel` 的 `ResetBrowse()` 会直接打断浏览态。
- 实现：`FolderWidget` 内计数 `_modalDepth` + `RunModal(...)` 包装；所有 `DialogFactory.ShowInput` /
  `MessageBox.Show` 调用（包括现有根态重命名/新建/删除组件确认）改走包装。
- 模态结束后面板仍未激活且未固定 → `HidePanel()`（保持"失焦收起"的既有语义）。
- 原生动词自己弹出的对话框无法包装，按"失去焦点"处理（见 §5.10）。

### 6.7 刷新与一致性

- 任何写操作后重新枚举（`UpdateUI()`）；根态 `F5` 走 `UpdateUI()`。
- 写操作只作用于磁盘；浏览态不改 `_data.Items`；`OnDataChanged` 不被浏览动作触发。
- `F5` 后清空选择（v1 约定）。

## 7. 测试与验收

### 7.1 可自检（新增 `tests/ShellOpsCheck` 控制台项目，退出码 0 = 全绿）

- **终端解析**：注入存在性下 `wt → powershell → cmd` 的顺序与逐级回退；全缺失返回 null。
- **终端转义**：含空格、中文、单引号、结尾反斜杠（`C:\`）及组合；PowerShell 单引号加倍；wt 结尾反斜杠加倍。
- **名称校验**：空/空白、非法字符、结尾点/空格、保留设备名、已存在（大小写不敏感）；重命名传 `selfPath` 时同路径不算冲突。
- **删除预检**：固定卷通过；可移动/网络（UNC）/subst/不存在 → 拒绝。
- **删除参数**：`DeleteFlags` 含 `FOF_ALLOWUNDO` 与 `FOF_WANTNUKEWARNING`；double-NUL 编码正确（单个与多个路径）。

### 7.2 构建与既有自检

- `dotnet build Kobold.csproj -c Debug`：0 错误、0 新增警告。
- 既有 9 项全绿：`LangCheck` / `WidgetItemsCheck` / `ScreenGeometryCheck` / `IslandLayoutCheck` /
  `StorageOpsCheck` / `DesktopIconsCheck` / `UiTokensCheck` / `XamlLoadCheck` / `FolderListingCheck`，加 `ShellOpsCheck`。
- `LangCheck` 覆盖新增 key（三语一致、无空值、`{0}` 可 Format）。

### 7.3 手动验收（构建后）

1. 浏览态空白右键 → 新建文件夹/文本文档出现在**当前目录**（资源管理器可见）；重名/非法字符在对话框内被拒且**不覆盖**；取消后无任何变化。
2. 条目右键 → 重命名（非法/重名被拒；仅大小写改名成功）；删除进回收站（**打开回收站确认可见、可还原**）；多选删除显示数量；失败项数提示正确。
3. 终端：文件夹条目 → 进入该目录；文件条目 → 所在目录；空白 → 当前目录；含空格/中文/单引号/根目录路径正确；未装 Windows Terminal 时回退 PowerShell（可临时改名验证）。
4. 原生菜单（**条目**）：文件 → 7-Zip 子菜单（含图标，验证 `WM_DRAWITEM` 转发）、发送到、属性；文件夹 → Git Bash Here/GUI Here、7-Zip、属性。
5. 原生菜单（**空白**）：Git Bash Here 在当前目录打开（验证 `%v`）、新建/粘贴/属性出现；**7-Zip 不出现**（本机注册如此，与资源管理器一致）。
6. 键盘：`F5`（浏览/根两态）、`Enter` 打开选中项、`Shift+F10` 与菜单键弹出对应菜单；`Esc` 仍收面板；`Backspace` 仍返回。
7. 多选：删除/复制路径可用，其余动作灰显；复制路径多行粘贴正确。
8. **未固定面板**：新建/重命名/删除过程中面板不收起；原生菜单打开时面板不收起；模态结束后面板仍停在同一目录。
9. 200% 缩放 / 多显示器 / 深浅主题：自建菜单位置与配色正常；原生菜单由系统绘制（深浅色不跟随，记录为限制）。
10. 回归：v1 浏览（进入/返回/路径菜单/空格预览/拖拽闸门）不受影响；根态全部旧菜单项行为不变 + 新增终端项；对照 `config.json` 前后确认浏览动作未写 `_data.Items`。
11. 失败路径：无权限目录里新建/删除 → 本地化错误提示，不崩溃；被占用文件删除失败 → 提示且文件仍在。

### 7.4 不可自检项

原生菜单是系统 UI，无自动化；互操作正确性靠手动验收 + §10 实施顺序中"先验证后接线"的那一步。

## 8. 风险

| 风险 | 处置 |
|---|---|
| 第三方 shell 扩展挂起 → 界面被拖住 | v1 接受（与资源管理器同款暴露面）；升级路径＝独立宿主进程 |
| 部分 `Directory\Background` 动词 `InvokeCommand` 无效（缺少完整 shell view site 的已知问题） | 实施第一步先用最小宿主验证 Git Bash Here（`%v` 生效）；若不生效，回退＝空白菜单降级为"当前目录的条目菜单"+自建动作，改动口径需用户确认 |
| 超大文件/卷无回收站 → 永久删除 | 固定卷预检 + 逐项调用 + `FOF_WANTNUKEWARNING`（系统显式确认）；绝不静默永久删除 |
| 原生菜单深浅色不跟随我们的主题 | 未公开 `uxtheme` API 可实测（`SetPreferredAppMode`），**不承诺**；自建菜单不受影响 |
| Win11 新式菜单 vs 旧式 | 我们拿到"显示更多选项"层；高频动作自建，不依赖系统菜单 |
| subst/映射盘回收站行为不一致 | 预检直接拒绝（§6.4） |
| cmd 回退对含 `%` 的目录名做变量展开 | 记录为已知限制（wt/ps 不受影响） |
| 模态对话框触发 `Deactivated` → 面板收起/浏览中断 | §6.6 护栏 + 验收第 8 条 |
| 互操作 COM 指针/句柄泄漏 | 全部 `finally` 释放；hook 会话期挂/摘（§5.7） |
| 依赖 site 的动词（共享等）灰显/无效 | v1 记录（§5.10）；如验收确认有问题，再评估最小 site |
| 原生菜单把第三方 shell 扩展载入本进程（一次性内存/句柄） | 2026-09-21 实测：文件条目菜单首次 ~6 MB / ~200 handles（7-Zip、Adobe 等），背景菜单 ~4 MB / ~155 handles；之后每次打开稳态 ~0 handles（§5 第 7 条显式释放）。长期方案仍是进程外宿主（§5.8） |

## 9. 本地化（新增 key，en / zh / ja 三语齐全，`LangCheck` 守护）

| Key | en | zh | ja |
|---|---|---|---|
| `Menu_OpenTerminal` | 💻 Open in Terminal | 💻 在终端中打开 | 💻 ターミナルで開く |
| `Menu_OpenInExplorer` | 📁 Open in Explorer | 📁 在资源管理器中打开 | 📁 エクスプローラーで開く |
| `Menu_ShowMoreOptions` | Show more options | 显示更多选项 | その他のオプションを表示 |
| `Menu_Refresh` | 🔄 Refresh | 🔄 刷新 | 🔄 更新 |
| `Menu_NewTextFile` | 📄 New Text Document | 📄 新建文本文档 | 📄 新しいテキスト ドキュメント |
| `Menu_DeleteItem` | 🗑️ Delete | 🗑️ 删除 | 🗑️ 削除 |
| `Dialog_NewTextFile_Title` | New Text Document | 新建文本文档 | 新しいテキスト ドキュメント |
| `Dialog_RecycleItem` | Move '{0}' to the Recycle Bin? | 将“{0}”移入回收站？ | 「{0}」をゴミ箱に移動しますか？ |
| `Dialog_RecycleItems` | Move {0} items to the Recycle Bin? | 将 {0} 个项目移入回收站？ | {0} 個の項目をゴミ箱に移動しますか？ |
| `Dialog_RecycleFailed` | {0} item(s) could not be moved to the Recycle Bin. | 有 {0} 项未能移入回收站。 | {0} 個の項目をゴミ箱に移動できませんでした。 |
| `Dialog_RecycleNotLocal` | Only items on local fixed drives can be moved to the Recycle Bin. | 只有本机固定磁盘上的项目才能移入回收站。 | ローカル固定ドライブ上の項目のみゴミ箱に移動できます。 |
| `Dialog_NameInvalid` | That name can't be used. | 该名称不可用。 | その名前は使用できません。 |
| `Dialog_NameExists` | An item with that name already exists. | 已存在同名的项目。 | 同じ名前の項目が既に存在します。 |
| `Dialog_CreateFailed` | Couldn't create the item: {0} | 无法创建：{0} | 作成できませんでした: {0} |
| `Dialog_RenameFailed` | Couldn't rename the item: {0} | 无法重命名：{0} | 名前を変更できませんでした: {0} |
| `Dialog_TerminalNotFound` | No terminal was found (Windows Terminal, PowerShell or cmd). | 未找到可用的终端（Windows Terminal / PowerShell / cmd）。 | 使用できるターミナルが見つかりません（Windows Terminal / PowerShell / cmd）。 |
| `UI_NewFolderDefault` | New Folder | 新建文件夹 | 新しいフォルダー |
| `UI_NewTextFileDefault` | New Text Document.txt | 新建文本文档.txt | 新しいテキスト ドキュメント.txt |

复用：`Menu_Open` / `Menu_OpenLocation` / `Menu_CopyPath` / `Menu_NewFolder` / `Dialog_NewFolder_Title` /
`Dialog_NewFolder_Prompt` / `Dialog_NewFile_Title` / `Dialog_NewFile_Prompt` / `Dialog_Confirm`。

## 10. 建议的实施顺序（供 writing-plans 展开）

1. `tests/ShellOpsCheck` + 纯逻辑（终端解析/转义、名称校验、删除预检与参数），先失败测试后实现。
2. `ShellFileOperations` 写操作 + `DialogFactory` 校验回调 + 浏览态新建/重命名/删除接入 + §6.6 模态护栏 + 第一批 key。
3. `TerminalLauncher` 启动 + `MenuActions` 自建菜单装配（条目/空白/多选灰显/键盘通道）+ 根态终端项 + 其余 key。
4. 原生菜单：**Step 1 先做最小验证**（面板内弹条目菜单与目录背景菜单；验证 Git Bash Here `%v`、7-Zip 子菜单与图标、消息转发），
   **Step 2 再接**"显示更多选项…"两处 + 失败回退 + 资源释放。
5. 手动验收 + README + 全量自检。

## 11. 本次复核的决策点（请确认）

- **D1 删除语义**：承诺改为"绝不静默永久删除"＋固定卷预检＋逐项调用＋系统 nuke 警告兜底（§6.4）。
- **D2 多选矩阵**：多选只开"删除到回收站 / 复制路径"，其余灰显（§3.1）。
- **D3 文本文档**：按输入原样命名，不自动补 `.txt`（§6.2）。
- **D4 根态**：不新增"在资源管理器中打开"（与既有"打开文件位置"重复），只加"在终端中打开"（§3.3）。
- **D5 原生菜单**：空白菜单用 `CreateViewObject` 取目录背景菜单；宿主用面板窗口自身（§5）。
- **D6 测试归属**：纯逻辑放新项目 `tests/ShellOpsCheck`，不塞进 `FolderListingCheck`（§4/§7.1）。

## 12. 自检记录（spec 复核后）

- **占位符扫描**：无 TBD/TODO；§6 各动作、§9 key、§7 验收都有唯一答案。
- **内部一致性**：§3.1 多选矩阵与 §6 各条一致；§6.4 承诺与 §8 风险表一致；§5 菜单来源与 §3.1/§3.2 入口一致；
  §7.3 验收第 4/5 条与 §5.9 本机注册表实测一致。
- **范围**：单个实施计划可覆盖（3 个新服务 + 1 个新 partial + 6 个既有文件小改（`Browse.cs`/`Interactions.cs`/
  `ContextMenuBuilder.cs`/`DialogFactory.cs`/`Localization.cs`/`README.md`）+ 1 个测试项目）。
- **歧义消除**：两处"显示更多选项…"的菜单语义、多选矩阵、刷新是否保留选择、文本文档是否补扩展名、
  删除失败口径、键盘锚点、模态期间面板行为——均已写明。
- **与草案差异**：见文首"复核修正"表（7 条）与 §11 决策点。

## 附录：上一轮（v1 浏览）遗留的小项，与本次无关，仅备查

页脚高度估算 20 vs 实际 ~23（被 `PADDING` 吸收）；`tests/FolderListingCheck` 缺 ACL 类用例（需造无权限目录）；
`DirectoryNotFoundException` 注释本身准确；页脚点击热区为文字宽度而非整行；头部/页脚拖放目标未标记拒绝
（锁定面板同样如此，属既有不对称）；面板关闭回调可加代际护栏；QuickLook 解析可显式指定 `RegistryView`；
`TextFormattingMode=Display` 在 1.2 缩放下字形先整数 hint 再放大（若观感发虚即源于此）。
