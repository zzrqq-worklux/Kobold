# Kobold 组件面板自由缩放与自适应布局（方向 A）设计（草案）

> **状态**：草案 — 2026-10-10 起草，**待用户审阅；未实施**。方向 A 已由用户于 2026-10-09 批准
> （"A 更彻底；自由缩放后 column 和文件图标大小将废弃，但可借鉴复用现有代码"）。
> **与现有计划的关系**：本设计**不属于** `docs/superpowers/plans/2026-09-28-widget-plugins.md`
> （组件类型 / Todo 计划）。它取代该计划遗留的两件事：① 文件夹的 列数/图标大小/最大行数 手动设置；
> ② Todo 日历密度问题（用户已明确：不做便宜止血，由自由缩放 + 自适应正面解决）。
> **决策依据**：代码走查 2026-10-10（`Core/PanelRows.cs`、`Core/WidgetConstants.cs`、`Core/FolderData.cs`、
> `Core/AppConfig.cs`、`Controls/FolderWidget.*`、`Controls/WidgetWindowBase.cs`、`Controls/WidgetChrome.*`、
> `Controls/TodoWidget.*`、`Controls/TodoCalendarView.*`、`Helpers/WidgetMenuBuilder.cs`）；
> 用户 2026-10-09/10 的方向要点。

## 1. 目标

1. 每个组件面板（folder / todo）**原生自由拖动缩放**（拖边缘/角），尺寸属于该组件、随组件持久化。
2. **通用自适应引擎**：面板内容尺寸 → 布局参数（列数、单元格/图标尺度、可见行数、Todo 日历格高与
   第二行小字显隐）；纯函数、进控制台自检。
3. **取代文件夹的三个手动设置**（列数 / 图标大小（全局 ItemScale） / 最大行数）：菜单入口移除；
   旧字段只作一次性迁移输入。
4. **Todo 日历密度**由自适应解决：第二行小字（农历/节假日，zh）在空间足够时显示、不足时隐藏；
   日历与清单随面板尺寸伸缩。
5. **复用现有布局代码**：`PanelRows`、`WidgetConstants`（72/85/4/40/16/0.6/20）、FolderWidget 的
   尺度/尺寸公式、TodoCalendarView 的格高/行高/字号常量 → 收敛为纯函数适配器。

成功标准：拖动缩放的即时反馈符合预期（含临界档位）；重启后尺寸恢复；锁定/DPI/多屏行为与现有
面板机制一致；全量自检覆盖引擎与迁移；升级用户的旧设置一次性换算为初始尺寸（观感可接受）。

## 2. 非目标（明确不做）

- 不做的岛与系统 tile 的自适应/缩放（岛维持固定几何）。
- 不做第三方动态插件、新组件类型、天气等（沿用既有结论）。
- 不改 Todo 数据语义（顺延/完成/级联/一层约束）与月历导航行为；不改文件夹浏览/拖拽/收纳逻辑。
- 不做每组件独立的字号/间距偏好面板——**尺寸即唯一布局输入**。
- 本设计不含发版；版本、打包、Release 由用户另行决定。

## 3. 交互设计

### 3.1 缩放手柄与拖动协议

- **手柄**：右/左/上/下四条 5 DIP 隐形边缘带 + **右下角 16×16 常显 grip**（低对比度、悬停提亮）；
  仅当组件未锁定（`IsLocked == false`）时参与命中测试与光标反馈。
- **光标**：右下角 = `SizeNWSE`；左右边 = `SizeWE`；上下边 = `SizeNS`；进入边缘带设置、离开恢复。
- **协议**（复用 Header 拖动的既有模式）：`Mouse.Capture` + 物理像素增量 ÷ 当前 DPI 求每步增量，
  实时更新 `Width/Height`（每步 clamp 到最小/最大值并即时重排）；松手后 `FitPanelToWorkArea()`、
  持久化尺寸、`SaveConfig`（既有防抖）。拖动中不写盘。
- **锁定**：锁定中的面板无手柄、无光标、不可缩放（与"锁定后不可移动"一致）。
- Header 拖动（移动）与边缘拖动（缩放）由各自的 capture 互斥，天然不并发。
- 手柄属于共享壳（`WidgetChrome`），两类组件经 `WidgetWindowBase` 获得同一能力。

### 3.2 尺寸边界

- **最小**（保证内容可读；最终值实现时定，自检 pin）：
  - folder：至少 1 列 × 1 行可见（含截断页脚时相应加高）≈ 180 × 137~157；
  - todo：日历格宽 ≥ ~36 DIP（内容宽 ≥ ~300）；高度含 6 周月历 + 1 行清单 ≈ 320。
- **最大**：面板所在屏的工作区尺寸（拖动过程即 clamp；松手 `FitPanelToWorkArea` 复核）。

### 3.3 尺寸持久化与迁移

- `WidgetData` 新增：`double? PanelWidth` / `double? PanelHeight`（DIP，窗口整体尺寸；与既有
  `PanelX/PanelY/PanelDpiScale` 同族）。
- 读取：面板打开时若已有尺寸 → 直接使用；缺失（升级用户 / 新建组件）→ 一次性迁移计算（§4.4），
  随后写回。迁移只发生一次。
- 新组件默认尺寸沿用今日观感：folder = 3 列 × 最多 3 行 × ItemScale 1.1 的换算尺寸；
  todo = 342 × 当前自然高度。
- **旧字段处置**：`FolderData.GridColumns` / `FolderData.MaxPanelRows`、`AppConfig.ItemScale` 保留为
  只读迁移输入；菜单入口移除；写入时保留原值（不主动清除，最小风险）；引擎不再消费。

### 3.4 与现有机制的交互

- 打开/恢复：`ShowPanel` 使用持久化尺寸（缺失则迁移值）；DPI/多屏恢复沿用 `PanelPlacement`。
- **数据变化不再自动改尺寸**（现状：folder 高度随行数收放、todo 随内容/换日收放）——超出由滚动条
  承担。这是本方向的行为变化，README/CHANGELOG 明示、冒烟确认。
- `FitPanelToWorkArea` 在松手与尺寸变更后调用（沿用）。
- 透明度/主题/语言刷新/打开动画/Alt+Tab/锁钉逻辑不变；岛不重建；尺寸写回不进数据列表
  （`OnDataChanged` 只走既有保存路径）。

## 4. 自适应引擎（通用）

### 4.1 形态

`Core/PanelLayoutAdapter.cs`（名待定；纯函数、无 WPF 依赖）：

```csharp
public static class PanelLayoutAdapter
{
    public static FolderLayout AdaptFolder(double contentWidth, double contentHeight, int itemCount);
    public static TodoLayout AdaptTodo(double contentWidth, double contentHeight, int weekCount, bool labelsAvailable);
    public static (double w, double h) MigrateFolderSize(int gridColumns, int maxPanelRows, int itemCount, double itemScale);
    public static (double w, double h) DefaultTodoSize(int weekCount);
}

public struct FolderLayout { public int Columns; public double Scale; public int VisibleRows; public bool Scrolls; public double CellWidth; public double CellHeight; }
public struct TodoLayout { public double CellHeight; public bool ShowDayLabels; public int ListRows; public bool ListScrolls; }
```

- **复用映射**：`PanelRows.VisibleRows/NeedsScroll` 原样保留调用；`WidgetConstants` 常量直接消费；
  FolderWidget 的 `GetScaledItemWidth/Height` 公式迁入引擎（UI 侧删除复制，单一来源）；
  TodoCalendarView 的 `CELL_HEIGHT=28 / ROW_HEIGHT=30 / PARENT_FONT=14 / CHILD_FONT=13 /
  MIN_LIST_ROWS=1 / MAX_LIST_ROWS=6` 成为引擎输出与渲染输入。

### 4.2 Folder 策略（提案 P1：图标随宽放大到上限，再加列）

- `columns = max(2, ceil(contentWidth / MAX_CELL_W))`，`MAX_CELL_W ≈ 112`（基准格 76 的 ~1.45 倍）；
- `scale = clamp((contentWidth − padding) / columns / 76, 0.9, 1.45)`；
- `cellW = ceil(76 × scale)`、`cellH = ceil(89 × scale)`；
- `visibleRows = max(1, floor((contentHeight − padding) / cellH))`；
- `scrolls = itemCount > columns × visibleRows`（沿用 `PanelRows` 语义）。
- 效果：拉宽 → 图标连续变大，到临界后多出一列、图标回落；拉高 → 行数增多；缩小 → 列/行减少、滚动。
- 备选 P2：先加列、列到上限再放大。**待用户确认（§9-3）**。

### 4.3 Todo 策略（提案）

- **宽度**：日历 7 格均分内容宽、清单同宽；文字/数字保持居中。
- **高度分配**（提案，待用户确认 §9-4）：清单优先满足到自然行数（`clamp(自然行, 1, 6) × 30`），
  剩余高度给月历格高：`cellH = clamp(可用日历高 ÷ weekCount, 22, 36)`。
- **第二行小字**：`ShowDayLabels = cellH ≥ 32`（zh 且数据可用时；< 32 隐藏）。宽度兜底：
  内容宽 < 260 时也隐藏。**日历密度问题由此解决**。
- **清单**：行数 = `clamp(floor(清单高 ÷ 30), 1, 6)`；超出滚动；完成置底/顺延/编辑语义不变。
- 面板尺寸固定：换日/增删条目不再触发尺寸变化（`RequestContentResize` 的自动尺寸改动移除）。

### 4.4 迁移公式

- folder：
  - `w = GridColumns × ceil(76 × ItemScale) + 16 + 2`；
  - `h = 40 + VisibleRows(itemCount, GridColumns, MaxPanelRows) × ceil(89 × ItemScale) + 16`
    （+ 20 截断页脚，按 `NeedsScroll` 估算）。
- todo：`w = 342`；`h = 40 + 自然内容高`（月历按 28 格高、清单按 `clamp(条目行, 1, 6)`）。
- 迁移为纯函数（§4.1 签名），自检覆盖典型用例（3 列/1.1 倍、4 列/1.3 倍、截断/不截断）。

## 5. 数据模型（汇总）

| 字段 | 位置 | 读 | 写 | 备注 |
|---|---|---|---|---|
| `PanelWidth` / `PanelHeight` | WidgetData | 缺失 → 迁移计算 | 缩放松手写回 | DIP；与 PanelX/Y 同族 |
| `GridColumns` / `MaxPanelRows` | FolderData | 迁移输入 | 保留原值 | 引擎不再消费；菜单移除 |
| `ItemScale` | AppConfig | 迁移输入 | 保留原值 | 全局；菜单移除 |

## 6. 测试与验证

- 新纯逻辑工程 `tests/AdaptiveLayoutCheck`（登记 `tools/Run-Checks.ps1`，CI 子集）：引擎边界
  （列/尺度/行数/滚动、单调性）、迁移公式（典型例）、Todo 高度分配与小字阈值、尺寸 clamp。
- `XamlLoadCheck` 扩展：手柄存在与光标映射、锁定禁用、拖动更新尺寸并重排、松手持久化、
  迁移默认值、Todo 小字随尺寸显隐（离屏 pin，不驱动真实鼠标）。
- `WidgetKindsCheck` / `MenuRenderCheck` 跟进：能力标志与菜单项移除后的断言更新；本地化死键清理
  （`Menu_GridSize/Menu_Columns/Menu_MaxRows/Menu_Rows/Menu_ItemSize/Size_*`），三语同步（LangCheck 绿）。
- 手动冒烟（用户）：拖动观感（含临界档位切换）、锁定、重启恢复、最小/最大、主题/透明度、
  folder 与 todo 全交互回归、Todo 小字显隐（zh）。混合 DPI/多屏受单屏限制，如实声明。
- 回归：`Run-Checks.ps1 -Group all` 全绿（矩阵增量 = AdaptiveLayoutCheck）。

## 7. 风险与未验证项

- **行为变化**（面板不再自动收放）可能让老用户觉得"面板不贴内容"——README/CHANGELOG 说明 + 冒烟。
- **P1 档位切换**（加列时图标回落）可能被认为跳动——备选 P2 / 滞回；由冒烟定。
- 迁移初值与旧观感存在一次性偏差（之后完全由用户拖拽决定）。
- 缩放命中测试（边缘带）在小面板/高 DPI 下的可用性；尺寸以 DIP 持久化，跨 DPI 恢复依赖既有机制。
- 滚动条出现/消失导致内容宽微变——引擎实时适配，可接受。
- 混合 DPI / 多屏人工验收受本机单屏限制，按既有记录如实声明。

## 8. 实施计划（草案概要）

| 阶段 | 内容 | 验证 |
|---|---|---|
| 1 | 纯自适应引擎 + 迁移函数 + `tests/AdaptiveLayoutCheck`（登记矩阵） | 新工程全绿 + 全量回归 |
| 2 | 缩放交互与尺寸持久化（Chrome 手柄、基类协议、WidgetData 字段、迁移接线） | XamlLoadCheck pins + 手动冒烟 |
| 3 | Folder 接入引擎（菜单项移除、列数/尺度由引擎驱动） | 全量回归 + 手动冒烟 |
| 4 | Todo 接入引擎（格高/小字显隐/清单行数、去自动改尺寸） | 全量回归 + 手动冒烟 |
| 5 | README/CHANGELOG + 全量矩阵 + 终审 + finishing | 文档审阅 + 全绿 |

任务拆分与提交信息见 `docs/superpowers/plans/2026-10-10-widget-adaptive-layout.md`（草案；
**待本 spec 获用户批准后再细化执行**）。

## 9. 待用户审阅的决策点

1. **手柄布局**：四边隐形带 + 右下角常显 grip（提案）；还是仅右下角？锁定即禁缩放（提案：是）。
2. **面板尺寸是否完全固定**（内容变化不再自动收放、滚动条承担）？提案：固定；迁移首见尺寸 = 旧观感。
3. **Folder 档位策略**：P1（图标先放大到上限、再加列，临界 ≈112）还是 P2（先加列）？临界值可调。
4. **Todo 高度分配**：清单先到自然行数、再放大月历（提案）还是月历优先？
5. **小字显隐阈值**：格高 ≥ 32 显示（提案）；是否加宽度兜底（< 260 隐藏）？
6. **旧字段写出**：保留原值（提案）还是停止写出？
7. **迁移观感一次性差异**是否接受（例：3 列 / 1.1 倍的面板迁移后尺寸固定、不随条目数收放）？
