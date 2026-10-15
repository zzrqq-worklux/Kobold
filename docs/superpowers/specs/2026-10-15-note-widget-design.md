# Kobold 笔记组件（Note Widget，kind = `note`）设计

> **状态**：**待用户审阅**（2026-10-15 对话逐条定案后落文；审阅通过后按 writing-plans 出实施计划）。
> **决策依据**：2026-10-15 对话的 9 轮逐条定案（见 §0 决策记录）；代码走查 2026-10-15
> （`Core/WidgetDataConverter.cs`、`Core/WidgetKindRegistry.cs`、`Core/WidgetManager.cs`、
> `Core/PanelSizeMigration.cs`、`Core/AppConfig.cs`、`Controls/WidgetWindowBase.cs`、
> `Controls/IslandWindow.xaml.cs`、`Controls/IconRenderers/*`、`Helpers/WidgetMenuBuilder.cs`）。
> **相关文件**：`docs/paper-todo-learnings.md`（图片资产 / 插件体系不引入的既有结论）、
> `docs/superpowers/plans/2026-09-28-widget-plugins.md`（kind 框架来源）。

## 0. 决策记录（2026-10-15 用户逐条确认）

1. **形态**：一板多纸——一个组件面板 = 一份笔记列表；不做「一板一纸」，不做「每纸独立悬浮」。
2. **内容与编辑**：存 **Markdown 源码**；列表看渲染后摘要；编辑时源码/预览切换。图片 / 附件本轮不做
   （沿用 paper-todo-learnings：图片资产体系与当前规模不匹配）。
3. **阅读/编辑交互**：**钻进纸页**——点卡片进铺满面板的阅读视图（渲染全文），「编辑」进源码编辑器，
   返回键/返回按钮回列表；与文件夹浏览的下钻/返回同构。
4. **标题与卡片**：**自动标题**（首行推导）+ 修改时间 + 两行摘要；不设独立标题字段。
5. **Markdown 子集**：基础子集 + **可交互任务框**（渲染后点击勾选、回写源码）。
6. **引擎**：**全自研**——子集解析器 + FlowDocument 渲染器，零新依赖。
7. **右键快速插入**（用户追加）：编辑态源码框右键菜单提供 Markdown 构造的快速插入（标题、列表、
   任务、引用、代码块、**表格**、分割线、强调类）；因此 **表格纳入子集**（能快速插入就必须能渲染），
   推翻 §8 早先的「表格不做」。
8. **框架收口**（用户方向 2 的落地）：笔记是第三个 kind，同一轮把 kind 接线的三处机械分发
   （窗口工厂 / 图标分发 / 转换器与尺寸迁移）收口为注册表驱动，降低后续加组件的触点。
9. **本轮不改任何现有组件的行为**：文件夹/待办零回归是硬约束。

---

## 1. 目标与成功标准

1. 新增第三种组件 **笔记**（note）：新建菜单可选；岛 tile、面板、右键菜单、开机恢复与另两类一致。
2. 列表 → 阅读 → 编辑三态齐备；Markdown 子集渲染（含表格与可交互任务框）；右键快速插入可用。
3. 数据安全：编辑中的文本不因收起面板 / 退出 / 关机丢失；空笔记不累积。
4. 框架收益落地：新 `WidgetWindowFactory` + 图标 `CreateByKey` + 转换器/迁移注册表化；
   「新增一个 kind」的触点清单成文（§7）。
5. 全量自检不回归（31/31 → 32/32，新增 `NoteMarkdownCheck`），README（英/中）与 CHANGELOG 同步。

## 2. 非目标（明确不做）

- 图片 / 附件、表格以外的高级块（数学公式、脚注、HTML）、嵌套列表、嵌套引用（`>>` 按字面文本）。
- WYSIWYG 富文本编辑（编辑永远是 Markdown 源码；预览只读可点）。
- 搜索 / 过滤、标签、置顶 / 手动排序、导出 / 分享、跨组件引用 / 反链。
- 「几分钟前」相对时间（用本地化短日期时间格式，`ToString("g")` 族）。
- 笔记列表的键盘导航（本轮鼠标优先；与待办行一致，不加方向键）。
- 发版：版本、打包、Release 由用户另行决定。

---

## 3. 数据模型与纯逻辑（`Core/`）

### 3.1 模型

- `NoteData : WidgetData` —— `List<NoteItem> Notes`（对齐 `TodoData` 的形态）。
- `NoteItem { string Id; string Text; DateTime CreatedAt; DateTime ModifiedAt; }` ——
  `Text` 是 Markdown 源码全文；时间戳本地时间，显示格式由呈现层决定。
- JSON：`Kind = "note"`；`WidgetDataConverter` 写出 Kind 在前、字段在后（对齐既有约定）。
- 修复（`AppConfig` 修复路径，对齐 `RepairTodo`）：
  - `Notes == null` → 空列表；`Id` 缺失 → 生成；`Text == null` → 空串；
  - 不规范化时间戳（不做整日截断——与待办不同，笔记时间戳就是时间戳）。

### 3.2 纯函数（新 `Core/NoteOps.cs`）

```csharp
public static class NoteOps
{
    // 标题推导：首个非空行；剥行首 # 与行内标记；空笔记 → ""
    public static string TitleOf(string text);

    // 摘要：剥块/行内标记后的纯文本，取前 N 个字符（呈现层给 N；含换行压缩为空格）
    public static string ExcerptOf(string text, int maxChars);

    // 任务框回写：把第 lineIndex 行的 [ ]/[x] 翻转；返回新文本（原样不动其它行）
    public static string ToggleTaskAt(string text, int lineIndex);

    // 显示排序：ModifiedAt 倒序（LINQ 稳定序，同刻保持原顺序）
    public static List<NoteItem> SortForDisplay(IList<NoteItem> notes);

    // 卡片任务进度：统计渲染出的任务框数与已完成数（无任务 → null）
    public static (int done, int total)? TaskProgressOf(string text);
}
```

### 3.3 快速插入为纯函数（新 `Core/NoteEditorOps.cs`）

全部形如 `(string text, int selStart, int selLength) -> (string newText, int newSelStart, int newSelLength)`，
UI 只负责拿 TextBox 的当前状态调用并回填——**纯逻辑可逐构造 pin**：

- `InsertHeading(text, sel, level)`：当前行（多行选区 = 每行）设置 `#{level} `（替换已有标题标记）。
- `InsertBullets / InsertNumbers / InsertTasks`：列表族前缀的插入/替换；
  **目标构造同款前缀再次应用 = 整体移除**（toggle 撤回）。
- `InsertQuote`：`> ` 前缀；同款再次应用 = 移除。
- `InsertCodeBlock`：在块边界插入围栏骨架，光标进内部空行。
- `InsertTable`：插入 `列 1 | 列 2` 两列骨架（表头 + 分隔行 + 一行空体），占位词三语（§6.4）。
- `InsertDivider`：块边界插入 `---`。
- `WrapBold / WrapItalic / WrapStrike / WrapInlineCode`：有选区 → 包住选区；无选区 → 插入
  三语占位词并选中占位词。
- `WrapLink`：有选区 → `[选区](url)` 光标进括号；无选区 → 插入占位并选中占位文字。

---

## 4. Markdown 子集语法（封闭表）

块级（行首判定；空行分隔块）：

| 构造 | 语法 | 渲染 |
|---|---|---|
| 标题 | `#`~`######` + 空格 + 文本（行尾 `#` 剥除） | 六级字号阶梯 |
| 段落 | 连续非空行 | 软换行**按原样逐行显示**（中文笔记的习惯；非 CommonMark 空格合并——按设计偏离并 pin） |
| 无序列表 | 行首 `- ` 或 `* `（仅第 0 列；缩进标记按普通文本行） | • 列表 |
| 有序列表 | 行首 `1. `（任意数字；渲染自首项数字起顺序编号） | 数字列表 |
| 任务 | `- [ ]` / `- [x]` / `- [X]`（`]` 后须空格或行尾） | 可点勾选方框（§5.3） |
| 引用 | 连续 `> ` 行 | 左侧竖线 + 次要色（单层） |
| 代码块 | ` ``` ` 开/闭（InfoString 收集但不高亮） | 等宽、底色、换行显示不横向滚动 |
| 表格 | GFM 子集：表头行 + `\|---\|:---\|---:\|` 对齐分隔行 + 体行；首尾 `|` 可省 | FlowDocument 表格（§4.1） |
| 分割线 | 独立行 `---` / `***` / `___`（≥3 同字符） | 细分割线（**不做 setext 标题**，`---` 恒为分割线） |

行内（渲染在文本内；优先级：行内代码 → 链接 → 强调族）：

| 构造 | 语法 | 备注 |
|---|---|---|
| 行内代码 | `` `code` `` | 内部不再解析；等宽、底色 |
| 链接 | `[文字](url)` | **仅 http/https 可点**（系统默认浏览器）；其余按字面源码显示 |
| 粗体 | `**x**` / `__x__` | 简单配对规则，不做 CommonMark 全量（逐构造 pin 行为） |
| 斜体 | `*x*` / `_x_` | 同上；`_` 不处理词内规则 |
| 删除线 | `~~x~~` | 同上 |
| 转义 | `\` + 特殊字符（`\* \_ \~ \` \[ \] \| \# \> \- \\`） | 按字面显示 |

**未闭合 / 不受支持的语法**：一律按字面文本显示（不抛错、不吞字）。表格单元格内支持全部行内构造；
单元格数少于表头 → 空补，多于 → 忽略多余。

### 4.1 解析器与渲染器边界

- `Core/MarkdownParser.cs`（纯逻辑、无 WPF）：`Parse(text) -> List<MdBlock>`；
  块 = `Heading(level, inlines) / Paragraph(inlines) / ListBlock(items, ordered, start) /
  TaskItem(checked, inlines) / Quote(blocks) / CodeBlock(text) / Table(align[], rows[][]) /
  ThematicBreak`；行内 = `Text / Code / Link(text, url) / Strong / Em / Strike` 的简单 AST。
  **任务框记录源码行号**（回写要用，§3.2 `ToggleTaskAt`）。
- `Controls/NoteMarkdownRenderer.cs`（UI 侧）：AST → `FlowDocument`；承载在**只读 `RichTextBox`**
  （免费获得选中 / 复制；链接与任务框用内联元素挂事件）。

---

## 5. 渲染

### 5.1 样式

- 全部走主题令牌：标题字号阶梯（相对正文）、引用竖线 = 边框色、代码块 = 悬浮底色 + 等宽字、
  表格 = 1px 细分隔线（列对齐按分隔行 `:` 关系）、删除线、链接色（可点链接用组件色淡染）。
- 高对比度：全量回退系统色（沿用 `ThemeManager.IsHighContrast` 管线）。
- 字体：正文用组件文字字体（CJK 友好）；代码块 / 行内代码等宽。

### 5.2 链接

- 点击 → `ShellExecute` 打开默认浏览器（http/https）；悬停手型 + Tooltip 显示完整 URL。

### 5.3 可交互任务框

- 手绘风小方框（与待办勾选一族的手绘笔触；颜色随组件色，高对比度回退系统色）。
- 点击 → `NoteOps.ToggleTaskAt(text, line)` 回写源码 → 原地重渲染，**保持滚动位置**。
- 阅读态与编辑态预览都可点；编辑态源码框里直接改文字（两条路径都落同一份源码）。

---

## 6. 窗口与视图（`Controls/`）

### 6.1 结构

- `NoteWidget : WidgetWindowBase`（薄壳，对齐 `TodoWidget`）+ `NotePanelView`（XAML + 三态）。
- Chrome：标题栏显示**组件名**（不做笔记名上标题栏；重命名组件照常）。

### 6.2 三态

1. **列表态（首页）**：
   - 卡片 = `TitleOf` 标题（粗）+ 修改时间（`ToString("g")`）+ `ExcerptOf` 摘要（渲染至多两行）；
     有任务框时摘要行尾带 `✓ 2/5` 进度记号（可选小增强，随实现归档）。
   - 顶部 `＋` 新建行；单击卡片进阅读态。
   - 排序：`SortForDisplay`（最近编辑在上）。
   - 右键：卡片 → 行菜单（删除）；空白 → 新建。**Ctrl+Z**（焦点不在文本框）撤回删除
     （多步栈 ≤20，参照待办实现）。
   - 空列表：只显示 `＋`，无提示文案（与待办空日一致）。
2. **阅读态**：渲染全文（只读 RichTextBox）；「编辑」按钮、返回按钮 / **Backspace** 回列表。
3. **编辑态**：源码框（组件文字字体）+ 段控 `[源码|预览]` 开关；「完成」/返回 **提交**，
   **Esc 取消**（回滚本次编辑）；离场时文本为空（含仅空白）→ **丢弃**该笔记；`ModifiedAt` 仅在文本真变时更新。

### 6.3 新建与不丢字

- 点 `＋` → 建空笔记并**直接进编辑态**；返回后落列表顶部（刚编辑过 = 最新）。
- 收起面板 / 退出 / 关机：编辑态文本**先提交再走**（与待办编辑器的 blur 提交同族）。

### 6.4 编辑态右键快速插入菜单

- 菜单（圆润样式 A + 组件色淡染，`MenuBuilder` 家族）：标准四项（剪切/复制/粘贴/全选，复用既有
  本地化 key，缺则新增）+ 分隔线 + **标题 ▸**（1–6）+ **格式 ▸**（粗体/斜体/删除线/行内代码/链接）
  + 平铺：无序列表 / 有序列表 / 任务列表 / 引用 / 代码块 / 表格 / 分割线。
- 行为一律调 §3.3 纯函数回填；**插入应可被编辑器自带 Ctrl+Z 撤销**（实现备忘：用文本插入 API，
  不整段 set Text）。
- 无选区占位词与表格表头占位词（`列 1 / 列 2` 等）为三语 key（zh/en/ja 同提交）。

### 6.5 面板尺寸

- 默认 300×380、最小随实现核定（chrome + 一张卡片 + `＋` 行可读），`GetMinPanelSize()` override；
  `PanelSizeMigration` 补 note 分支（固定默认值）；两者进自检 pin。

---

## 7. 组件框架收口（kind 接线注册表化）

### 7.1 三处机械分发收口

1. **窗口工厂**：新 `Controls/WidgetWindowFactory.cs` —— `Dictionary<string kindId, Func<WidgetData, IWidgetWindow>>`
   注册 folder/todo/note；`WidgetManager.CreateWindow` 改查表；**未知 kind → null 的行为保留**
   （`WidgetKindRegistry.Find == null` 的既有短路不动）。
2. **图标分发**：`IconRendererFactory.CreateByKey(iconKey, style)`（"folder"/"todo"/"note" → 各自
   经典/手绘渲染器）；新增 `Controls/IconRenderers/NoteIconStyles.cs`（经典 + 手绘两版笔记图标，
   手绘跟随组件色 + 主题墨色，对齐现有两种图标的画法）；`IslandWindow.CreateTile` 删掉
   `Kind == "todo"` 特判，改走 `WidgetKindRegistry.Find(...).IconKey`。
3. **转换器与迁移注册表化**：`WidgetKind` 增字段 `Type DataType`（供 `WidgetDataConverter` 读路径
   与 `KindOf` 用），`WidgetDataConverter` 的 folder/todo 硬编码分支改为查注册表；
   `PanelSizeMigration` 加 note 分支（固定默认尺寸——不强行抽象三家的差异逻辑）。

### 7.2 收口后「新增一个 kind」触点清单（写进代码注释，成为后续规范）

| # | 触点 | 收口后动作 |
|---|---|---|
| 1 | `Core/XxxData.cs` | 新建数据模型 |
| 2 | `Core/WidgetKindRegistry.cs` | 登记（Id / NameKey / IconKey / DataType / CreateDefault） |
| 3 | `Controls/WidgetWindowFactory.cs` | 登记窗口工厂 |
| 4 | `Controls/IconRenderers/` | 新图标渲染器 + `CreateByKey` 映射 |
| 5 | `Core/PanelSizeMigration.cs` | 默认尺寸分支 |
| 6 | `Core/AppConfig.cs` | 修复分支（null 列表等） |
| 7 | `Controls/XxxWidget.xaml(.cs)` + 视图 | 窗口与界面 |
| 8 | `Core/Localization.cs` | 三语 key |
| 9 | 自检 + `Run-Checks.ps1` 矩阵 + README/CHANGELOG | 覆盖与文档 |

（1–7 里 2/3/4 已被注册表吃掉"HOW"，剩下的是"HOW MUCH"；`WidgetMenuBuilder` 的 per-kind 菜单
附加项与 `GetMinPanelSize` 的 per-kind 值保持现状钩子，不强行进注册表。）

---

## 8. 兼容性

- **前向**：v1.4.0 读到 `Kind = "note"` 的配置 → `UnknownData` 原样保留、不丢数据（既有机制，
  `WidgetKindsCheck` 已 pin；新增 note 用例）。
- **回退**：v1.3.0 / v1.3.1 读含 note 的配置 → 整个 `Widgets` 格式对它不可读 → 保留
  `config.json.failed-*` 并回默认设置（既有行为，README「数据」小节已述，本次照实补充 note 语境）；
  更早版本无该保护。与待办组件引入时的先例一致。
- **升级**：新增 kind 不迁移任何既有数据；旧配置加载后只在下一次保存时带上 `Widgets` 原状
  （既有解决方式，不动）。

## 9. 测试与验收

### 9.1 新自检工程 `NoteMarkdownCheck`（注册进 `Run-Checks.ps1` 矩阵，Ci=True）

- 解析器逐构造：标题（1–6、行尾 `#`、`#` 后无空格按段落）、段落软换行、列表（`-`/`*`/数字、
  缩进行按文本）、任务（`[ ]`/`[x]`/`[X]`、行号映射）、引用、代码块（未闭合围栏）、分割线、
  表格（对齐 `:`、列数不等、缺体行）、行内（代码优先于强调、未闭合按字面、转义、链接
  协议过滤）、空文本 / 纯空白。
- `NoteOps`：TitleOf（空 / 多行 / 带标记）、ExcerptOf（长度、换行压缩）、ToggleTaskAt
  （往返幂等、非任务行 no-op、`[X]`→`[x]` 规范化）、SortForDisplay（稳定序）、TaskProgressOf。
- `NoteEditorOps`：逐构造（无选区/有选区/多行/已带前缀 toggle 移除/光标与选区结果值）、表格骨架
  三语占位、边界（空文本、选区越界）。

### 9.2 既有自检扩展

- `WidgetKindsCheck`：note 配置往返、注册表三 kind、`DataType` 接线、迁移默认尺寸。
- `XamlLoadCheck`：NoteWidget 三主题加载、卡片构建（标题/时间/摘要/进度）、三态流转、
  任务框点击回写、删除 + Ctrl+Z、空笔记丢弃、快速插入菜单结构与插入结果、图标（经典 + 手绘）
  渲染、`WidgetWindowFactory` 分发（含未知 kind → null）。
- `LangCheck`：新 key 三语同步（含占位词与表格列名）。
- `MenuRenderCheck`：新菜单沿用样式 A（不新增模板，采样不回归）。

### 9.3 冒烟清单（用户手动）

1. 新建笔记组件（岛 `＋` → 笔记）→ 图标与名称正确（经典/手绘、三语）。
2. `＋` 新建 → 直接编辑：写标题/列表/任务/引用/代码块/表格 → 返回 → 卡片标题与摘要正确、置顶。
3. 阅读态：渲染样式（深/浅/高对比度、组件色）、链接可点、Backspace 返回。
4. 任务框：阅读态与预览态点击勾选/取消 → 源码同步（编辑态可见 `[x]`）、滚动位置保持。
5. 右键快速插入：各构造 ×（无选区/选区/多行）；同款再次点击移除；插入可 Ctrl+Z 撤回。
6. 编辑提交：完成/返回提交、Esc 取消、空文本丢弃；收起面板/退出不丢字。
7. 删除 + Ctrl+Z 多步撤回；排序（最近编辑在上）。
8. 旧版本兼容：v1.4.0 装 note 配置 → 组件出现在列表中、数据不丢。
9. 回归：文件夹/待办全部原有行为抽查（拖放/剪贴板/Enter 链/撤回栈）。
10. 全量自检 `Run-Checks.ps1 -Group all` 32/32。

## 10. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 自研解析器边缘 case（强调符、表格错位） | 子集封闭 + 逐构造 pin + 不支持即字面（不吞字）；行为以 pin 为准成文 |
| FlowDocument 超长笔记性能 | 典型笔记规模无虞；本轮不专门优化，冒烟留意，超长文本留作后续课题 |
| 任务框回写与源码编辑竞争 | 回写按"当前文本 + 行号"重算，不做缓存映射；解析每次新鲜产出 |
| 收口改动伤到 folder/todo | 三处收口各配既有 pin 的回归断言（WidgetKindsCheck/XamlLoadCheck 全量重跑） |
| 快速插入菜单过长 | 标题/格式进子菜单；平铺 7 项；长度按圆润样式冒烟定夺 |

## 11. 交付物

- `Core/`：`NoteData.cs`、`NoteItem`、`NoteOps.cs`、`NoteEditorOps.cs`、`MarkdownParser.cs`（+ AST）、
  注册表/转换器/迁移/修复的 note 接线与收口。
- `Controls/`：`NoteWidget.xaml(.cs)`、`NotePanelView`（三态 + 快速插入菜单）、
  `NoteMarkdownRenderer.cs`、`NoteIconStyles.cs`、`WidgetWindowFactory.cs`、图标与岛的分发收口。
- `tests/NoteMarkdownCheck/`（新）+ WidgetKindsCheck / XamlLoadCheck 扩展 + 矩阵登记。
- `Core/Localization.cs` 三语 key；README 英/中 + CHANGELOG（Unreleased）。
