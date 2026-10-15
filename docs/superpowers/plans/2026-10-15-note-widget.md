# 笔记组件（Note Widget）与组件框架收口实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **状态：已获用户批准（2026-10-15：spec 审阅通过，追加"右键快速插入 + 表格"后定稿）**。按"决策记录"口径执行。
> 执行方式：subagent-driven-development（每任务 = 实现子代理 TDD → 暂存不提交 → 控制者快照 + 审查包 → 修复循环 →
> 复审通过后由实现子代理按计划提交信息提交；冒烟由用户操作）。
> worktree：`.worktrees/note-widget`；分支：`note-widget`（基线 = 本计划 `docs(plans)` 提交在 main 的 SHA）。
> Spec：`docs/superpowers/specs/2026-10-15-note-widget-design.md`（§0 九条决策记录；本计划不再复述决策，只引 §号）。

**Goal：** 交付第三种组件 **笔记**（kind `note`）：列表（自动标题/时间/摘要）→ 阅读（Markdown 渲染 + 可交互任务框）→ 编辑（源码 + 预览 + 右键快速插入）三态；同轮把 kind 接线的四处机械分发（转换器 / 窗口工厂 / 图标分发 / 尺寸迁移）收口为注册表驱动。

**Architecture：** 纯逻辑进 `Core/`（`NoteData`、`NoteOps`、`NoteEditorOps`、`MarkdownParser` + AST），零新依赖；WPF 接线进
`Controls/`（`NoteWidget` 薄壳 + `NotePanelView` 三态 + `NoteMarkdownRenderer` + `NoteIconStyles`）；注册表两处新增
（`WidgetKind.DataType`、`WidgetWindowFactory`）。所有新行为以既有自检工程的离屏 pin 覆盖（反射调用 + 合成键盘，不驱动真实鼠标、不读写剪贴板）。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵 31 → 32 行）。

## Global Constraints

- C# 7.3 / net48，不引入新依赖；构建 0 警告。
- TDD：每任务先写失败 pin（编译失败或运行时失败均可）→ 最小实现 → 相关自检与 LangCheck 全绿。
- 三语 key 同步（zh/en/ja 同一提交内）；`LangCheck` 保持绿；新增 key 必须同任务使用（死键规则）。
- 第三四组任务（T1-T4）是**零行为变化**的收口重构：WidgetKindsCheck 与 XamlLoadCheck 的既有 pin 全部保持绿。
- hex 字面量只许在 `Core/UiTokens.cs`（`UiTokensCheck` 会抓）；笔记图标与渲染不新增颜色值，一律走
  `UiTokens` / `ThemeManager` / 组件色。
- 自检内**禁止读写系统剪贴板**（快速插入菜单的四个标准项只 pin 命令绑定，绝不执行）；**禁止驱动真实鼠标**
  （离屏 pin + 反射 + 合成键盘，参照 `RaiseKey` / `InvokeViewEdit` 既有助手）。
- 笔记时间戳夹具一律显式传入（如 `2026-10-15 09:30`）；断言不得依赖 `DateTime.Now`（仅允许 `ModifiedAt >= 测试开始时刻` 这类比较）。
- 每个提交独立可编译、可回退；**先任务审查后由实现子代理按 pathspec 提交**。推送/合并必须先问用户。
- 既有环境事项：`MenuRenderCheck` 曾为观察项，v1.4.0 轮后稳定绿；若复发且非本分支回归，如实记录。
- 新自检工程 `NoteMarkdownCheck` 必须登记进 `tools/Run-Checks.ps1` 矩阵（`Ci = $true`，纯逻辑）。

---

## Task T1: 转换器注册表化（Kind → 数据类型）

**Files：**
- Modify: `Core/WidgetKindRegistry.cs`（`WidgetKind` 增 `DataType`）
- Modify: `Core/WidgetDataConverter.cs`（ReadJson / KindOf 改查注册表）
- Test: `tests/WidgetKindsCheck/Program.cs`

**Interfaces：**
- Produces: `WidgetKind.DataType`（`Type` 字段）；转换器读路径 = `WidgetKindRegistry.Find(kind)` → `raw.ToObject(entry.DataType, payloadSerializer)` → `data.Kind = kind`；写路径 `KindOf` = 反向查 `entry.DataType == data.GetType()` → `entry.Id`，兜底 `data.Kind`。
- 兼容：`Kind` 缺失/空 → 先归一为 `"folder"` 再走注册表；未登记 kind → `UnknownData`（原行为）。

- [ ] **Step 1（红）：** `tests/WidgetKindsCheck/Program.cs` 加：
  - `registry: the folder kind carries its data type` / `the todo kind carries its data type`：`folder.DataType == typeof(FolderData)`、`todo.DataType == typeof(TodoData)`（现编译失败即红）。
  - `converter: the registered type drives the load`：手写 `{"Kind":"todo", ...}` 夹具 → `AppConfig.Load` → `is TodoData` 且 `Kind == "todo"`（先有行为后 pin 会直接绿——两条新 pin 至少一红：Type 字段不存在）。
  运行 `dotnet run --project tests\WidgetKindsCheck\WidgetKindsCheck.csproj -c Debug --nologo` → 编译失败（CS0117：无 `DataType`）即红。留档。

- [ ] **Step 2（实现）：**
  - `WidgetKind` 增：
    ```csharp
    /// <summary>The concrete data type the converter instantiates for this kind.</summary>
    public Type DataType;
    ```
    folder 条目加 `DataType = typeof(FolderData),`、todo 加 `DataType = typeof(TodoData),`（放在 `IconKey` 之后）。
  - `WidgetDataConverter.ReadJson`：把 folder/todo 两个 if 块替换为：
    ```csharp
    if (string.IsNullOrEmpty(kind)) kind = "folder";
    var entry = WidgetKindRegistry.Find(kind);
    if (entry == null) return new UnknownData { Kind = kind, Raw = raw };
    var data = (WidgetData)raw.ToObject(entry.DataType, CreatePayloadSerializer());
    data.Kind = kind;
    return data;
    ```
  - `KindOf`：
    ```csharp
    foreach (var entry in WidgetKindRegistry.All)
    {
        if (entry.DataType == data.GetType()) return entry.Id;
    }
    return data.Kind;
    ```
  - 类注释更新一句：读写的类型映射来自 `WidgetKindRegistry`。

- [ ] **Step 3（绿）：** WidgetKindsCheck 全绿（既有 85+ pins 不回归）；`dotnet build Kobold.csproj -c Debug --nologo` 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `refactor(widgets): drive the data converter from the kind registry`
  → `git add Core/WidgetKindRegistry.cs Core/WidgetDataConverter.cs tests/WidgetKindsCheck/Program.cs`

---

## Task T2: 窗口工厂（Kind → 窗口构造）

**Files：**
- Create: `Controls/WidgetWindowFactory.cs`
- Modify: `Core/WidgetManager.cs`（`CreateWindow` 改查表）
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces: `WidgetWindowFactory.Create(WidgetData data) -> IWidgetWindow`（未登记 / null → null）。
- Consumes: 既有 `FolderWidget(FolderData)` / `TodoWidget(TodoData)` 构造器。

- [ ] **Step 1（红）：** XamlLoadCheck 加（既有窗口构造 pin 的写法即可，无需真实显示）：
  - `factory: folder data builds the folder widget`：`WidgetWindowFactory.Create(new FolderData()) is FolderWidget`。
  - `factory: todo data builds the todo widget`：`is TodoWidget`。
  - `factory: an unknown kind has no window`：`WidgetWindowFactory.Create(new UnknownData { Kind = "weather" }) == null`；`Create(null) == null`。
  运行 → 编译失败（无 `WidgetWindowFactory`）即红。留档。

- [ ] **Step 2（实现）：**
  - 新建 `Controls/WidgetWindowFactory.cs`（`using System; using System.Collections.Generic; using Kobold.Core;`）：
    ```csharp
    /// <summary>
    /// The kind-to-window registry: every widget kind registers its window
    /// constructor here, so adding a kind never edits the manager. A kind
    /// with no registration (or a null datum) has no window.
    /// </summary>
    public static class WidgetWindowFactory
    {
        private static readonly Dictionary<string, Func<WidgetData, IWidgetWindow>> Factories =
            new Dictionary<string, Func<WidgetData, IWidgetWindow>>(StringComparer.Ordinal)
        {
            { "folder", data => new FolderWidget((FolderData)data) },
            { "todo", data => new TodoWidget((TodoData)data) },
        };

        public static IWidgetWindow Create(WidgetData data)
        {
            if (data == null || string.IsNullOrEmpty(data.Kind)) return null;
            Func<WidgetData, IWidgetWindow> factory;
            return Factories.TryGetValue(data.Kind, out factory) ? factory(data) : null;
        }
    }
    ```
  - `WidgetManager.CreateWindow`（349 行一带）：保留 `data == null || WidgetKindRegistry.Find(data.Kind) == null → null` 的短路，其后两行 `is FolderData/is TodoData` 替换为 `return WidgetWindowFactory.Create(data);`。删除不再使用的分支后确认 usings 干净。

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿（既有 widget 构造 pin 不回归——它们直接 new 具体类，不受影响）；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `refactor(widgets): build widget windows through a kind factory`
  → `git add Controls/WidgetWindowFactory.cs Core/WidgetManager.cs tests/XamlLoadCheck/Program.cs`

---

## Task T3: 图标分发收口（IconKey → 渲染器）

**Files：**
- Modify: `Controls/IconRenderers/IconRendererBase.cs`（`IconRendererFactory` 增 `CreateByKey`）
- Modify: `Controls/IslandWindow.xaml.cs`（`CreateTile` 去 `Kind == "todo"` 特判）
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces: `IconRendererFactory.CreateByKey(string iconKey, string style) -> IIconRenderer`：
  `"todo"` → `CreateTodo(style)`；`"folder"` / null / 未知 → `Create(style)`（与今日 `CreateTile` 的 else 分支一致）。
- Consumes: 既有 `Create(style)` / `CreateTodo(style)` 与 `WidgetKindRegistry.Find(...).IconKey`。

- [ ] **Step 1（红）：** XamlLoadCheck 加：
  - `icon: the by-key factory dispatches todo`：`CreateByKey("todo", "handdrawn") is HandDrawnTodoIconRenderer`；`CreateByKey("todo", "classic") is ClassicTodoIconRenderer`。
  - `icon: the by-key factory falls back to folder for everything else`：`CreateByKey("folder", "classic") is ClassicIconRenderer`；`CreateByKey("nonsense", "classic") is ClassicIconRenderer`；`CreateByKey(null, "handdrawn") is HandDrawnIconRenderer`。
  运行 → 编译失败（无 `CreateByKey`）即红。留档。

- [ ] **Step 2（实现）：**
  - `IconRendererFactory` 增：
    ```csharp
    /// <summary>Picks the renderer from a <see cref="WidgetKind.IconKey"/>;
    /// the folder glyph covers the fallback cases, matching the island's
    /// historical else-branch.</summary>
    public static IIconRenderer CreateByKey(string iconKey, string style)
    {
        if (iconKey == "todo") return CreateTodo(style);
        return Create(style);
    }
    ```
  - `IslandWindow.CreateTile`（612 行一带）替换 `if (widget.Kind == "todo") … else …` 为：
    ```csharp
    var kind = WidgetKindRegistry.Find(widget.Kind);
    IconRendererFactory.CreateByKey(kind != null ? kind.IconKey : "folder", iconStyle)
        .Render(iconCanvas, widget.Color);
    ```
    （`WidgetKindRegistry` 已在 IslandWindow 的既有 using/引用里；确认即可。）

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿（既有 `island: folder tile refreshes…` / `island: todo tile refreshes…` pins 即回归保护）；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `refactor(island): dispatch tile icons by the kind icon key`
  → `git add Controls/IconRenderers/IconRendererBase.cs Controls/IslandWindow.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T4: 笔记图标（经典 + 手绘 + note 键登记）

**Files：**
- Create: `Controls/IconRenderers/NoteIconStyles.cs`
- Modify: `Controls/IconRenderers/IconRendererBase.cs`（`CreateByKey` 增 `"note"` 分支 + `CreateNote` 工厂）
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces: `class ClassicNoteIconRenderer : IconRendererBase`、`class HandDrawnNoteIconRenderer : IconRendererBase`；
  `IconRendererFactory.CreateNote(string style)`（"handdrawn" → 手绘，其余 → 经典，对齐既有两种）；
  `CreateByKey("note", style)` 映射。
- 参考：`Controls/IconRenderers/TodoIconStyles.cs`（两类渲染器的画法与常量）、`IconStyles.cs`、`HandDrawnIconRenderer.cs`（手绘笔触近似法）。

**视觉简报（画什么）：** 一页笔记——圆角矩形纸面，右上折角；纸面内 3~4 条横线（短横线缩进）；左侧一条组件色书签/页边。经典 = 干净几何路径；手绘 = 轻微抖动的笔触 + 纸面色跟主题（参照 `HandDrawnTodoIconRenderer` 的纸色/墨色处理）；组件色用于书签与横线强调；高对比度下墨色回退系统色（对齐既有渲染器）。

- [ ] **Step 1（红）：** XamlLoadCheck 加（沿用既有 icon pin 的 `RenderIcon` 助手）：
  - `icon: note factory dispatches classic and hand-drawn`：`CreateNote("classic") is ClassicNoteIconRenderer`、`CreateNote("handdrawn") is HandDrawnNoteIconRenderer`、`CreateNote("rounded") is ClassicNoteIconRenderer`（退役样式回退，对齐 folder/todo 的既有 pin 语式）。
  - `icon: the by-key factory dispatches note`：`CreateByKey("note", "handdrawn") is HandDrawnNoteIconRenderer`。
  - `icon: classic note accents use the widget colour`：渲染后画布中能找到组件色（六位 hex → `Utils.HexToColor` 全等比较；参照 `icon: classic todo accents use the widget colour` 的找色写法）。
  - `icon: hand-drawn note uses the widget colour and themed ink`：组件色存在 + 纸面色随主题变化（参照 `HandDrawnTodoPaperColour` 的写法，复制其"取纸面色"助手并适配本文件）。
  - `icon: hand-drawn note geometry differs from classic`：两画布子元素结构/数量不同（参照既有 `island: hand-drawn system geometry differs from classic` 语式）。
  运行 → 编译失败（类型不存在）即红。留档。

- [ ] **Step 2（实现）：** 新建 `NoteIconStyles.cs`（两个渲染器，实现时**逐行对照** `TodoIconStyles.cs` 的骨架：`Render(Canvas, string colourHex)` → 解析颜色 → 画路径/矩形 → 缩放居中到 64×64）；`IconRendererFactory` 增 `CreateNote` 与 `CreateByKey` 的 `"note"` 分支。

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the note icon renderers`
  → `git add Controls/IconRenderers/NoteIconStyles.cs Controls/IconRenderers/IconRendererBase.cs tests/XamlLoadCheck/Program.cs`

---

## Task T5: NoteData 接线（模型 / 注册表 / 修复 / 迁移 / 三语 key）

**Files：**
- Create: `Core/NoteData.cs`
- Modify: `Core/WidgetKindRegistry.cs`（note 条目）
- Modify: `Core/AppConfig.cs`（校验 + 修复分支）
- Modify: `Core/PanelSizeMigration.cs`（note 默认尺寸）
- Modify: `Core/Localization.cs`（`UI_DefaultNoteName` 三语）
- Test: `tests/WidgetKindsCheck/Program.cs`

**Interfaces：**
- Produces:
  - `class NoteData : WidgetData { List<NoteItem> Notes; }`；`class NoteItem { string Id; string Text; DateTime CreatedAt; DateTime ModifiedAt; }`
  - 注册表 `note` 条目：`Id="note"`、`NameKey="UI_DefaultNoteName"`、`IconKey="note"`、`DataType=typeof(NoteData)`、`CreateDefault=() => new NoteData()`。
  - 迁移默认：`PanelWidth ??= 300`、`PanelHeight ??= 380`（常量 `NOTE_DEFAULT_WIDTH/HEIGHT`）。
- Consumes: T1 的 `WidgetKind.DataType`（转换器自动接纳 note，无需改转换器——这正是收口的收益，pin 验证）。

- [ ] **Step 1（红）：** WidgetKindsCheck 加：
  - `note: a round trip keeps the kind and the notes`：显式时间戳夹具（`new DateTime(2026,10,15,9,30,0)` 与 `…, 10, 0, 0`）→ 保存 → 重新加载 → `is NoteData`、条数、Text、两个时间戳精确相等、`Kind == "note"`。
  - `note: saving writes Kind= note`（文件文本含 `"Kind": "note"`）。
  - `note: an unknown kind is still preserved`（既有 pin 保持）。
  - `note: the registry holds three kinds`：既有 `All.Count == 2` pin 改为 3；note 条目的 Id/NameKey/IconKey/DataType 齐备（对齐 folder/todo 的字段 pin）。
  - `note: repair gives a missing item id one` / `a null note text becomes empty`（`RepairNote` 行为）。
  - `note: a null notes list is rejected as corrupt`（`Notes == null` 的 JSON → 走 failed 路径，对齐 todo 的 `Items == null` 校验 pin）。
  - `note: the migration default is 300x380`：缺尺寸 → `(300, 380)`；已有宽度只补高度（对齐 todo 的 `D` 组 pin 语式）。
  运行 → 编译失败（`NoteData` 不存在）即红。留档。

- [ ] **Step 2（实现）：**
  - `Core/NoteData.cs`（对齐 `TodoData` 形态）：
    ```csharp
    public class NoteData : WidgetData
    {
        public List<NoteItem> Notes { get; set; }

        public NoteData()
        {
            Id = Guid.NewGuid().ToString();
            Color = UiTokens.DefaultFolderColor;
            Notes = new List<NoteItem>();
            Kind = "note";
        }
    }

    /// <summary>One note: the markdown source plus its two timestamps.</summary>
    public class NoteItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
    }
    ```
  - 注册表加 note 条目（`CreateDefault = () => new NoteData()`）。
  - `AppConfig`：① 校验循环（220 行一带）加 `if (widget is NoteData note && note.Notes == null) throw new InvalidDataException("config note Notes is null");`；
    ② `RepairLegacyItems` 分发加 `else if (widget is NoteData note) RepairNote(note);`；
    ③ 新方法：
    ```csharp
    private static void RepairNote(NoteData note)
    {
        foreach (var item in note.Notes)
        {
            if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString();
            if (item.Text == null) item.Text = string.Empty;
        }
    }
    ```
  - `PanelSizeMigration.FillMissingSizes` 加 note 分支（类常量 `NOTE_DEFAULT_WIDTH = 300, NOTE_DEFAULT_HEIGHT = 380`，注释引 spec §6.5）。
  - `Localization.cs`：三语 `["UI_DefaultNoteName"]` = `"Note"` / `"笔记"` / `"ノート"`（放在 `UI_DefaultTodoName` 行之后；先读该行值的确切写法再落）。

- [ ] **Step 3（绿）：** WidgetKindsCheck + LangCheck 全绿；build 0 警告。

- [ ] **Step 4（提交，1 笔）：**
  `feat(note): register the note widget kind`
  → `git add Core/NoteData.cs Core/WidgetKindRegistry.cs Core/AppConfig.cs Core/PanelSizeMigration.cs Core/Localization.cs tests/WidgetKindsCheck/Program.cs`

---

## Task T6: NoteOps + NoteMarkdownCheck 工程建立 + 矩阵登记

**Files：**
- Create: `Core/NoteOps.cs`
- Create: `tests/NoteMarkdownCheck/NoteMarkdownCheck.csproj`、`tests/NoteMarkdownCheck/Program.cs`
- Modify: `tools/Run-Checks.ps1`（矩阵加行）

**Interfaces：**
- Produces（本轮先交 4 个；`TaskProgressOf` 在 T8 追加）：
  ```csharp
  public static class NoteOps
  {
      public static string TitleOf(string text);
      public static string ExcerptOf(string text, int maxChars);
      public static string ToggleTaskAt(string text, int lineIndex);
      public static List<NoteItem> SortForDisplay(IList<NoteItem> notes);
  }
  ```

- [ ] **Step 1（红）：** 新建工程（`csproj` 逐字对齐 `tests/TodoCalendarCheck/TodoCalendarCheck.csproj`，改 `AssemblyName`/`RootNamespace` 为 `Kobold.NoteMarkdownCheck`；`Program.cs` 采用同一自检骨架：`Check()` 辅助 + 小结 + 失败非零退出——实现时复制该文件骨架）。加 pins：
  - TitleOf：`null/空 → ""`；`"# 标题\n正文"` → `"标题"`；`"## 二级"` → `"二级"`；首行空白行被跳过；行内标记剥除（`"**粗** 与 [链](https://x)"` → `"粗 与 链"`）；纯空白 → `""`。
  - ExcerptOf：`maxChars<=0 → ""`；多行压空白连接；剥 `#`/`- `/`1. `/`> ` 前缀；围栏行不出现；超长截断加 `…`；恰好不超不加深略号。
  - ToggleTaskAt：`"- [ ] a" → "- [x] a"`；`"- [x] b" → "- [ ] b"`；`"- [X] c" → "- [ ] c"`；往返幂等（两次回到原文）；非任务行（`"普通"`、`"  - [ ] 缩进"`）原样返回；行号越界原样；`\r\n` 行尾保留（多行夹具逐字符相等）。
  - SortForDisplay：显式时间戳倒序；同刻保持原顺序（稳定）；不改动源列表。
  运行 `dotnet run --project tests\NoteMarkdownCheck\NoteMarkdownCheck.csproj -c Debug --nologo` → 编译失败（`NoteOps` 不存在）即红。留档。

- [ ] **Step 2（实现）：** `Core/NoteOps.cs`：
  - `TitleOf`：按行扫首个非空行；剥 `^#{1,6}\s+`；`StripInlineMarkers`（`\[([^\]]*)\]\([^)]*\)` → `$1`，再替换掉 `** __ ~~ * _ ` `` ` 字符）；Trim。
  - `ExcerptOf`：逐行清洗（剥行首 `#`/列表/任务/引用前缀、跳过围栏行）→ 剥行内标记 → 各段用空格连接 → 连续空白压成一个 → 超 `maxChars` 截断（`Substring(0, maxChars).TrimEnd() + "…"`）。
  - `ToggleTaskAt`：`text.Split('\n')`（`\r` 随行保留）；行匹配 `^- \[( |x|X)\]( |$)`；`" "→"x"`、`"x"/"X"→" "`；不匹配原样返回；`string.Join("\n", …)`。
  - `SortForDisplay`：`notes.OrderByDescending(n => n.ModifiedAt).ToList()`（LINQ 稳定序）。
- [ ] **Step 3（实现）：** `tools/Run-Checks.ps1` 矩阵在 `LunarCalendarCheck` 之后插入：
  ```powershell
  @{ Name = 'NoteMarkdownCheck';     Ci = $true  }
  ```
- [ ] **Step 4（绿）：** NoteMarkdownCheck 全绿；`powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -List` 显示 30 行（含新行）；build 0 警告。
- [ ] **Step 5（提交，1 笔）：**
  `feat(note): add the note ops and the markdown check project`
  → `git add Core/NoteOps.cs tests/NoteMarkdownCheck/ tools/Run-Checks.ps1`

---

## Task T7: Markdown 块级解析器（含表格结构；行内暂为字面）

**Files：**
- Create: `Core/MarkdownParser.cs`（AST + 块级解析；行内 = stub）
- Test: `tests/NoteMarkdownCheck/Program.cs`

**Interfaces：**
- Produces（T8 依赖这些确切名字）：
  ```csharp
  public enum MdAlign { None, Left, Center, Right }

  public abstract class MdBlock { public int Line; }
  public sealed class MdHeading : MdBlock { public int Level; public List<MdInline> Inlines; }
  public sealed class MdParagraph : MdBlock { public List<List<MdInline>> Lines; }   // 每个源码行一组（软换行保留）
  public sealed class MdListBlock : MdBlock { public bool Ordered; public int Start; public List<MdListItem> Items; }
  public sealed class MdListItem { public int Line; public List<MdInline> Inlines; }
  public sealed class MdTaskBlock : MdBlock { public bool Checked; public List<MdInline> Inlines; }
  public sealed class MdQuote : MdBlock { public List<MdBlock> Blocks; }
  public sealed class MdCodeBlock : MdBlock { public string Info; public string Text; }
  public sealed class MdTable : MdBlock { public MdAlign[] Align; public List<List<MdInline>> Header; public List<List<List<MdInline>>> Rows; }
  public sealed class MdThematicBreak : MdBlock { }

  public abstract class MdInline { }
  public sealed class MdText : MdInline { public string Text; }
  public sealed class MdCodeSpan : MdInline { public string Text; }
  public sealed class MdLink : MdInline { public string Text; public string Url; }
  public sealed class MdStrong : MdInline { public List<MdInline> Inlines; }
  public sealed class MdEm : MdInline { public List<MdInline> Inlines; }
  public sealed class MdStrike : MdInline { public List<MdInline> Inlines; }

  public static class MarkdownParser
  {
      public static List<MdBlock> Parse(string text);
      public static List<MdInline> ParseInlines(string text);   // T7 stub：单个 MdText（行内字面）
  }
  ```
- 关键约定：所有 `Line` 是**整体文本的绝对行号（0 基）**；引用内递归解析后 `Line += 引用起始行`。
- 任务识别：仅第 0 列 `- [ ]`/`- [x]`/`- [X]`（`]` 后空格或行尾）；**先于**无序列表判定。

- [ ] **Step 1（红）：** NoteMarkdownCheck 加块级 pins（每条断言类型/计数/关键字段/行号）：
  - `block: a blank text parses to nothing`；`a single line parses as one paragraph`。
  - `block: headings carry their level`（`#`~`######`、行尾 `#` 剥除、`#后无空格` → 段落）；行号正确。
  - `block: a paragraph keeps its source lines`（三行 → `Lines.Count == 3`）；空行分段。
  - `block: bullets group into one list`（连续 `- `/`* ` → 1 个块 N 项）；`block: numbered items keep the start number`；`block: a nested-looking item stays a paragraph`（缩进行 `  - x` 不与列表合流——按文本行进段落）。
  - `block: tasks parse with their state and line`（`[ ]`/`[x]`/`[X]`、行号、`- [x]nospace` 之后无空格 → 不算任务）。
  - `block: a quote groups its lines`（内层段落计数；内层任务行号 = 绝对行号）。
  - `block: an unclosed fence runs to the end`；`block: the info string is kept`。
  - `block: a standalone rule is a break, not a table`（`---` 单独行）；`block: the line after a paragraph rule`（`---` 紧随段落仍为分割线——不做 setext）。
  - `block: a table reads the header, the separator and the rows`（对齐 `:---:` 三角；`Rows.Count`）；`block: the table pads short rows and drops extra cells`；`block: a pipe line without a separator stays a paragraph`；`block: an escaped pipe does not split a cell`（`a \| b` 一个单元格）。
  运行 → 编译失败即红。留档。

- [ ] **Step 2（实现）：** `Core/MarkdownParser.cs`：
  - `Parse`：`text.Replace("\r\n","\n").Replace('\r','\n').Split('\n')` 后单遍扫描（算法骨架）：
    ```csharp
    // 空行 → 跳过；``` 围栏 → MgCodeBlock（未闭合到文末）；独立 ---/***/___ → MdThematicBreak；
    // 含 | 且下一行是分隔行 → ParseTable；^(#{1,6})\s+ → MdHeading；
    // ^>（连续）→ 剥前缀递归 Parse 并整体 Line += 起始行 → MdQuote；
    // ^- \[( |x|X)\]( |$) → MdTaskBlock；^- /^\* /^\d+\. 连续同类 → MdListBlock；
    // 其余连续行 → MdParagraph（每组源码行一个 Lines 项，均走 ParseInlines）。
    ```
  - 正则常量：fence `^\s*````；heading `^(#{1,6})\s+(.*?)(?:\s+#+)?\s*$`；quote `^>\s?(.*)$`；task `^- \[( |x|X)\](?: (.*)|$)`；bullet `^[-*](?: (.*)|$)`；numbered `^(\d+)\.(?: (.*)|$)`；rule `^\s*(?:-{3,}|\*{3,}|_{3,})\s*$`；分隔行 `^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)+\|?\s*$`。
  - 表格：按 `(?<!\\)\|` 切单元格（去首尾空 `|`），字幕组与体行都按对齐数补空/截断；单元格文本走 `ParseInlines`。
  - `ParseInlines` stub：`new List<MdInline> { new MdText { Text = text } }`。
  - 段落终止条件：空行或任一块起始行（heading/quote/task/list/fence/rule/表格对）。
- [ ] **Step 3（绿）：** NoteMarkdownCheck 全绿；WidgetKindsCheck 不回归；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the markdown block parser`
  → `git add Core/MarkdownParser.cs tests/NoteMarkdownCheck/Program.cs`

---

## Task T8: 行内解析 + 表格行内生效 + TaskProgressOf

**Files：**
- Modify: `Core/MarkdownParser.cs`（`ParseInlines` 真实现；表格/标题/段落自动受益）
- Modify: `Core/NoteOps.cs`（`TaskProgressOf`）
- Test: `tests/NoteMarkdownCheck/Program.cs`

**Interfaces：**
- Produces: `NoteOps.TaskProgressOf(string text) -> (int done, int total)?`（无任务 → null；统计含引用内的任务行；代码块内不算）。
- 行内优先级（左→右扫描）：转义 → 行内代码 → 链接 → `**`/`__` → `~~` → `*`/`_`；未闭合按字面。

- [ ] **Step 1（红）：** 加 pins：
  - `inline: code spans win over emphasis`（`` `a **b** c` `` 一个 MdCodeSpan）；`` 未闭合反引号按字面 ``。
  - `inline: strong and em pair simply`（`**a**`、`__a__`、`*b*`、`_b_` 各成块；嵌套 `**a *b* c**` → Strong 内 Em）；未闭合 `**a` 按字面。
  - `inline: strike uses the double tilde`（`~~x~~`）；单个 `~` 按字面。
  - `inline: links carry text and url`（`[t](https://x)` → MdLink("t","https://x")）；`http://` 同样；**非 http/https 链接整个按字面**（`[t](ftp://x)` → 单个 MdText 原样）；未闭合 `[t](` 按字面。
  - `inline: backslash escapes the markers`（`\*x\*` 字面、`\\` 字面反斜杠、`\|` 字面竖线）。
  - `inline: table cells parse their inlines`（`| **a** | ` 头格为 MdStrong）——经 `Parse` 集成断言。
  - `inline: quotes re-parse their content`（引用内 `**a**` 成立）。
  - `progress: counts open and done tasks`（(1,3) 夹具）；`progress: a day with no tasks reports null`；`progress: code-block task lines are not counted`（``` 内 `- [ ] x` 不算）；`progress: quoted tasks are counted`。
  运行 → 失败（stub 返回字面 / 无 TaskProgressOf）即红。留档。

- [ ] **Step 2（实现）：** `ParseInlines` 带位置指针的单遍扫描 + 递归；字节级细节按 pin 语义；`TaskProgressOf` 基于 `Parse` 的块递归计数（`MdTaskBlock` 计 1 + Checked；`MdQuote` 递归）。
- [ ] **Step 3（绿）：** NoteMarkdownCheck 全绿；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the inline parser and task progress`
  → `git add Core/MarkdownParser.cs Core/NoteOps.cs tests/NoteMarkdownCheck/Program.cs`

---

## Task T9: NoteEditorOps（快速插入纯函数）

**Files：**
- Create: `Core/NoteEditorOps.cs`
- Test: `tests/NoteMarkdownCheck/Program.cs`

**Interfaces：**
- Produces（返回新文本 + 新光标 + 新选区；占位词由调用方传入，核心不引 Localization）：
  ```csharp
  public static class NoteEditorOps
  {
      public static (string Text, int SelStart, int SelLength) InsertHeading(string text, int selStart, int selLength, int level);
      public static (string, int, int) InsertBullet(string text, int selStart, int selLength);
      public static (string, int, int) InsertNumbered(string text, int selStart, int selLength);
      public static (string, int, int) InsertTask(string text, int selStart, int selLength);
      public static (string, int, int) InsertQuote(string text, int selStart, int selLength);
      public static (string, int, int) InsertCodeBlock(string text, int selStart, int selLength);
      public static (string, int, int) InsertTable(string text, int selStart, int selLength, string col1, string col2);
      public static (string, int, int) InsertDivider(string text, int selStart, int selLength);
      public static (string, int, int) WrapBold(string text, int selStart, int selLength, string placeholder);
      public static (string, int, int) WrapItalic(string text, int selStart, int selLength, string placeholder);
      public static (string, int, int) WrapStrike(string text, int selStart, int selLength, string placeholder);
      public static (string, int, int) WrapInlineCode(string text, int selStart, int selLength, string placeholder);
      public static (string, int, int) WrapLink(string text, int selStart, int selLength, string placeholder);
  }
  ```
- 引擎约定：行级操作 = 生成按位置升序的 `(pos, removeLen, insert)` 编辑表 → 单遍应用；光标/选区换算：
  `p' = p + Σ(delta | 编辑位置 ≤ p)`；落在删除区间内的光标钳到该编辑之后（`pos + insert.Length`）。

- [ ] **Step 1（红）：** pins（每条给全文 + 期望全文 + 期望 SelStart/SelLength）：
  - 标题：空文本 caret 0 → `"# "` caret 2；`"## a"` 改 `3` → `"### a"`；多行选区两行都加。
  - 列表 toggle：`"- a"` 再插 bullet → `"a"`；`"1. a"` 插 bullet → `"- a"`；`"  x"`（无前缀）插 numbered → `"1.   x"`（前缀加在最前，内容不动）；两次应用 bullet 幂等往返。
  - 任务：`"a"` → `"- [ ] a"`；`"- [x] a"` 再插 task → `"a"`；`"- a"` 插 task → `"- [ ] a"`。
  - 引用：`"a"` → `"> a"`；再次 → `"a"`。
  - 代码块：空行 caret → 骨架（光标在内部空行）；非空行 → 行尾插入。
  - 表格：非空行 → 骨架三行 + 光标在首个体格；空行 → 就地替换。全文逐字符 pin。
  - 分割线：两个锚点用例。
  - Wrap：有选区 `**x**` 且选区仍罩 `x`；无选区 → 占位词被选中；链接：有选区光标进 `()`、无选区选中占位词。
  - 边界：空文本 + caret 0 各 wrap 的占位路径；selStart/selLength 越界钳位不抛。
  运行 → 编译失败（类型不存在）即红。留档。

- [ ] **Step 2（实现）：** `Core/NoteEditorOps.cs`（结构：`AffectedLines(text, selStart, selEnd)` → 每行匹配既有前缀族 → 编辑表 → `ApplyEdits` 单遍重建 + 换算）。前缀族正则与 T7 解析器一致（`^#{1,6}\s+`、`^- \[( |x|X)\]\s?`、`^[-*]\s`、`^\d+\.\s`、`^>\s?`）。
- [ ] **Step 3（绿）：** NoteMarkdownCheck 全绿；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the editor insert ops`
  → `git add Core/NoteEditorOps.cs tests/NoteMarkdownCheck/Program.cs`

---

## Task T10: NoteWidget 壳 + 视图骨架 + 工厂登记 + 最小尺寸

**Files：**
- Create: `Controls/NoteWidget.xaml`、`Controls/NoteWidget.xaml.cs`、`Controls/NotePanelView.xaml`、`Controls/NotePanelView.xaml.cs`
- Modify: `Controls/WidgetWindowFactory.cs`（note 登记）
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces: `NoteWidget : WidgetWindowBase`（`public NotePanelView PanelView { get; }`）；`NotePanelView` 空骨架（列表宿主 + 顶部 `＋` 行、无行为）；
  `NoteWidget` 常量 `MIN_WIDTH = 260`、`MIN_HEIGHT = 220`（`GetMinPanelSize()` override，注释引 spec §6.5）。
- Consumes: T5 `NoteData`、T4 图标、T3 岛分发（登记后岛 tile 自动可用）。

- [ ] **Step 1（红）：** XamlLoadCheck 加（对齐 `TodoWidget [dark/light/high contrast]` 既有 pin 形态）：
  - `NoteWidget [dark] / [light] / [high contrast] loads`；
  - `NoteWidget with notes [dark] loads`（1 条带文本的 `NoteData`）；
  - `factory: note data builds the note widget`：`WidgetWindowFactory.Create(new NoteData()) is NoteWidget`；
  - `note: the panel minimum fits the shell`：`GetMinPanelSize()`（既有反射取法）== (260, 220)。
  运行 → 编译失败即红。留档。

- [ ] **Step 2（实现）：**
  - `NoteWidget.xaml`：逐行对齐 `TodoWidget.xaml`（`local:WidgetWindowBase` 根、同名属性集、`<local:WidgetChrome x:Name="NoteChrome"/>`、`TextOptions.TextFormattingMode="Display"`；暂不加 PreviewKeyDown——T11 撤回栈需要时加），`Title="NoteWidget"`。
  - `NoteWidget.xaml.cs`：对齐 `TodoWidget.xaml.cs` 骨架：ctor `(NoteData data) : base(data)` → `InitializeComponent()` → `AttachChrome(NoteChrome)` → `_view = new NotePanelView()` → `NoteChrome.ContentHost.Content = _view` → `_view.Attach(data, this)`；
    `GetMinPanelSize()` override → `(MIN_WIDTH, MIN_HEIGHT)`；`GetContentSize()` override（先读基类该方法的用途与 TodoWidget 的写法再定值；笔记取 `(300, 380)` 常量）；`UpdateUI()` override → `_view?.Refresh()`；`ShowWidgetMenu` override → `WidgetMenuBuilder.Build(this, onClosed).IsOpen = true;`。
  - `NotePanelView.xaml/.cs` 骨架：根 `Grid` 两行（列表 `ScrollViewer` + 顶部 `＋` 行占位 Border）；`Attach(NoteData, NoteWidget)` 存引用；`Refresh()` 空实现；构造 `ContextMenu` 不放这（T15）。
  - `WidgetWindowFactory` 工厂表加：`{ "note", data => new NoteWidget((NoteData)data) }`。
  - Commit 信息备注：本任务后岛 `＋` 菜单自动出现「笔记」（`WidgetMenuBuilder` 的 kinds 循环）+ tile 用 T4 图标。

- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；WidgetKindsCheck 不回归；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the note widget shell`
  → `git add Controls/NoteWidget.xaml Controls/NoteWidget.xaml.cs Controls/NotePanelView.xaml Controls/NotePanelView.xaml.cs Controls/WidgetWindowFactory.cs tests/XamlLoadCheck/Program.cs`

---

## Task T11: 列表态（卡片 / 排序 / 删除 / Ctrl+Z 撤回 / 菜单）

**Files：**
- Modify: `Controls/NotePanelView.xaml`、`Controls/NotePanelView.xaml.cs`
- Modify: `Controls/NoteWidget.xaml`、`Controls/NoteWidget.xaml.cs`（PreviewKeyDown 路由 Ctrl+Z）
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces（视图内部 + pin 反射面）：
  - `NotePanelView.InvokeDelete(note)` 私有；`public bool TryUndo()`（撤回最近一次删除，多步栈 ≤20）；
  - `NotePanelView.RebuildList()` 私有（重建卡片）；
  - `NoteWidget.Window_PreviewKeyDown`：焦点不在 `TextBox` 时 Ctrl+Z → `_view.TryUndo()`（对齐 TodoWidget 写法）。
- 卡片结构：`Border`（悬浮底色圆角）→ `StackPanel`：标题（14 粗）、时间（11 次要色、`ModifiedAt.ToString("g")`）、摘要（`ExcerptOf(text, 80)`，`TextTrimming` 两行）、进度（`TaskProgressOf` 非 null 时 `"✓ {done}/{total}"` 11 次要色）。

- [ ] **Step 1（红）：** XamlLoadCheck 加：
  - `note: cards show title, time and excerpt`（3 条夹具：标题+正文、带任务、空摘要；断言卡片 StackPanel 子元素文本，时间用显式时间戳的 `ToString("g")` 期望值）。
  - `note: a task note shows its progress`（`"- [x] a\n- [ ] b"` → `"✓ 1/2"`）。
  - `note: the list orders by modified time`（两条不同 ModifiedAt → 卡片顺序）。
  - `note: the row menu deletes the note and undo restores it`（`RaiseEvent(MenuItem.ClickEvent)` 风格调用删除 → 数据少一条；反射 `TryUndo()` → 同位复原）。
  - `note: undo walks back beyond one step`（删两条 → 两次 TryUndo 逐步复原）。
  - `note: the stack keeps only the newest twenty`（21 条删除 → 21 次 TryUndo 后仍有 1 条未回；对齐待办上限 pin 判别写法）。
  - `note: an empty list shows only the add row`（无笔记 → 无卡片、`＋` 行可见）。
  运行 → 失败（无卡片逻辑）即红。留档。

- [ ] **Step 2（实现）：**
  - `NotePanelView`：`_removals = new List<(NoteItem Item, int Index)>()`（或小类 `NoteRemoval`）；删除 = 从 `_data.Notes` 移除 + 入栈（超 20 丢最旧）+ `OnDataChanged`（经 `_host` 的上报路径——先读 TodoCalendarView 如何触发持久化并照做）+ 重建；`TryUndo` = 弹栈 → `Insert(index, item)` → 重建 → true。
  - `RebuildList()`：清列表宿主 → `NoteOps.SortForDisplay(_data.Notes)` 逐条建卡片（上述结构；悬浮/主题色走既有令牌）；空列表只留 `＋` 行。
  - 行菜单：`MenuBuilder`（组件色，参照 TodoCalendarView 的行菜单构建）加「删除」（既有 `Menu_DeleteItem` key；若语义不符读现有 key 再选）。
  - `NoteWidget.xaml` 根加 `PreviewKeyDown="Window_PreviewKeyDown"`；code-behind 照 TodoWidget 的 Ctrl+Z 写法（`Keyboard.FocusedElement is TextBox` 则放行）。
- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): show the note list with delete undo`
  → `git add Controls/NotePanelView.xaml Controls/NotePanelView.xaml.cs Controls/NoteWidget.xaml Controls/NoteWidget.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T12: NoteMarkdownRenderer（AST → FlowDocument 静态渲染）

**Files：**
- Create: `Controls/NoteMarkdownRenderer.cs`
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces：
  ```csharp
  public sealed class NoteRenderStyle
  {
      public Brush Text; public Brush Muted; public Brush Accent;
      public Brush CodeBackground; public Brush Divider;
      public bool HighContrast;
      public FontFamily BodyFont; public double BaseFontSize;
  }
  public sealed class NoteRenderCallbacks
  {
      public Action<int> ToggleTaskLine;   // 任务框点击：源码行号（T13 接）
      public Action<string> OpenLink;      // http/https 点击
  }
  public static class NoteMarkdownRenderer
  {
      public static FlowDocument Build(List<MdBlock> blocks, NoteRenderStyle style, NoteRenderCallbacks callbacks = null);
  }
  ```
- 映射：`MdHeading` → 字号阶梯（Base × 1.6/1.4/1.25/1.12/1.05/1.0，自 H1 递减）；`MdParagraph` 行间 `LineBreak`；`MdListBlock` → `List`/`ListItem`（有序 `StartIndex` = Start）；`MdTaskBlock` → 段 + 任务字形（`Border`+`Path`：方框；完成 = 方框 + 手绘感勾，颜色 Accent 高对比回退系统色；`Tag = line`；`MouseLeftButtonUp` → `callbacks?.ToggleTaskLine?.Invoke(line)`）；`MdQuote` → 左缩进 + 左竖线 `Border` 包裹；`MdCodeBlock` → 等宽 + `CodeBackground` + 不换行截断？**换行显示**（`TextWrapping` 默认）；`MdTable` → `Table`（列均分 `GridLength(1, Star)`、单元 `Padding`、表头粗体 + 底边线、列 `TextAlignment` 按 `Align`）；`MdThematicBreak` → `Divider` 色细线。
- 行内：`MdStrong`→`FontWeight`；`MdEm`→`FontStyle`；`MdStrike`→`TextDecorations.Strikethrough`；`MdCodeSpan`→等宽 + `CodeBackground`；`MdLink`→`Hyperlink`（`NavigateUri` + `Click` → `callbacks?.OpenLink?.Invoke(url)` + `e.Handled = true`）。

- [ ] **Step 1（红）：** XamlLoadCheck 加（`NoteRenderStyle` 用真实令牌/主题构造 + 一条注入组件色的用例）：
  - `renderer: headings step down in size`（H1..H6 段落的 FontSize 严格递减且 ≥ Base）。
  - `renderer: a paragraph keeps its source line breaks`（3 行段落 = 2 个 `LineBreak`）。
  - `renderer: strong runs are bold, em are italic, strike strikes`（Run 属性断言）。
  - `renderer: inline code is monospace on its own background`。
  - `renderer: a task glyph carries its source line`（`Tag` 恰为解析行号；完成态与未完成态结构不同）。
  - `renderer: the table renders aligned cells`（`Table.RowGroups` 行/列数、单元格 `TextAlignment` 与对齐数组一致）。
  - `renderer: a link routes its url on click`：以记录型 `OpenLink` 构造 → 对 `Hyperlink.RaiseEvent(ClickEvent)` → 记录到 url 且事件已 `Handled`（不会真导航——不触浏览器）。
  - `renderer: high contrast swaps to system colours`（`HighContrast = true` 的样式或由 `ThemeManager` 驱动——按实现选取断言点：段落前景 == 系统窗文本色）。
  - `renderer: the accent follows the widget colour`（任务字形/链接色 == `Utils.HexToColor(data.Color)`）。
  运行 → 编译失败即红。留档。

- [ ] **Step 2（实现）：** `Controls/NoteMarkdownRenderer.cs`（`using System.Windows.Documents;` 等）；`NoteRenderStyle` 由视图从 `ThemeManager` + 组件色构造（本任务在 pin 里手工构造，T13 接入视图）。
- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；MenuRenderCheck 不回归；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): render markdown into the note views`
  → `git add Controls/NoteMarkdownRenderer.cs tests/XamlLoadCheck/Program.cs`

---

## Task T13: 阅读态（钻进 / 返回 / 任务框点击 / 滚动保持）

**Files：**
- Modify: `Controls/NotePanelView.xaml`、`Controls/NotePanelView.xaml.cs`
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces（pin 反射面）：`NotePanelView.OpenNote(NoteItem)` 私有 → 阅读态；`BackToList()` 私有；`_state` 枚举（List/Reading/Editing）；
  阅读 UI：头部行 [返回按钮 | 弹性 | 编辑按钮] + `ScrollViewer` > 只读 `RichTextBox`（`IsReadOnly=true`、`IsDocumentEnabled=true` 让 Hyperlink 可点）。
- Consumes: T12 `Build(blocks, style, callbacks)`；`NoteOps.ToggleTaskAt`；`ThemeManager`（高对比度）。

- [ ] **Step 1（红）：** XamlLoadCheck 加：
  - `note: a card click opens the reading view`（造卡 → 卡上 `RaiseEvent(MouseLeftButtonUp)` 风格或直调 `OpenNote` 反射 → `_state == Reading`、`RichTextBox.Document` 块数 > 0）。
  - `note: back returns to the list`（返回按钮 click → List；列表卡片仍在）。
  - `note: Backspace in the reading view goes back`（阅读态 `RaiseKey(window, view, richTextBox, Key.Back)` → List；对齐既有 RaiseKey 用法）。
  - `note: toggling a task rewrites the source line and re-renders`（阅读态点任务字形 → `note.Text` 含 `[x]`、字形结构翻转、`ModifiedAt` 前进）。
  - `note: task toggling keeps the list order`（该笔记升到最前——ModifiedAt 最新）。
  - `note: a reading view link carries its url`（`Hyperlink.NavigateUri` == 夹具 url）。
  运行 → 失败即红。留档。

- [ ] **Step 2（实现）：**
  - 视图三态骨架：`_state` + 两个容器（列表容器 / 阅读容器）切换可见性；头部返回/编辑按钮（`MenuBuilder` 风格或轻量 `Button`——按 TodoCalendarView 的按钮样式选最贴近的既有样式）。
  - `OpenNote`：记 `_current` → 渲染 `NoteMarkdownRenderer.Build(MarkdownParser.Parse(note.Text), style, callbacks)` 进 RTB → List→Reading。
  - `callbacks.ToggleTaskLine`：`note.Text = NoteOps.ToggleTaskAt(note.Text, line)` → `note.ModifiedAt = DateTime.Now` → 持久化 → 记 `ScrollViewer.VerticalOffset` → 重渲染 → `Dispatcher.BeginInvoke` 后 `ScrollToVerticalOffset`（保持滚动；离屏窗口视口为 0 时该路径无害，冒烟验证真实保持）。
  - `BackToList()`：Reading→List + `RebuildList()`（顺序刷新）。
  - Backspace：视图根 `PreviewKeyDown`（或 RTB 的 `PreviewKeyDown`）当 `_state == Reading && e.Key == Key.Back` → 返回 + `Handled`。
  - `style` 构造：`ThemeManager` 当前令牌 + `Utils.HexToColor(_data.Color)`；高对比度走 `ThemeManager.IsHighContrast`（照 todo 视图的取法）。
- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the note reading state`
  → `git add Controls/NotePanelView.xaml Controls/NotePanelView.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T14: 编辑态（新建流 / 提交 / 取消 / 丢弃 / 预览开关 / 面板收起提交）

**Files:**
- Modify: `Controls/NotePanelView.xaml`、`Controls/NotePanelView.xaml.cs`
- Modify: `Controls/NoteWidget.xaml.cs`（Closing 提交钩子）
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces（pin 反射面）：`NotePanelView.StartNewNote()` 私有（`＋` 与空白菜单共用）；`CommitPendingEdit()` public（面板收起/关闭时调用，幂等）；`CancelEdit()` 私有；`_editingBox`（源码 `TextBox`）、`_previewToggle`（[源码|预览] 段控）。
- Consumes: T12 渲染器（预览）、T9 `NoteEditorOps` 尚未接（T15）——本任务右键菜单不建。

- [ ] **Step 1（红）：** XamlLoadCheck 加（编辑器 pin 打法参照 TodoCalendarView 的 `InvokeViewEdit`/`RaiseKey` 家族——新写 `InvokeNoteEdit(view)`/`NoteSourceBox(view)` 辅助，命名随文件既有风格）：
  - `note: clicking add creates a note and opens its editor`（`StartNewNote` → 数据 +1、`_editingBox.Text == ""`、状态 Editing）。
  - `note: the blank-area menu opens the editor too`（空白菜单「新建」项 click → 同上）。
  - `note: typing then leaving commits the text and bumps the time`（box 文本 → `CommitPendingEdit` → 数据文本、`ModifiedAt >= 起始时刻`；状态回 List、该笔记在列表最上）。
  - `note: leaving without changes keeps the time`（不改文本 → ModifiedAt 不变）。
  - `note: Esc cancels the edit`（新文本不入数据、原文本保留；新建空笔记 Esc → 被丢弃）。
  - `note: an empty new note is discarded on leave`；`note: emptying an existing note discards it`。
  - `note: hiding the panel commits the pending edit`（进入编辑写文本 → `widget.HidePanel()` → 数据已提交）。
  - `note: the preview toggle shows the parsed text`（切预览 → 容器内出现 `FlowDocument`（RichTextBox）且 `_editingBox` 不可见；切回源码文本原样）。
  - `note: a preview task click rewrites the box text`（预览态点字形 → `_editingBox.Text` 含 `[x]`；数据尚未动——提交前不落库）。
  - `note: Backspace in the editor edits instead of leaving`（Editing 态 `RaiseKey(Key.Back)` 不切态）。
  运行 → 失败即红。留档。

- [ ] **Step 2（实现）：**
  - 编辑容器：头部 [返回（=提交） | 弹性 | 段控 [源码|预览] | 完成（=提交）] + `_editingBox`（`AcceptsReturn`、`TextWrapping`、对齐视图字体；`PreviewKeyDown` → Esc = `CancelEdit()`）+ 预览 RTB（复用 T12 builder，callbacks.ToggleTaskLine → 改 `_editingBox.Text` = `ToggleTaskAt` + 重渲染预览）。
  - `StartNewNote`：`new NoteItem { Id = Guid.NewGuid().ToString(), Text = "", CreatedAt = now, ModifiedAt = now }` → `_data.Notes.Add` → 编辑态（`_isNew = true`）。
  - `CommitPendingEdit()`：取 `_editingBox.Text`；Trim 后空 → 删该笔记（新/旧同规）→ 重建列表；否则文本有变才写回 + `ModifiedAt = now` → 持久化；退编辑→List；置 `_isNew = false`、清引用。幂等（无编辑态直接返回）。
  - `CancelEdit()`：`_isNew` → 移除该笔记；否则不动数据；退 List。
  - `NoteWidget.xaml.cs`：`Closing += (s, e) => _view?.CommitPendingEdit();`；`HidePanel` —— 先读基类是否 virtual：是 → override 先提交再 `base.HidePanel()`；否 → 视图侧订阅 `_host.IsVisibleChanged`（!IsVisible 时提交）。二选一按实际代码定，提交信息注明。
  - 空白/`＋` 的菜单或点击统一走 `StartNewNote`。
- [ ] **Step 3（绿）：** XamlLoadCheck 全绿；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the note editor and create flow`
  → `git add Controls/NotePanelView.xaml Controls/NotePanelView.xaml.cs Controls/NoteWidget.xaml.cs tests/XamlLoadCheck/Program.cs`

---

## Task T15: 编辑态右键快速插入菜单

**Files：**
- Modify: `Controls/NotePanelView.xaml.cs`（菜单构建 + 接线）；如菜单构建较长可拆 `Controls/NotePanelView.Editor.cs` partial（单一职责：插入菜单与编辑器操作）
- Modify: `Core/Localization.cs`（新 key 三语）
- Test: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Produces: `NotePanelView.BuildEditorMenu() -> ContextMenu`（私有；`_editingBox.ContextMenuOpening` 时重建）；
  菜单动作 = `ApplyEditorOp(Func<(string,int,int)> op)`：取 box 状态 → op → **整段替换写回**（`SelectionStart=0; SelectionLength=Text.Length; SelectedText = result.Text`——单条原生撤回单元）→ 设 `SelectionStart/Length` → `Focus()`。
- 新 key（三语；放置在各语言 `Menu_` 族合适位置）：`Menu_InsertHeading`（标题）、`Menu_InsertTable`（表格）、`Menu_InsertBullets`（无序列表）、`Menu_InsertNumbers`（有序列表）、`Menu_InsertTasks`（任务列表）、`Menu_InsertQuote`（引用）、`Menu_InsertCode`（代码块）、`Menu_InsertDivider`（分割线）、`Menu_FormatBold`（粗体）、`Menu_FormatItalic`（斜体）、`Menu_FormatStrike`（删除线）、`Menu_FormatCode`（行内代码）、`Menu_FormatLink`（链接）、`Menu_Cut`/`Menu_Copy`/`Menu_Paste`/`Menu_SelectAll`（若已存在则复用，不新增）；占位词 `Note_PlaceholderBold/Italic/Strike/InlineCode/Link`、表头 `Note_TableColumnFormat` = `"列 {0}"` / `"Column {0}"` / `"列 {0}"`。
- 标题子菜单 1-6 的显示名：`"标题 {0}"` / `"Heading {0}"` / `"見出し {0}"`（用 `Localization.Format("Menu_InsertHeading", level)`）。

- [ ] **Step 1（红）：** XamlLoadCheck 加（**不执行任何剪贴板命令**）：
  - `note: the source box menu offers the standard four as commands`（进入编辑态 → 反射取 `_editingBox.ContextMenu`（或触发 `ContextMenuOpening`）→ 前四项 `Command` 分别为 `ApplicationCommands.Cut/Copy/Paste/SelectAll`；仅断言，不触发）。
  - `note: the heading submenu writes a level`（取「标题」子菜单第 3 项 → `RaiseEvent(ClickEvent)` → box 文本 `"### x"`（夹具 `"x"`）。
  - `note: applying the same list twice removes it`（两次点「无序列表」→ 回原文）。
  - `note: the table entry lays out the skeleton`（夹具 `"x"`、光标在行尾（pos 1）→ 点「表格」→ 文本逐字符等于 `"x\n\n| 列 1 | 列 2 |\n| --- | --- |\n|  |  |"`、光标在首个体格——与 T9 的 InsertTable 非空行口径同源）。
  - `note: wrapping a selection bolds it`（选中 `"x"` → 点「粗体」→ `"**x**"` 且选区罩 `x`）；`note: an empty selection inserts the placeholder`。
  - `note: an insert is undoable with the box's own undo`（插入后 `box.CanUndo == true`；`box.Undo()` → 回原文本。**若此 pin 红：改走"逐行最小编辑"回填**——每行前缀单独用 `SelectedText` 插入，仍不整段 set `Text`；在实现记录中注明实际路径）。
  - `note: the editor menu follows the widget colour`（菜单宿主背景/淡染与组件色——按 MenuBuilder 既有 pin 语式断言一处即可）。
  运行 → 失败即红。留档。

- [ ] **Step 2（实现）：** 菜单构建按 §6.4 结构（标准四命令 + 分隔 + 标题子菜单 + 格式子菜单 + 平铺七项）；所有插入项走 `NoteEditorOps`（占位词取 `Localization`）；子菜单构建参照 `WidgetMenuBuilder.CreateItemSizeMenu` 的既有写法；菜单样式沿用 `MenuBuilder`（样式 A 自动生效，不改任何模板）。
- [ ] **Step 3（绿）：** XamlLoadCheck + LangCheck 全绿；build 0 警告。
- [ ] **Step 4（提交，1 笔）：**
  `feat(note): add the editor insert menu`
  → `git add Controls/NotePanelView.xaml.cs Core/Localization.cs tests/XamlLoadCheck/Program.cs`（若拆了新 partial 文件一并加上）

---

## Task T16: 文档（README 英/中 + CHANGELOG）

**Files：**
- Modify: `README.md`（英文功能段 + 中文功能段 + 两张用法表 + 「数据」小节兼容句）
- Modify: `CHANGELOG.md`（「Unreleased」）

- [ ] **Step 1：** README：
  - 英文功能列表在「Widget kinds」条目后新增 **Note widget** 条目：三态（list → reading → editor）、自动标题/时间/摘要卡片、Markdown 子集渲染（标题/列表/任务框/引用/代码块/表格/分割线/强调/链接）、任务框点击回写、右键快速插入、删除撤回；「Widget kinds」条目把「a folder or a todo」更新为「a folder, a todo or a note」。
  - 用法表（英）加行：`Create a note | Island ＋ → Note; the panel's ＋ row`、`Read a note | Click a card (Backspace / back returns)`、`Tick a task in a note | Click the box in the reading or preview view`、`Insert markdown fast | Right-click the source editor`。
  - 中文镜像同四处。
  - 「数据」小节（英/中）兼容句扩一档：v1.4.0 读 note 配置保留为未知类型不丢数据；回退 v1.3.x 走 `config.json.failed-*`（照 §8 措辞）。
- [ ] **Step 1b：** `Core/WidgetKindRegistry.cs` 类注释补「新增一个 kind 的触点清单」块注释（spec §7.2 的 9 行表压缩成 6~9 行注释；注明 `WidgetWindowFactory` / `IconRendererFactory.CreateByKey` / `WidgetKind.DataType` 已把大部分"HOW"吃掉），成为后续加组件的成文规范。
- [ ] **Step 2：** CHANGELOG「Unreleased / 新增」按用户可感知口径加两条（笔记组件一段；编辑器右键快速插入并入同段），不记框架收口（内部重构）。
- [ ] **Step 3（提交，1 笔）：** `docs: document the note widget` → `git add README.md CHANGELOG.md Core/WidgetKindRegistry.cs`

---

## 收尾（控制者执行，非任务）

- [ ] `tools\Run-Checks.ps1 -Group all`（32 行）全绿；`MenuRenderCheck` 若复发按观察项裁定；config-pin 误报先重跑。
- [ ] 计划文件追加"执行记录"，提交 `docs: record the note widget execution`。
- [ ] `tools\deploy.ps1` → **用户手动冒烟**（spec §9.3 十条）：①新建笔记组件与图标（经典/手绘、三语）；②新建→编辑→返回的卡片与置顶；③阅读态样式（深/浅/高对比度、组件色）与链接；④任务框双路径勾选与滚动保持；⑤右键快速插入全构造与 Ctrl+Z；⑥提交/取消/丢弃/面板收起不丢字；⑦删除撤回与排序；⑧v1.4.0 兼容；⑨文件夹/待办回归抽查；⑩全量自检。
- [ ] 用户确认后：合并 main + 推送（发布提交或 HEAD 带 `[ci]` 视需要）；worktree 按收尾规范清理。

---

## Self-Review

- **Spec 覆盖**：§3 模型/纯逻辑 = T5/T6/T8/T9；§4 子集 = T7/T8；§5 渲染 = T12/T13；§6 三态与快速插入 = T10/T11/T13/T14/T15；§6.4 菜单 = T15；§7 收口 = T1/T2/T3/T4/T5/T10；§8 兼容 = T5（未知类型保留 pin）+ T16（文档）；§9 测试 = 各任务 pin + T6 工程/矩阵 + 收尾；§10 风险对应缓解已内嵌（逐构造 pin / 收口回归 / undo 回退路径）。
- **占位符扫描**：无 TBD；尺寸初值（300×380 / 260×220）与滚动保持的冒烟移交均已显式写明。
- **类型一致性**：`NoteOps.TaskProgressOf` 仅在 T8 定义并在 T11 消费（T6 不预引）；`NoteRenderCallbacks.ToggleTaskLine` 贯穿 T12→T13→T14；`WidgetWindowFactory.Create` T2 定义/T10 扩表；editor op 返回三元组在 T9/T15 同名使用。
- **风险**：① 解析器边缘由逐构造 pin 锁定；② `TextBox.SelectedText` 整段替换的原生撤回若 pin 红，T15 已给"逐行最小编辑"回退路径；③ 面板收起提交的接线依赖基类 `HidePanel` 是否 virtual——T14 已给双路径与验证 pin。
