# Widget 文件操作增强（剪贴板 / 拖入子文件夹 / 新建文件对话框）实施计划

> **状态**：**已获用户批准（2026-10-12，对话逐条定案 + SVG 草图选定样式 1）**。本计划按"决策记录"口径执行。
> 执行方式：subagent-driven-development（每任务 = 实现子代理 TDD → 暂存不提交 → 控制者快照 + 审查包 →
> 修复循环 → 复审通过后由实现子代理按计划提交信息提交；冒烟由用户操作）。
> worktree：`.worktrees/widget-file-ops`；分支：`widget-file-ops`（基线 0db8ff3）。
> 设计草图：`docs/superpowers/plans/2026-10-12-widget-file-ops-newfile-dialog.svg`（选定样式 1 + 浅色变体另见图）。

**Goal：** folder widget 获得三组文件操作能力：①与资源管理器互通的文件剪贴板（复制/剪切/粘贴）；
②browse 内把条目拖入子文件夹（含 Ctrl 复制）；③"新建文件"改用名称 + 可编辑后缀选择器对话框（a 方案：
按输入名建空文件），并跟随组件颜色；④（可选收尾）拖到返回键 = 移回上级目录。

**Architecture：** 复用既有 shell 封装与拖放管线：`Services/ShellFileOperations` 增 `Copy`（FO_COPY）；
新 `Helpers/FileClipboard`（DataObject 载荷 + 快捷键解析，纯函数可 pin）；`Core/BrowseMove` 增落点守卫；
新 `Core/NewFileName`（名称+后缀合成）；`Helpers/DialogFactory` 增 `ShowNewFile`（样式 1，组件色覆盖
Accent 资源）；`Themes/Controls.xaml` 的 ComboBox 模板补可编辑支持；folder widget 侧新增
`Controls/FolderWidget.Clipboard.cs` partial 与条目级 DragOver/Drop。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；Newtonsoft.Json；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵）。

**Spec：** 无独立 spec；需求来源 = 2026-10-12 对话（含 SVG 草图与"按推荐定案"）与下方"决策记录"。

## Global Constraints

- C# 7.3 / net48，不引入新依赖；构建 0 警告。
- TDD：每任务先写失败自检（编译失败或运行时失败均可）→ 最小实现 → 相关自检与 LangCheck 全绿。
- 三语 key 同步（zh/en/ja 同一提交内）；`LangCheck` 保持绿；**死键三语同删**（本计划移除
  `Menu_NewTextFile`、`Dialog_NewTextFile_Title`、`UI_NewTextFileDefault`、`Dialog_NewFile_Prompt`）。
- 不新增自检工程；新 pin 进现有 check；`tools/Run-Checks.ps1` 无需改动。自检内**禁止读写系统剪贴板**
  （用 `DataObject` 直接构造）；真实文件操作只允许 `ShellOpsCheck` 既有的 %TEMP% 沙盒模式。
- 不驱动用户真实鼠标：交互以纯逻辑 pin + 反射调用 + 用户冒烟覆盖。
- **路径安全**：主面板（根模式）粘贴/拖入一律"只加引用、永不移动源文件"（与外部拖入既有语义一致）；
  browse 的真实移动/复制交给 shell（冲突/进度/撤销用 shell 原生行为）。
- **锁定面板一律拒绝**剪贴板与拖入（与"锁定 = 内容不可进出"一致）。
- 每个提交独立可编译、可回退；先任务审查后由实现子代理按 pathspec 提交。
- 全量矩阵 `Run-Checks.ps1 -Group all` 在整支终审前由控制者跑；推送/合并必须先问用户。
- 探针组件 `sdd-layout-probe` 保留勿动；`.worktrees/widget-plugins` 保留勿动。

## 决策记录（2026-10-12 用户确认）

1. **剪贴板**：browse 模式 Ctrl+C/X/V 全套（与 Explorer 双向互通，粘贴到当前目录）；主面板
   `Ctrl+C` = 把选中项发布为 FileDrop（可粘贴到 Explorer/其他面板），`Ctrl+V` = 把剪贴板文件加为引用；
   主面板不做 `Ctrl+X`；锁定面板全部拒绝。
2. **拖入子文件夹**：browse 拖源落到任意文件夹条目（同/跨面板均可）= 移动；按住 Ctrl = 复制；
   资源管理器外部拖入**不做**；主面板条目拖入 browse **不做**；锁定拒绝。
3. **新建文件（a 方案）**：对话框 = 名称框 + 可编辑后缀 ComboBox（常用表 10 项，默认 `.txt`，
   可自定义输入）+ 实时预览"将创建：xxx"；名称带点规则 = "去重 + 追加"；跟随组件颜色（Accent 资源
   覆盖 + 预览后缀着色 + 对话框边框 25% 淡染；高对比度下不覆盖系统色）；两入口共用；browse 菜单
   由"新建文本文档"改名"新建文件"；重名/非法名在框内报错（沿用 `ValidateName`）。
4. **④ 拖回上级**：以既有返回键（BackButton）为落点（当前无面包屑条），移动到父目录，作最后可选任务。
5. 顺序：T1 剪贴板内核 → T2 剪贴板接线 → T3 拖入子文件夹 → T4 新建文件（命名+对话框）→ T5 接线 →
   T6（可选）拖回上级 → T7 文档。

---

## Task F1: 剪贴板内核（shell Copy + FileClipboard 载荷/快捷键）

**Files：**
- Modify: `Services/ShellFileOperations.cs`（FO_COPY + `Copy` + `CopyFlags`）
- Create: `Helpers/FileClipboard.cs`
- Test: `tests/ShellOpsCheck/Program.cs`（Copy/flag pins）、`tests/XamlLoadCheck/Program.cs`（载荷 pins）

**Interfaces：**
- Produces: `ShellFileOperations.Copy(IList<string> paths, string targetFolder, IntPtr ownerHwnd) -> bool`；
  `ShellFileOperations.CopyFlags`（ushort）
- Produces: `Helpers.FileClipboard`：`CreatePayload(IEnumerable<string> paths, bool move) -> DataObject`、
  `PathsFrom(IDataObject) -> List<string>`、`IsMoveEffect(IDataObject) -> bool`、
  `Resolve(bool ctrl, bool shift, bool alt, bool browsing, bool locked, Key key) -> ClipboardAction`；
  `enum ClipboardAction { None, Copy, Cut, Paste }`；常量 `CopyEffect = 1`、`MoveEffect = 2`、
  `PreferredDropEffectFormat = "Preferred DropEffect"`

- [ ] **Step 1（红）：** 在 `tests/ShellOpsCheck/Program.cs` 写 pins：
  - `CopyFlags` 位含 `FOF_ALLOWUNDO(0x40)` 与 `FOF_RENAMEONCOLLISION(0x8)`；`FO_COPY == 0x0002`（内部常量经反射或公开常量）。
  - 沙盒复制：源文件在 `root`，`Copy(new[]{src}, root2, IntPtr.Zero)` 后 `root2` 有同名文件且源仍在；
    空列表返回 true；空白目标返回 false。
  在 `tests/XamlLoadCheck/Program.cs` 写 pins（**不碰系统剪贴板**）：
  - `CreatePayload(paths, move: true)` 的 `DataObject` 含 FileDrop 列表（顺序/去空）且
    `IsMoveEffect` 为 true；`move: false` 为 false；`PathsFrom` 还原列表。
  - `Resolve` 真值表：ctrl+C→Copy；ctrl+X→（browsing? Cut : None）；ctrl+V→Paste；
    无 ctrl / 带 shift 或 alt / locked → None。
  运行对应两个自检 → **编译失败**（新 API 不存在）即红，留档。

- [ ] **Step 2（实现）：**
  - `ShellFileOperations`：`internal const ushort FOF_RENAMEONCOLLISION = 0x0008;`
    `public const ushort CopyFlags = FOF_ALLOWUNDO | FOF_RENAMEONCOLLISION;`
    `private const uint FO_COPY = 0x0002;` + `public static bool Copy(...)`（复制 `Move` 的 SHFileOperation 结构，
    `wFunc = FO_COPY`，`fFlags = CopyFlags`；注释说明"粘贴冲突自动保留两份，不弹替换"）。
  - `Helpers/FileClipboard`：`CreatePayload` 用 `SetFileDropList` + `SetData(PreferredDropEffectFormat,
    new MemoryStream(BitConverter.GetBytes(move ? MoveEffect : CopyEffect)))`；读取端兼容 null/短流（返回 false / 空表）；
    `Resolve` 按 Step 1 的真值表实现（`Key.C/Key.X/Key.V`）。

- [ ] **Step 3（绿）：** `dotnet run --project tests\ShellOpsCheck\ShellOpsCheck.csproj -c Debug --nologo`、
  `dotnet run --project tests\XamlLoadCheck\XamlLoadCheck.csproj -c Debug --nologo` 全绿；
  `dotnet build Kobold.csproj -c Debug --nologo` 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(files): add shell copy and the file clipboard payload` →
  `git add Services/ShellFileOperations.cs Helpers/FileClipboard.cs tests/ShellOpsCheck/Program.cs tests/XamlLoadCheck/Program.cs`

---

## Task F2: 剪贴板接线（browse 键/菜单 + 主面板键/菜单 + 三语键）

**Files：**
- Create: `Controls/FolderWidget.Clipboard.cs`（partial：CopySelection / CutSelection / PasteHere / PasteIntoWidget）
- Modify: `Controls/FolderWidget.Interactions.cs`（`Window_KeyDown` 顶部接 `FileClipboard.Resolve` 分派）
- Modify: `Controls/FolderWidget.MenuActions.cs`（browse 条目菜单加 复制/剪切；browse 背景菜单与主面板空区菜单加 粘贴；主面板条目菜单加 复制）
- Modify: `Core/Localization.cs`（三语新增 `Menu_Copy`/`Menu_Cut`/`Menu_Paste`）
- Test: `tests/XamlLoadCheck/Program.cs`（菜单结构/禁用态 pins）

**Interfaces：**
- Consumes: F1 的 `FileClipboard`、`ShellFileOperations.Copy`
- Produces: `FolderWidget` 私有 `CopySelection(List<DisplayItem>, bool cut)`、`PasteHere()`、`PasteIntoWidget()`

- [ ] **Step 1（红）：** XamlLoadCheck pins：
  - 根模式 folder 的条目菜单含 `Menu_Copy`（header == `Localization.Get("Menu_Copy")`）；锁定时不出现或禁用。
  - browse 背景菜单含 `Menu_Paste`；主面板空区菜单含 `Menu_Paste`；browse 条目菜单含 `Menu_Copy` 与 `Menu_Cut`。
  - `Menu_Copy` 等 key 在 zh/en/ja 均存在（也可由 LangCheck 覆盖）。
  运行 → 失败（菜单项缺失；若引用新 key 则先编译失败）。留档。

- [ ] **Step 2（实现）：**
  - `FolderWidget.Clipboard.cs`：
    - `CopySelection(items, cut)`：`items.Count == 0 || _data.IsLocked` 直接返回；`Clipboard.SetDataObject(
      FileClipboard.CreatePayload(paths, cut), true)`（catch 异常 → `Debug.WriteLine`，不弹窗）。
    - `PasteHere()`（browse）：`Clipboard.GetDataObject()` → `PathsFrom`；空则返回；`IsMoveEffect` →
      `BrowseMove.MovableInto(paths, CurrentBrowsePath, exists)` + `ShellFileOperations.Move`；
      否则 `ShellFileOperations.Copy(paths, CurrentBrowsePath, hwnd)`；`Dispatcher.BeginInvoke(UpdateUI)`。
    - `PasteIntoWidget()`（根模式）：`AddExternalPaths(paths.ToArray())`（只加引用，去重既有）。
  - `Window_KeyDown`：在 `HandleMenuActionKey` 之前 `var action = FileClipboard.Resolve(ctrl, shift, alt,
    IsBrowsing, _data.IsLocked, e.Key);` 非 None 时执行对应动作并 `e.Handled = true`。
  - 菜单：`ShowBrowseItemMenu` 在 `Menu_CopyPath` 后加 `Menu_Copy`/`Menu_Cut`（`isEnabled` = 未锁定且选择非空）；
    `ShowBrowseBackgroundMenu` 在新建两条后加 `Menu_Paste`；`ShowPanelContextMenu` 加 `Menu_Paste`；
    `ShowRootItemMenu` 在 `copyPathItem` 后加 `Menu_Copy`（未锁定时）。
  - `Localization.cs` 三语：`Menu_Copy` = "📄 Copy"/"📄 复制"/"📄 コピー"；
    `Menu_Cut` = "✂ Cut"/"✂ 剪切"/"✂ 切り取り"；`Menu_Paste` = "📋 Paste"/"📋 粘贴"/"📋 貼り付け"。

- [ ] **Step 3（绿）：** XamlLoadCheck + `tests\LangCheck` + `tests\MenuRenderCheck` 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(files): wire the file clipboard into both modes` →
  `git add Controls/FolderWidget.Clipboard.cs Controls/FolderWidget.Interactions.cs Controls/FolderWidget.MenuActions.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`

---

## Task F3: 拖入子文件夹（条目级移动/复制 + 守卫）

**Files：**
- Modify: `Core/BrowseMove.cs`（`CanDropInto` 守卫）
- Modify: `Controls/FolderWidget.DragDrop.cs`（条目级 DragOver/DragLeave/Drop + `BrowseDragPaths` 助手）
- Modify: `Controls/FolderWidget.xaml`（`ItemBorder`：`AllowDrop="True"` + 三个事件）
- Test: `tests/FolderListingCheck/Program.cs` 或 `tests/ShellOpsCheck/Program.cs`（守卫 pins）、`tests/XamlLoadCheck/Program.cs`（条目 Drop 行为 pin）

**Interfaces：**
- Produces: `BrowseMove.CanDropInto(IEnumerable<string> paths, string targetFolder, Func<string, bool> isDirectory) -> bool`
  （目标必须存在且是目录；每个 path ≠ 目标路径、且目标不在 path 子树内；空列表/空目标 false）
- Produces: `FolderWidget.BrowseDragPaths(IDataObject) -> List<string>`（读 `BrowseDragPathsFormat`）

- [ ] **Step 1（红）：** pins：
  - `CanDropInto`：正常文件→子目录 true；目标不存在 false；目标是文件 false；path==目标 false；
    文件夹拖进自身子树 false（大小写/尾分隔符变体）；空表 false。
  - XamlLoadCheck：browse 模式对"文件夹条目"合成 DragOver（构造 `DataObject` 带 `BrowseDragPathsFormat`
    + `BrowseDragSourceFormat`，反射调用条目 handler 或 `ItemsContainer_DragOver`）→ Effects=Move；
    Ctrl 变体 → Copy；对文件条目 → None；锁定 → None。
  运行 → 编译失败（CanDropInto 不存在）即红，留档。

- [ ] **Step 2（实现）：**
  - `BrowseMove.CanDropInto`：基于 `Path.GetFullPath` 归一化 + `OrdinalIgnoreCase`；子树判定
    `IsUnder(target, path)`（`path + 分隔符` 前缀，`Path.GetFullPath` 失败按 false）。
  - `FolderWidget.DragDrop.cs`：
    - `BrowseItem_DragOver/DragLeave/Drop`（sender=ItemBorder，`DataContext as DisplayItem`）：
      `IsBrowsing && !_data.IsLocked && item.IsDirectory && IsBrowseDrag(...) && CanDropInto(...)`；
      Ctrl（`DragDropKeyStates.ControlKey`）→ Copy 否则 Move；高亮目标条目（`BorderBrush = Accent`，离开清除）；
      拒绝时 `DragDropSession.Current?.MarkTargetRefused()`。
    - Drop：`movable = BrowseMove.MovableInto(paths, target.Path, exists)`；Copy → `ShellFileOperations.Copy`，
      否则 `Move`；`Dispatcher.BeginInvoke(UpdateUI)`。
    - 容器 `ItemsContainer_DragOver`：光标在条目上时由条目 handler 接管（ItemBorder AllowDrop + Handled）；
      背景逻辑不变。
  - `FolderWidget.xaml`：`ItemBorder` 增 `AllowDrop="True"` 与三个事件（模板共享，root 模式由 handler 内部
    `IsBrowsing` 拒绝保持现有重排/合并行为）。

- [ ] **Step 3（绿）：** 新增守卫 pins 的 check + XamlLoadCheck 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(files): drop items into subfolders while browsing` →
  `git add Core/BrowseMove.cs Controls/FolderWidget.DragDrop.cs Controls/FolderWidget.xaml tests/FolderListingCheck/Program.cs tests/XamlLoadCheck/Program.cs`

---

## Task F4: 新建文件内核（命名逻辑 + 可编辑 ComboBox + ShowNewFile 对话框）

**Files：**
- Create: `Core/NewFileName.cs`
- Modify: `Themes/Controls.xaml`（ComboBox 模板支持 `IsEditable`）
- Modify: `Helpers/DialogFactory.cs`（`ShowNewFile`，样式 1）
- Test: `tests/ShellOpsCheck/Program.cs`（命名 pins）、`tests/XamlLoadCheck/Program.cs`（可编辑模板 pin）

**Interfaces：**
- Produces: `NewFileName.CommonExtensions`（`.txt .md .json .csv .log .html .xml .ini .bat .ps1`）、
  `DefaultExtension = ".txt"`、`NormalizeExtension(string) -> string`（空→""；非法字符→null 表示错误）、
  `Compose(string name, string extension) -> string`（去重 + 追加）
- Produces: `DialogFactory.ShowNewFile(Window owner, string title, string nameLabel, string extLabel,
  string previewFormat, string initialName, string initialExtension, IReadOnlyList<string> extensions,
  string widgetColor, Func<string,string> validate) -> string`（取消返回 null）

- [ ] **Step 1（红）：** pins：
  - `NormalizeExtension("md") == ".md"`、`".MD" == ".md"`（小写归一）、`" .tx-t "? → ".tx-t"`（trim + 允许 `-`）、
    空/全点 → ""、含非法字符（空格/`/`/`*`）→ null。
  - `Compose("报告", ".md") == "报告.md"`；`Compose("报告.md", ".md") == "报告.md"`（去重，忽略大小写）；
    `Compose("报告.md", ".txt") == "报告.md.txt"`；`Compose("报告", "") == "报告"`。
  - XamlLoadCheck：可编辑 ComboBox ApplyTemplate 后存在 `PART_EditableTextBox` 且 `IsEditable=false` 时不显示
    （非可编辑既有行为不变：SelectionBoxItem 仍渲染）。
  运行 → 编译失败（NewFileName 不存在）即红，留档。

- [ ] **Step 2（实现）：**
  - `Core/NewFileName.cs`：按 Step 1 语义；`Compose` 判尾缀用 `name.EndsWith(ext,
    StringComparison.OrdinalIgnoreCase)`；`NormalizeExtension` 逐字符校验 `char.IsLetterOrDigit(c) || c == '-'`。
  - `Themes/Controls.xaml`：模板增 `TextBox x:Name="PART_EditableTextBox" Visibility="Collapsed"`（透明背景、
    无边框、`Padding="{TemplateBinding Padding}"` 右侧留 28 给箭头、`Text="{Binding Path=Text,
    RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"`、CaretBrush=Accent）；
    Trigger `IsEditable=True` → 隐藏原 ContentPresenter、显示该 TextBox；非可编辑路径零变化。
  - `DialogFactory.ShowNewFile`（样式 1，宽 380）：标题、两标签行、`TextBox`（名称，默认全选）+
    可编辑 `ComboBox`（下拉项 = 常用后缀；`IsEditable=true`）；预览 `TextBlock`：`previewFormat` 格式化，
    后缀 Run `Foreground = 组件色`；`TextChanged`/`SelectionChanged` 重算 `Compose` 预览；OK 时先
    `NormalizeExtension`（null → 框内错误）再 `validate(composed)` 错误显示在框内（沿用 `DialogFactory.ShowInput`
    的 errorText 模式）；Enter=确定、Esc=取消。
    组件色：`if (!ThemeManager.IsHighContrast)` 时 `dialog.Resources[ThemeManager.BrushKeyPrefix+"Accent"] =
    组件色`、`AccentHover = Utils.DarkenColor(组件色, 0.2)`、`OnAccent = 白/黑按亮度`；边框 =
    `Utils.MixColor(ThemeManager.DialogBorderBrush.Color, 组件色, 0.25)`（与菜单 25% 淡染同口径）。

- [ ] **Step 3（绿）：** ShellOpsCheck 命名 pins + XamlLoadCheck（模板 pin + 既有 combo 相关 pin 不回归）全绿；
  build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(files): add the new-file naming core and dialog` →
  `git add Core/NewFileName.cs Themes/Controls.xaml Helpers/DialogFactory.cs tests/ShellOpsCheck/Program.cs tests/XamlLoadCheck/Program.cs`

---

## Task F5: 新建文件接线（两入口 + 菜单改名 + 三语键重整）

**Files：**
- Modify: `Controls/FolderWidget.Interactions.cs`（`CreateNewFile` → `ShowNewFile`，storage 目录 + 校验器）
- Modify: `Controls/FolderWidget.MenuActions.cs`（`CreateBrowseTextFile` → `ShowNewFile`）
- Modify: `Core/Localization.cs`（新增 5 键；删除 4 死键，三语同步）
- Test: `tests/XamlLoadCheck/Program.cs`（两入口 pin）、`tests/LangCheck`（自动）

**Interfaces：**
- Consumes: F4 的 `ShowNewFile`、`NewFileName`
- 新增 key：`UI_NewFileDefault`（默认名称）、`Dialog_LabelFileName`、`Dialog_LabelExtension`、
  `Dialog_WillCreateFormat`（"将创建：{0}"）、`Dialog_ExtCustomHint`
- 删除 key（三语同删，确认无引用）：`Menu_NewTextFile`、`Dialog_NewTextFile_Title`、
  `UI_NewTextFileDefault`、`Dialog_NewFile_Prompt`

- [ ] **Step 1（红）：** XamlLoadCheck pins：
  - browse 背景菜单第二项 header == `Localization.Get("Menu_NewFile")`（改名后；当前是 `Menu_NewTextFile` → 运行时红）。
  - 根模式调私有的新建入口（反射，注入假输入不现实 → 改为 pin `NewFileName.Compose` 与校验器接线：
    断言 `CreateNewFile` 源码路径不可测——故 pin 改为：`Menu_NewFile` 存在且两菜单首项 header 一致）。
  运行 → 红（`Menu_NewFile` 不存在于 browse 菜单），留档。

- [ ] **Step 2（实现）：**
  - `CreateNewFile()`：`ShowNewFile(this, Localization.Get("Dialog_NewFile_Title"),
    Localization.Get("Dialog_LabelFileName"), Localization.Get("Dialog_LabelExtension"),
    Localization.Get("Dialog_WillCreateFormat"), Localization.Get("UI_NewFileDefault"),
    NewFileName.DefaultExtension, NewFileName.CommonExtensions, _data.Color,
    n => NameErrorText(ShellFileOperations.ValidateName(n, Utils.GetStoragePath(), PathExists)))`；
    返回非 null → 在 storage 建空文件（沿用 `File.WriteAllText` + 条目添加）。
  - `CreateBrowseTextFile(directory)`：同对话框（校验器按 `directory`），`ShellFileOperations.CreateTextFile`
    沿用；菜单项 key `Menu_NewTextFile` → `Menu_NewFile`。
  - `Localization.cs`：新增键三语（`UI_NewFileDefault` = "New File"/"新建文件"/"新規ファイル"；
    `Dialog_LabelFileName` = "File name"/"文件名"/"ファイル名"；`Dialog_LabelExtension` = "Extension"/"扩展名"/"拡張子"；
    `Dialog_WillCreateFormat` = "Will create: {0}"/"将创建：{0}"/"作成: {0}"；
    `Dialog_ExtCustomHint` = "Custom extensions allowed"/"可直接输入自定义后缀"/"拡張子は直接入力可"）；
    删 4 死键并确认无引用（grep）。

- [ ] **Step 3（绿）：** XamlLoadCheck + LangCheck + MenuRenderCheck 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(files): create files through the new-name dialog` →
  `git add Controls/FolderWidget.Interactions.cs Controls/FolderWidget.MenuActions.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`

---

## Task F6（可选）: 拖到返回键 = 移回上级

**Files：**
- Modify: `Controls/FolderWidget.xaml`（`BackButton`：`AllowDrop="True"` + DragOver/DragLeave/Drop）
- Modify: `Controls/FolderWidget.DragDrop.cs`（BackButton 三个 handler，复用 F3 的守卫/操作与高亮）
- Test: `tests/XamlLoadCheck/Program.cs`（合成 DragOver → Effects = Move；根目录时 None）

- [ ] **Step 1（红）：** pin：browse 到 `root\sub`，父目录 = `root`；带 `BrowseDragSourceFormat=sub` 的
  DataObject 对 BackButton 合成 DragOver → Effects=Move；`CurrentBrowsePath` 为盘根（父为 null）→ None。
  运行 → 红（BackButton 无 AllowDrop/handler），留档。
- [ ] **Step 2（实现）：** handler 目标 = `Path.GetDirectoryName(CurrentBrowsePath)`；守卫与操作复用 F3；
  高亮 BackButton。
- [ ] **Step 3（绿）：** XamlLoadCheck 全绿。
- [ ] **Step 4（提交，1 笔）：** `feat(files): drop items on the back button to move them up` →
  `git add Controls/FolderWidget.xaml Controls/FolderWidget.DragDrop.cs tests/XamlLoadCheck/Program.cs`

---

## Task F7: 文档（README 英中 + CHANGELOG Unreleased）

- [ ] README（英中同步）：剪贴板（browse Ctrl+C/X/V 与资源管理器互通；主面板 Ctrl+C=发布、Ctrl+V=加引用）、
  拖入子文件夹（Ctrl=复制）、新建文件对话框（名称 + 后缀选择器 + 实时预览；按输入名建空文件；跟随组件色）。
- [ ] CHANGELOG「Unreleased」：三条功能 + 一句"主面板粘贴沿用只加引用语义"。
- [ ] 提交 → `docs: document the file operations round`

---

## 收尾（控制者执行，非任务）

- [ ] 整支终审（requesting-code-review）：diff `0db8ff3..HEAD` 全量；重点：锁定拒绝一致性、路径安全
  （主面板只加引用）、隧道/冒泡不吞既有拖放、三语无死键。
- [ ] `tools\Run-Checks.ps1 -Group all`（31 行）全绿；config-pin 误报先重跑。
- [ ] 计划文件追加"执行记录"，提交 `docs: record the file operations execution`。
- [ ] `tools\deploy.ps1` → **用户手动冒烟**（browse 复制/剪切/粘贴与 Explorer 双向、拖入子文件夹 +
  Ctrl 复制、拖回上级、新建文件对话框三语/组件色/重名报错、锁定面板拒绝）。
- [ ] 用户确认后：合并 main + 推送（需用户拍板）；worktree/SDD 工作区按收尾规范清理。
