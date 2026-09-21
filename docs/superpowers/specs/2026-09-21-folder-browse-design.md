# Kobold 面板内文件夹浏览（Folder Browse）设计

> **状态**：设计已与用户确认（2026-09-21），待复核后进入实施计划。
> **前置约定**：双击文件夹 = 在组件面板内就地浏览，不再唤起资源管理器。浏览是
> **只读视图**：不改动 `_data.Items`，不提供任何文件写操作。

## 目标

在现有组件面板里增加第二个内容模式：**浏览模式**。双击组件里的文件夹条目后，
面板内容替换为该文件夹的子文件夹与文件；可逐级下钻、可返回；回到根即恢复组件内容。

成功标准：
- 双击文件夹不再打开资源管理器，而是在面板内看到内容。
- 任意深度下钻与逐级返回都不丢失组件原有内容（根态条目一个不少）。
- 浏览态不做任何可能改坏磁盘文件的操作。

## 非目标（v1 明确不做）

- 浏览态的文件写操作：重命名 / 删除 / 移动 / 新建。
- 浏览态的拖拽：拖出、跨组件移动、从资源管理器拖入。
- 可点击的面包屑、前进历史、地址栏、搜索过滤。
- `FileSystemWatcher` 实时刷新。
- Shell 右键菜单（真正的资源管理器上下文菜单）。
- 组件面板自身的内存优化：另立任务，本设计的「内存约束」只约束本次新增代码。

## 架构

### 新增：`Core/FolderListing.cs`（纯逻辑，无 UI 依赖）

与 `Core/WidgetItems.cs` 同样的定位：可被 `tests/*Check` 控制台自检直接覆盖。

```csharp
public sealed class BrowseEntry
{
    public string Name { get; set; }
    public string Path { get; set; }
    public bool IsDirectory { get; set; }
}

public sealed class ListingResult
{
    public List<BrowseEntry> Entries { get; set; }   // 已排序、已截断
    public int TotalCount { get; set; }              // 过滤后的总数（截断前）
    public bool Truncated { get; set; }              // TotalCount > Entries.Count
    public bool Failed { get; set; }                 // 目录整体不可访问
}

public static ListingResult ListChildren(string directory, int maxEntries);
```

枚举与排序规则：

1. 先枚举子目录、再枚举文件，**两类各自排序后拼接**（目录永远在前）。
2. 排序用 Windows 自然排序（`shlwapi!StrCmpLogicalW`，与资源管理器一致）；
   调用不可用时回退 `StringComparer.OrdinalIgnoreCase`。
3. 过滤掉带 `Hidden` 或 `System` 属性的条目（与资源管理器默认一致）。
4. 单个条目读取属性失败 → 跳过该条目，不影响整体。
5. 目录不可访问（`UnauthorizedAccessException` 等）→ `Failed = true`，`Entries` 为空。
6. 截断：排序后按序取前 `maxEntries` 条（目录天然排在前，因此目录优先保留），
   其余条目只计数不返回；`TotalCount` 始终是过滤后的真实总数。
7. `BrowseEntry.Name` 是原始文件系统名，**不做显示名改写**；显示名仍由 `FolderWidget`
   现有的 `GetDisplayName(path)` 统一处理（去掉 `.lnk` 扩展名）。

### 修改：`Controls/DisplayItem.cs`

新增一个字段（决定双击行为，根态与浏览态共用）：

```csharp
public bool IsDirectory { get; set; }
```

根态的 `IsDirectory` 复用 `UpdateUI()` 里已有的存在性检查（当前为
`IsMissing = !File.Exists && !Directory.Exists`），不额外增加文件系统调用。

### 修改：`Controls/FolderWidget.xaml.cs`（浏览状态 + 数据源切换）

```csharp
private readonly List<string> _browseStack = new List<string>();   // 空 = 根态
private string CurrentBrowsePath => _browseStack.Count == 0 ? null : _browseStack[_browseStack.Count - 1];

public void EnterFolder(string path);   // 压栈 + UpdateUI
public void GoBack();                   // 弹栈（根态无操作）+ UpdateUI
private void ResetBrowse();             // 清栈（面板隐藏时调用）
```

`UpdateUI()` 的数据源二选一：

- 根态（`CurrentBrowsePath == null`）：现有逻辑，来自 `_data.Items`（**不改动**）。
- 浏览态：`FolderListing.ListChildren(CurrentBrowsePath, MAX_BROWSE_ENTRIES)` 映射为
  `DisplayItem`（`IsStored`/`IsMissing` 不适用，保持 false）。

`UpdateUI()` 同时负责：标题文本（根态 = 组件名；浏览态 = 当前文件夹名，Tooltip = 完整路径）、
返回按钮可见性、空态文案（空目录 / 不可访问）、"还有 N 项"页脚、面板高度上限与滚动。

### 修改：`Controls/FolderWidget.xaml`

- 标题栏新增返回按钮：`[←][标题……][?][锁][钉]`（新列插在最左，仅浏览态可见，
  复用现有 24×24 / `Kobold.Brush.Icon*` 的按钮样式与 `Cursor=Hand`）。
  **点击处理必须 `e.Handled = true`**，与现有的锁/钉/帮助按钮一致，否则会触发标题栏拖拽。
- `ItemsContainer` 外包 `ScrollViewer`（`VerticalScrollBarVisibility=Auto`，
  `HorizontalScrollBarVisibility=Disabled`，背景透明以保留套索选择的空白命中区）。
  **层级顺序固定**：`ScrollViewer` 在外层，现有 `LayoutTransform` 仍留在 `ItemsContainer`
  上——滚动条本身不被缩放，而滚动范围按缩放后的内容尺寸计算。
- 新增"还有 N 项"页脚 `TextBlock`（固定在滚动区之外的内容区底部，仅截断时可见，
  文案走 `Localization.Format`，点击用资源管理器打开当前目录）。

### 不再需要

`Controls/FolderWidget.Interactions.cs::Item_DoubleClick` 是死代码（XAML/代码都未接线），
本次一并删除，避免与新的双击分支混淆。

## 交互规格

| 场景 | 行为 |
|---|---|
| 根态双击文件夹条目 | 进入该文件夹（不再 `Process.Start`） |
| 根态双击文件条目 | 保持现状：shell 打开 |
| 浏览态双击子文件夹 | 继续下钻 |
| 浏览态双击文件 | shell 打开（与根态一致） |
| 缺失条目（`IsMissing`） | 保持现状：交给 shell（会弹系统错误），不做特殊处理 |
| 返回按钮 / `Backspace` | 返回上一级；根态无操作（按钮隐藏） |
| `Esc` | **保持现状：收起面板**（不改肌肉记忆；返回用返回按钮/Backspace） |
| 浏览态右键条目 | 只读三项：打开 / 在资源管理器中显示 / 复制路径 |
| 根态右键条目 | 保持现状（含收纳 / 移出 / 重命名 / 移出组件） |
| 浏览态条目拖拽 | 禁用：不启动 `DoDragDrop` |
| 浏览态接受拖入 | 禁用：`DragOver`/`Drop` 直接 `e.Effects = None` 且不处理 |
| 浏览态套索选择 | 保留（纯视觉，无后续动作） |
| 面板收起（失焦/切 tile/`HidePanel`） | 清空浏览栈，下次打开回到根态 |

## 尺寸与滚动

- 数据源行数按当前模式计算（根态 = `_data.Items.Count`，浏览态 = 返回条目数）。
- **高度上限 = `SystemParameters.WorkArea.Height × 0.6`**；超出则面板取上限高度、内容区
  内部滚动。宽度规则不变（列数 × 条目宽），不做横向滚动。
- 目录条目上限 `MAX_BROWSE_ENTRIES = 200`（见 `FolderListing`），超出显示"还有 N 项未显示"。
- `MAX_BROWSE_ENTRIES`、`MAX_BROWSE_ICON_LOADS`、面板高度比例系数放进
  `Core/WidgetConstants.cs`（与既有常量同处），不散落在控件代码里。
- 该上限对根态同样生效：组件条目过多时面板不再无限增高（属行为改进，纳入验收）。

## 内存约束（约束本次新增代码）

- 浏览态只为**前 `MAX_BROWSE_ICON_LOADS`（60）个条目**异步加载 shell 图标（其余条目使用
  通用文件/文件夹图标），避免"大目录 × 每项一次 `SHGetFileInfo` + 一个 `BitmapSource`"。
- 不新增任何常驻定时器、事件监听或 `FileSystemWatcher`。
- 离开浏览态 / 面板收起时清空条目列表，让大目录产生的条目对象与图标引用可回收。
- 复用现有 `GetFileIcon` 的缓存策略（文件夹共用 `::folder::` key，开销接近零）。

## 本地化

新增 key（en / zh / ja 三语齐全，`tests/LangCheck` 守护）：

| Key | 用途 |
|---|---|
| `UI_BrowseBack` | 返回按钮 Tooltip |
| `UI_FolderEmpty` | 空文件夹的空态文案 |
| `UI_FolderAccessDenied` | 无法访问目录的空态文案 |
| `UI_BrowseMoreItems` | "还有 {0} 项未显示，点击在资源管理器中打开"（格式化） |

复用现有 `Menu_Open` / `Menu_OpenLocation` / `Menu_CopyPath` / `UI_DropHere`。

## 测试

- 新增 `tests/FolderListingCheck`（控制台自检，退出码 0 = 全绿）：
  目录优先于文件；自然排序（`file2` < `file10`，中文名稳定）；隐藏/系统项被过滤；
  截断时 `TotalCount`/`Truncated` 正确且目录优先保留；不存在的目录返回 `Failed`；
  空目录返回 0 条且 `Failed = false`。
- 既有 `tests/XamlLoadCheck` 必须继续通过（XAML 结构改动）。
- 既有 `tests/LangCheck` 必须继续通过（新增三语文案）。
- 手动验收（构建后）：
  1. 双击组件里的文件夹 → 面板内显示内容，无资源管理器窗口。
  2. 连下钻 3 层并逐级返回 → 回到根态，组件条目与角标完好。
  3. 打开一个 500+ 条目的目录 → 面板不超高、内部滚动、"还有 N 项"出现。
  4. 打开一个无权限目录（如 `C:\System Volume Information`）→ 空态提示，不崩溃。
  5. 浏览态右键 → 只出现只读三项；拖拽无反应。
  6. 收起面板再打开 → 回到根态。

## 边界与已确认行为

- **同一路径既是组件条目又是浏览条目**：两者独立，互不写回（用户已确认）。
- **已收纳文件**：物理位置已在 Kobold 存储，浏览其原目录时不会出现（预期行为）。
- **junction / 符号链接**：允许无限下钻，与资源管理器一致，不设深度上限。
- **组件锁定（`IsLocked`）**：浏览仍允许（只读无风险）；锁定只约束内容的移入移出。
- **浏览位置不持久化**：不写入 `config.json`，重启回到根态。

## 风险与已知限制

- `ScrollViewer` 会把内容区坐标系统从"面板"改为"可滚动视口"：拖拽插入指示线
  （`DropIndicator`）与套索选择框（`SelectionRect`）目前是 `Grid.Row 1` 的兄弟元素、
  依赖相对面板的坐标。实施时把这两个覆盖层一并放进滚动内容层（或按
  `ScrollViewer.VerticalOffset` 修正坐标），并在根态回归验证拖拽/套索。
- 拖拽到面板边缘时**不自动滚动**（v1 限制，记录在案）。
- 面板高度上限改变了根态的现有行为（组件条目很多时从"无限增高"变为"内部滚动"），
  属于有意为之的改进。

## 实施顺序（供 writing-plans 展开）

1. `Core/FolderListing.cs` + `tests/FolderListingCheck`（先失败测试，后实现）。
2. `DisplayItem.IsDirectory` + 双击分支改造（根态文件夹 → 进入）+ 死代码清除。
3. 标题栏返回按钮 + 浏览栈 + `UpdateUI()` 数据源切换 + 空态与页脚文案。
4. `ScrollViewer` + 高度上限 + 目录条目上限与图标加载上限。
5. 浏览态只读右键菜单 + 禁用拖拽。
6. 三语文案 + `LangCheck`/`XamlLoadCheck` + 手动验收清单。
