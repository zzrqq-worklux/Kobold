# 浏览目录外部文件拖入实施计划（Explorer · 企业微信 FileDrop）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **状态：已执行（2026-10-21）；最终终审使用官方 `deepseek/deepseek-flash`。**
> **Spec**：`docs/superpowers/specs/2026-10-21-external-file-drop-design.md`（本计划只引 §号，不复述决策）。
> **基线**：`main` @ `d437b36`；worktree：`C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop`；分支 `external-file-drop`。
> **执行方式**：subagent-driven-development。每任务：实现子代理 TDD → 按 pathspec `git -C <worktree> add`（不提交）→ 控制者 `git -C <worktree> stash create` 快照 + 审查包 → 任务审查 → 修复 / 复审 → 复审通过后由实现子代理按计划信息提交。
> **子代理模型**：官方 `deepseek/deepseek-flash`。实现与审查同一模型。
> **子代理命令**：一律绝对路径 / `git -C <worktree>`；**禁止 `Set-Location`**（实测会让子代理会话假死）。
> **会话位置**：控制者会话驻留 `C:\Users\31177347\Desktop\Coding`（技能所在仓库）；所有工作树操作使用绝对路径。

**Goal：** 让 Explorer / 企业微信的外部 `FileDrop` 能落进“从 Widget 打开的文件夹”：拖到空白复制到当前目录，拖到文件夹行复制到该子文件夹；根 Widget 的引用行为保持不变。

**Architecture：** 纯策略 `Core/ExternalDropPolicy` 裁决 browse 落点（接不接受、复制到哪）；`FolderWidget.DragDrop` 在 `ItemsContainer` / `BrowseItem` / `ContentPanel` 三层接住外部 `FileDrop`，统一走 `ShellFileOperations.Copy`。不做虚拟文件 / COM / 新依赖。

**Tech Stack：** WPF / .NET Framework 4.8 / C# 7.3；控制台自检（`tests/*`，`Run-Checks.ps1` 矩阵 32 行不变）。

---

## Global Constraints

- C# 7.3 / net48；零新依赖；构建 0 警告。
- TDD：每任务先写失败 pin（编译失败或运行时失败均可）→ 最小实现 → 相关自检全绿；红测 / 绿测输出写进执行记录。
- 不新增 check 工程；矩阵维持 30 check + config-pin + installer = 32。
- 自检内**禁止读写系统剪贴板**、**禁止驱动真实鼠标**；真实拖放由冒烟覆盖。
- 时间戳夹具一律显式传入；断言不得依赖 `DateTime.Now`。
- 本轮**不新增三语 key**；`LangCheck` 零变化。
- hex 字面量只许在 `Core/UiTokens.cs`；本轮不新增颜色值。
- 每个提交独立可编译、可回退；先任务审查后提交。
- 合并 / 推送 / 部署必须先问用户。
- 子代理命令一律绝对路径 / `git -C <worktree>`；禁止 `Set-Location`。
- 临时探针只存在于 Task 1，最终删除 / 不入库。
- 离屏无法验证的项（真实 OLE 拖放、shell 复制对话框）如实标注为冒烟覆盖，不设假 pin。

## 任务依赖图

```
T1 诊断探针（已完成取证；删除探针）
T2 ExternalDropPolicy 纯策略 + pins
T3 UI 接线 + XAML + XamlLoadCheck pin（依赖 T2）
T4 文档 + 全量自检 + 执行记录
```

---

## Task 1: 诊断探针（已完成，剩余删除）

**Files：**
- Modify（临时）：`Controls/FolderWidget.DragDrop.cs`

**状态：** 探针已由实现子代理添加、Release 构建 0 警告、经用户同意部署并完成 Explorer / 企微两次真实拖放；证据已写入 SDD ledger 与 spec §0。现在只删除探针。

### Step 1: 删除探针

删除 `ProbeDrop` 方法，以及 `ItemsContainer_DragOver` / `ItemsContainer_Drop` 开头的两行
`ProbeDrop(...)` 调用与 `// TEMP PROBE` 注释。不要改其他代码。

### Step 2: 确认差异干净

Run:
```
git -C "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop" diff --stat
```
Expected: 无输出（探针是唯一改动，删除后工作树干净）。

### Step 3: 不提交

- 探针不入库；Task 1 结束。

---

## Task 2: `ExternalDropPolicy` 纯策略

> **注：Task 2/3 的代码片段是修复前版本；最终实现增加了 `InternalDragFormats` / `FromFormats`，见执行记录。**

**Files：**
- Create: `Core/ExternalDropPolicy.cs`
- Modify: `tests/DragOutCheck/Program.cs`

**Interfaces：**
- Produces（Task 3 消费）：
  ```csharp
  public static class ExternalDropPolicy
  {
      public static bool Accepts(bool browsing, bool locked, bool hasFileDrop, bool overFileRow);
      public static string TargetDirectory(bool overFolderRow, string folderRowPath, string currentBrowsePath);
  }
  ```

### Step 1: 红测（`tests/DragOutCheck`）

在 `Program.Main` 的 `LockedWidgetsNeverGiveItemsUp();` 后加：

```csharp
ExternalDropPolicyPins();
```

在 `Program` 里加：

```csharp
private static void ExternalDropPolicyPins()
{
    Check(ExternalDropPolicy.Accepts(true, false, true, false),
        "external drop: the browse background accepts a file drop");
    Check(ExternalDropPolicy.Accepts(true, false, true, false) &&
          ExternalDropPolicy.TargetDirectory(true, @"C:\a\sub", @"C:\a") == @"C:\a\sub",
        "external drop: a folder row accepts and targets the subfolder");
    Check(!ExternalDropPolicy.Accepts(true, false, true, true),
        "external drop: a file row refuses");
    Check(!ExternalDropPolicy.Accepts(true, true, true, false),
        "external drop: a locked widget refuses");
    Check(!ExternalDropPolicy.Accepts(true, false, false, false),
        "external drop: a non-file payload refuses");
    Check(!ExternalDropPolicy.Accepts(false, false, true, false),
        "external drop: root mode never uses the browse policy");
    Check(ExternalDropPolicy.TargetDirectory(true, null, @"C:\a") == @"C:\a" &&
          ExternalDropPolicy.TargetDirectory(false, @"C:\a\sub", @"C:\a") == @"C:\a",
        "external drop: a blank folder row falls back to the current folder");
}
```

### Step 2: 运行 → 红

Run:
```
dotnet run --project "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop\tests\DragOutCheck\DragOutCheck.csproj" -c Debug --nologo
```
Expected: 编译失败，`ExternalDropPolicy` 不存在。记录失败输出。

### Step 3: 实现 `Core/ExternalDropPolicy.cs`

```csharp
using System;

namespace Kobold.Core
{
    /// <summary>
    /// The browse-mode verdict for an external FileDrop: accept/copy or refuse.
    /// Root-mode drops are handled by the existing root path and never call this.
    /// </summary>
    public static class ExternalDropPolicy
    {
        /// <summary>
        /// True when a browse-mode external FileDrop should be accepted.
        /// File rows refuse (only folder rows and the background accept);
        /// a locked widget refuses everything.
        /// </summary>
        public static bool Accepts(bool browsing, bool locked, bool hasFileDrop, bool overFileRow)
        {
            return browsing && !locked && hasFileDrop && !overFileRow;
        }

        /// <summary>
        /// The folder a browse-mode external drop copies into: the folder row
        /// under the pointer when there is one, otherwise the current folder.
        /// </summary>
        public static string TargetDirectory(bool overFolderRow, string folderRowPath, string currentBrowsePath)
        {
            return overFolderRow && !string.IsNullOrEmpty(folderRowPath)
                ? folderRowPath
                : currentBrowsePath;
        }
    }
}
```

### Step 4: 运行 → 绿

Run the DragOutCheck command from Step 2.
Expected: 新增 7 条 PASS，旧 9 条 PASS，`ALL DRAG-OUT CHECKS PASSED`。

### Step 5: 暂存 + 提交

```
git -C "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop" add Core/ExternalDropPolicy.cs tests/DragOutCheck/Program.cs
git -C "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop" commit -m "feat(drop): decide external file drops in the browser"
```

---

## Task 3: UI 接线 + XAML + XamlLoadCheck pin

> **注：Task 2/3 的代码片段是修复前版本；最终实现增加了 `InternalDragFormats` / `FromFormats`，见执行记录。**

**Files：**
- Modify: `Controls/FolderWidget.DragDrop.cs`
- Modify: `Controls/FolderWidget.xaml`
- Modify: `tests/XamlLoadCheck/Program.cs`

**Interfaces：**
- Consumes: Task 2 的 `ExternalDropPolicy`.
- Produces: browse-mode external `FileDrop` acceptance on background / folder row / empty state.

### Step 1: 红 pin（XAML 落点）

In `XamlLoadCheck` `Main`, after `failures += TryCheck("folder: the browse back button accepts drops", BrowseBackButtonAcceptsDrops);`, add:
```csharp
failures += TryCheck("folder: the content panel accepts external drops", FolderContentPanelAcceptsDrops);
```
Add the method near `BrowseBackButtonAcceptsDrops`:
```csharp
private static void FolderContentPanelAcceptsDrops()
{
    var widget = new FolderWidget(new FolderData());
    var content = widget.FindName("ContentPanel") as Grid;
    var scroller = widget.FindName("ItemsScroller") as ScrollViewer;
    var empty = widget.FindName("EmptyState") as FrameworkElement;
    if (content == null || !content.AllowDrop)
        throw new InvalidOperationException("ContentPanel does not accept drops");
    if (scroller == null || !scroller.AllowDrop)
        throw new InvalidOperationException("ItemsScroller does not accept drops");
    if (empty == null || empty.IsHitTestVisible)
        throw new InvalidOperationException("EmptyState still blocks drops");
}
```
Run XamlLoadCheck → fail (`ContentPanel.AllowDrop` false / EmptyState hit-test true). Record.

### Step 2: XAML

`Controls/FolderWidget.xaml`:

- `<Grid x:Name="ContentPanel">` →
```xml
<Grid x:Name="ContentPanel" AllowDrop="True" Background="Transparent"
      DragOver="ContentPanel_DragOver" DragLeave="ContentPanel_DragLeave"
      Drop="ContentPanel_Drop">
```
- `<ScrollViewer x:Name="ItemsScroller"` add `AllowDrop="True"` (keep all existing attributes).
- `<StackPanel x:Name="EmptyState"` add `IsHitTestVisible="False"`.

### Step 3: 代码

In `Controls/FolderWidget.DragDrop.cs`:

1. Add helpers near `AddExternalPaths`:

```csharp
/// <summary>True when the drag carries real files (the only external payload this round accepts).</summary>
private static bool IsExternalFileDrop(IDataObject data)
{
    return data != null && data.GetDataPresent(DataFormats.FileDrop);
}

/// <summary>
/// Copies a browse-mode external FileDrop into targetDir through the shell,
/// so collisions keep both files and progress/cancel stay Explorer-like.
/// </summary>
private void CopyExternalFileDrop(DragEventArgs e, string targetDir)
{
    if (_data.IsLocked || string.IsNullOrEmpty(targetDir))
    {
        e.Effects = DragDropEffects.None;
        return;
    }

    var paths = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
    if (paths == null || paths.Length == 0)
    {
        e.Effects = DragDropEffects.None;
        return;
    }

    bool copied = ShellFileOperations.Copy(paths, targetDir, PanelHwnd);
    e.Effects = copied ? DragDropEffects.Copy : DragDropEffects.None;

    // Deferred so the re-list does not run inside the OLE drag loop.
    Dispatcher.BeginInvoke(new Action(UpdateUI));
}
```

2. `ItemsContainer_DragOver`: in the `if (IsBrowsing)` branch, before the existing
   `IsBrowseDrag` check, add:

```csharp
if (IsExternalFileDrop(e.Data))
{
    bool accepted = ExternalDropPolicy.Accepts(true, _data.IsLocked, true, false);
    e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
    SetBrowseDropHighlight(accepted);
    DropIndicator.Visibility = Visibility.Collapsed;
    if (!accepted) DragDropSession.Current?.MarkTargetRefused();
    e.Handled = true;
    return;
}
```

3. `HandleBrowseDrop`: at the top of the method, add:

```csharp
if (IsExternalFileDrop(e.Data))
{
    CopyExternalFileDrop(e, CurrentBrowsePath);
    return;
}
```

4. `BrowseItem_DragOver`: after `var target = border?.DataContext as DisplayItem;` and before
   the existing `bool accepted = ...` line, add:

```csharp
if (IsExternalFileDrop(e.Data))
{
    bool accepted = ExternalDropPolicy.Accepts(
        true, _data.IsLocked, true, !(target != null && target.IsDirectory));
    e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
    if (accepted) SetItemDropHighlight(border);
    else
    {
        ClearItemDropHighlight();
        DragDropSession.Current?.MarkTargetRefused();
    }
    return;
}
```

5. `BrowseItem_Drop`: after `var target = border?.DataContext as DisplayItem;` and before the
   existing `bool accepted = ...` line, add:

```csharp
if (IsExternalFileDrop(e.Data))
{
    bool accepted = ExternalDropPolicy.Accepts(
        true, _data.IsLocked, true, !(target != null && target.IsDirectory));
    if (!accepted)
    {
        e.Effects = DragDropEffects.None;
        return;
    }
    CopyExternalFileDrop(e, ExternalDropPolicy.TargetDirectory(
        true, target.Path, CurrentBrowsePath));
    return;
}
```

6. Add the `ContentPanel` handlers:

```csharp
/// <summary>The empty-state / panel-gap drop target: only browse mode takes external files.</summary>
private void ContentPanel_DragOver(object sender, DragEventArgs e)
{
    if (e.Handled || !IsBrowsing || !IsExternalFileDrop(e.Data)) return;

    bool accepted = ExternalDropPolicy.Accepts(true, _data.IsLocked, true, false);
    e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
    e.Handled = true;
}

private void ContentPanel_DragLeave(object sender, DragEventArgs e)
{
    SetBrowseDropHighlight(false);
    ClearItemDropHighlight();
}

private void ContentPanel_Drop(object sender, DragEventArgs e)
{
    if (e.Handled || !IsBrowsing || !IsExternalFileDrop(e.Data)) return;

    if (_data.IsLocked) e.Effects = DragDropEffects.None;
    else CopyExternalFileDrop(e, CurrentBrowsePath);
    e.Handled = true;
}
```

7. **根模式不动**：`ItemsContainer_Drop` 末尾的
   `if (e.Data.GetDataPresent(DataFormats.FileDrop)) { ... AddExternalPaths ... }`
   保持原样；不要把外部 FileDrop 提到根分支前面。

### Step 4: 验证

Run:
```
dotnet build "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop\Kobold.csproj" -c Debug --nologo
dotnet run --project "...\tests\LangCheck\LangCheck.csproj" -c Debug --nologo
dotnet run --project "...\tests\DragOutCheck\DragOutCheck.csproj" -c Debug --nologo
dotnet run --project "...\tests\XamlLoadCheck\XamlLoadCheck.csproj" -c Debug --nologo
```
Expected: build 0 warnings; LangCheck unchanged; DragOutCheck includes the 7 new pins;
XamlLoadCheck includes the new drop pin and all existing pins PASS.

### Step 5: 暂存 + 提交

```
git -C "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop" add Controls/FolderWidget.DragDrop.cs Controls/FolderWidget.xaml tests/XamlLoadCheck/Program.cs
git -C "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop" commit -m "feat(drop): copy external files into a browsed folder"
```

---

## Task 4: 文档 + 全量自检 + 执行记录

**Files：**
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/plans/2026-10-21-external-file-drop.md`
- Modify: `docs/superpowers/specs/2026-10-21-external-file-drop-design.md`（状态 → 已执行）

### Step 1: README

English (add to the existing “Drag & drop everywhere” / “Drop into a folder” area):

```markdown
- **Drop into a browsed folder** — an external file dropped on the panel
  blank copies into the current folder; dropped on a folder row it copies
  into that subfolder. Explorer-style `(1)` names keep both copies.
- **Enterprise WeChat** — its FileDrop (a WeWork cache path) follows the
  same rules: the widget root keeps a reference, a browsed folder gets a
  copy.
```

Chinese:

```markdown
- **拖入浏览目录** — 外部文件拖到面板空白 → 复制进当前目录；拖到文件夹行 →
  复制进该子文件夹；重名按 Explorer 习惯生成 `(1)`，不覆盖。
- **企业微信** — 企微的 FileDrop（WXWork 缓存路径）遵循同一规则：组件根目录保持引用，
  浏览中的文件夹复制一份。
```

### Step 2: CHANGELOG

Under `## Unreleased` → `### 修复` add:
```markdown
- 修复企业微信等外部文件拖入“从 Widget 打开的文件夹”时显示禁止光标、不复制的问题：
  浏览空白 → 当前目录，文件夹行 → 该子文件夹，重名自动 `(1)`。
```

### Step 3: 全量自检

Run:
```
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop\tools\Run-Checks.ps1" -Group all
```
Expected: 32/32; no config-pin failure. If a check fails, stop and debug; do not commit a red matrix.

### Step 4: 执行记录

Append to the plan:

```markdown
## 执行记录（2026-10-21）

- 基线：main @ d437b36；worktree ...；build 0 warnings。
- Task 1 探针：企微 = WeWork Message + FileDrop + WXWork 缓存路径，无 descriptor；
  Explorer = FileDrop + FileGroupDescriptorW；用户实测根目录正常、浏览目录禁止光标。
- Task 2-3：提交哈希、红测输出摘要、绿测输出摘要、审查结论。
- Task 4：全量 32/32；冒烟 <通过 / 待用户>。
- 已知限制：不做虚拟文件 / Outlook 附件；复制同步执行；真实拖放仅冒烟。
```

### Step 5: 提交

```
git -C "..." add README.md CHANGELOG.md docs/superpowers/plans/2026-10-21-external-file-drop.md docs/superpowers/specs/2026-10-21-external-file-drop-design.md
git -C "..." commit -m "docs: document the external file drop"
```

### Step 6: 汇报

- 报告 32/32、每个提交哈希、冒烟清单。
- **合并 / 推送 / 部署前必须问用户**；未经同意不得执行。

---

## 自检（writing-plans）

**Spec coverage：**
- §3 诊断 → Task 1。
- §4 目标矩阵 → Task 3（浏览背景 / 文件夹行 / 文件行 / 锁定；根模式不动）。
- §5 纯策略 → Task 2。
- §6.2 代码 → Task 3。
- §6.3 XAML → Task 3。
- §6.4 三语 key → 本轮不新增，Task 4 全量 LangCheck 覆盖。
- §7 测试 → Task 2/3 + Task 4 全量。
- §8 风险 → 任务内边界与冒烟。

**Placeholder scan：** 无 TBD/TODO；所有代码步骤有实际代码。

**Type consistency：**
- `ExternalDropPolicy.Accepts(bool, bool, bool, bool)` 与 Task 3 的三处调用一致。
- `ExternalDropPolicy.TargetDirectory(bool, string, string)` 与 Task 3 的文件夹行调用一致。
- `IsExternalFileDrop(IDataObject)` / `CopyExternalFileDrop(DragEventArgs, string)` 在 Task 3 内定义并使用。
- `ContentPanel_DragOver` / `ContentPanel_DragLeave` / `ContentPanel_Drop` 与 XAML 名称一致。
- 根模式 `AddExternalPaths` 不在 Task 3 的改动范围内。

---

## 执行记录（2026-10-21）

- **基线**：`main` @ `d437b36`；worktree `C:\Users\31177347\.local\share\opencode\worktree\c3852b\kobold-external-file-drop`
  （分支 `external-file-drop`）；Release / Debug 构建均 0 警告 / 0 错误。
- **Task 1 探针**（不入库，已删除，diff 干净）：
  - 企微拖放（13:45:32）：formats = `WeWork Message|FileDrop|FileNameW|FileName|…`，
    **无 descriptor**；`FileDrop` 路径 = `C:\Users\31177347\Documents\WXWork\…\Cache\File\…xlsx`。
  - Explorer 拖放（13:46:03）：`FileDrop` + `FileGroupDescriptorW`(596 B) + `Shell IDList Array`。
  - 用户实测：根目录拖入正常（引用），**浏览目录显示禁止光标、不复制**。
  - 结论：descriptor-first / COM 虚拟文件方案被裁掉（YAGNI）；本轮只做浏览模式外部 `FileDrop`。
- **Task 2**：`ae36c98` `feat(drop): decide external file drops in the browser`
  - 红测：`ExternalDropPolicy` 不存在 → 9 × `CS0103`，生成失败。
  - 绿测：`DragOutCheck` 16/16（9 旧 + 7 新），`ALL DRAG-OUT CHECKS PASSED`。
  - 审查：clean；遗留 minor（`using System;` 未用、复合断言消息、报告 `CS0103` 计数笔误、
    `TargetDirectory` 的 null/empty 分支未单独 pin）。
- **Task 3**：`6a6f932` `feat(drop): copy external files into a browsed folder` +
  修复 `4513f41` `fix(drop): keep internal drags out of the external branch`
  - 红测：`XamlLoadCheck` 新 pin `folder: the content panel accepts external drops` FAIL
    （`ContentPanel does not accept drops`），仅此 1 条失败。
  - 绿测：build 0/0；`LangCheck` 178 key 不变；`DragOutCheck` 16/16；`XamlLoadCheck` 286 PASS。
  - 修复动因：根 Widget / 跨组件 / 浏览面板的内部拖动也带 `FileDrop`，裸 `FileDrop` 判定
    会把内部拖动当外部复制。新增 `InternalDragFormats` + `ExternalDropPolicy.IsExternalFileDrop`，
    内部拖动回到原有分支（浏览面板仍拒绝根 / 跨组件拖动）。
  - 修复后：`DragOutCheck` 21/21（9 旧 + 7 策略 + 5 内部拖动 pin）；`XamlLoadCheck` 286 PASS；
    build 0 警告 / 0 错误。
  - 审查：clean；遗留 minor（格式字面量重复、`IDataObject` 分类未单测、`autoConvert=true`、
    `ContentPanel` 无高亮、`DragLeave` 重复、`Accepts` 表达式重复；修复按裁定触及 Task 2 文件）。
- **Task 4**：`Run-Checks.ps1 -Group all` → **32/32**（30 check + config-pin + installer）；
  README 中英补充「拖入浏览目录 / 企业微信」；CHANGELOG `Unreleased → 修复` 加一条；
  spec 状态 → 已执行。
- **冒烟**：**待用户冒烟**（控制器已停止 Kobold；上一轮部署的是诊断探针构建，不是修复构建，
  因此真实 OLE 拖放 / 企微实拖 / shell 复制进度与重名 `(1)` 仍待用户在修复构建上确认）。
- **已知限制**：不做虚拟文件 / Outlook 附件（descriptor-only 载荷不消费）；复制同步执行，
  无自绘进度；真实拖放仅冒烟覆盖；新增内部拖动格式必须同步登记到 `InternalDragFormats`。
- **终审修复波（整支终审）**：终审结论 **"With fixes"**，全部 findings 已按裁定实现
  （Core 拥有格式名 + `FromFormats`、UI 严格格式判定、`ContentPanel` 高亮 / 拒绝反馈、
  格式名映射 pins、`ItemsContainer` 断言、README / spec 措辞）。修复提交
  `fix(drop): close the final review findings`（本次提交；SHA 见
  `.superpowers/sdd/2026-10-21-external-file-drop/final-fix-report.md`）。
  验证：`DragOutCheck` **25/25**、`XamlLoadCheck` 全 PASS、`LangCheck` 不变、
  build 0 警告 / 0 错误、`Run-Checks.ps1 -Group all` **32/32**。
  冒烟：仍 **待用户冒烟**（未部署修复构建）。

