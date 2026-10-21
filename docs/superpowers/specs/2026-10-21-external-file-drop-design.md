# Kobold 浏览目录外部文件拖入设计（Explorer · 企业微信 FileDrop）

> **状态**：**已执行（2026-10-21）**。
> **基线**：`main` @ `d437b36`（笔记导航与交互打磨已合并、已推送、已部署）。
> **关系**：独立增量 spec；不修改 `2026-10-19-note-navigation-and-polish-design.md` 的任何条款。
> **定性**：一次**小范围交互修复**。探针证明企微走的是 `FileDrop` + 缓存路径，不是 OLE
> 虚拟文件；根 Widget 的引用落点已经正常，真正的缺口是**浏览模式拒绝外部 `FileDrop`**。
> 本轮只修这个缺口，不引入 COM / 虚拟文件 / 新依赖。
> **代码走查（2026-10-21）**：`Controls/FolderWidget.DragDrop.cs`、`Controls/FolderWidget.xaml`、
> `Services/ShellFileOperations.cs`、`Core/StorageOps.cs`、`tests/DragOutCheck`、
> `tests/XamlLoadCheck`、提交 `6bed985`。

## 0. 决策记录（2026-10-21 用户确认 + 探针证据）

1. **探针证据**：
   - 企微拖入的 `formats` = `WeWork Message|FileDrop|FileNameW|FileName|Preferred
     DropEffect|Logical Performed DropEffect|text/x-moz-url|text/uri-list|Text|
     UnicodeText|System.String`，**没有** `FileGroupDescriptorW`；`FileDrop` 路径是
     `C:\Users\31177347\Documents\WXWork\...\Cache\File\...xlsx`。
   - Explorer 拖入同时带 `FileDrop` 与 `FileGroupDescriptorW`（596 bytes）。
   - 结论：不能 descriptor 优先；本轮不需要虚拟文件读取。
2. **用户实测**：企微拖到 Widget 根目录 → 条目出现、能打开；拖到从 Widget 打开的文件夹
   → **禁止光标**，没有复制。
3. **根 Widget 行为不变**：真实 `FileDrop` 仍是引用（`IsReference=true`），源文件不动；
   企微缓存路径也按引用（用户确认根目录可用）。
4. **浏览模式**：外部 `FileDrop` 一律**复制**。拖到面板空白 / 空态 → `CurrentBrowsePath`；
   拖到文件夹行 → 该子文件夹；拖到文件行 → 拒绝；锁定 → 拒绝。
5. **不做**：`FileGroupDescriptorW` / `FileContents` / COM 虚拟文件（Outlook 附件后续单独
   spec）；不做进度条、移动、覆盖提示定制、内容去重。
6. **零新依赖 / 32/32**：不新增 check 工程；纯策略 pin 放进 `DragOutCheck`，XAML 落点 pin
   放进 `XamlLoadCheck`。

## 1. 目标与成功标准

| # | 目标 | 验收口径 |
|---|---|---|
| 1 | 企微 / Explorer 能拖进浏览目录 | 拖到浏览背景 → 文件出现在当前目录；拖到文件夹行 → 出现在该子文件夹；列表自动刷新 |
| 2 | 光标语义明确 | 可接收 = `Copy`；文件行 / 锁定 / 非 FileDrop = `None`；不再“禁止光标、松手没反应” |
| 3 | 根 Widget 零变化 | 根目录仍按引用加入；源文件不动；企微缓存路径条目仍能打开 |
| 4 | 空态 / 边距可接 | 空文件夹 / 面板空白也能接住外部文件 |
| 5 | 零回归 | 内部重排、跨 widget 合并、browse 拖拽、拖出到 Explorer、锁定拒绝全部保持；32/32 |
| 6 | 重名安全 | 复制走 shell 的自动重命名（`(1)`），不覆盖已有文件 |

## 2. 非目标（本轮不做）

- 不做 `FileGroupDescriptorW` / `FileContents` 虚拟文件（Outlook 附件另立 spec）。
- 不做拖入进度条 / 取消 / 后台传输；复制是同步 shell 调用。
- 不做“移动”语义；浏览模式一律复制，根模式一律引用。
- 不做拖出到企微的专门适配；拖出仍走现有 `DragOutPolicy` / shell。
- 不做内容哈希去重；同一文件重复拖入会得到 `(1)` 副本。
- 不改 `WidgetItem` / `FolderData` 的数据格式；不新增配置字段。
- 不新增 check 工程；不改变 32 行的自检矩阵。

## 3. 诊断：禁止光标从哪来

### 3.1 代码路径

- `ItemsContainer_DragOver` 的浏览分支只接受 `IsBrowseDrag`：
  ```csharp
  bool accepted = IsBrowseDrag(e.Data, out var browseSource) &&
                  !BrowseMove.SameFolder(browseSource, CurrentBrowsePath);
  e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
  ```
  外部 `FileDrop` 不是 browse 拖拽 → `accepted=false` → `None`（禁止光标）。
- `ItemsContainer_Drop` 的浏览分支调用 `HandleBrowseDrop`：
  ```csharp
  if (!IsBrowseDrag(e.Data, out var sourceFolder) || ...) {
      e.Effects = DragDropEffects.None;
      return;
  }
  ```
  即使 DragOver 接受，外部 `FileDrop` 也会在这里被拒。
- `BrowseItem_DragOver` / `BrowseItem_Drop` 同样只认 `IsBrowseDrag`，所以文件夹行也拒绝。
- `ContentPanel` 没有 `AllowDrop`，空态 / 边距根本接不住。
- 根模式 `ItemsContainer_Drop` 有 `DataFormats.FileDrop` 分支，调用 `AddExternalPaths`，
  所以根目录能加入（与用户实测一致）。

### 3.2 探针（已完成，临时，不入库）

- 只读日志记录 `GetFormats()`、`FileDrop` 路径、`FileGroupDescriptorW` 是否存在、
  Effects。用户用 Explorer / 企微各拖一次；证据见 §0。
- 探针在 Task 1 部署后删除；最终提交不含探针。

## 4. 目标矩阵（用户已确认）

| 拖入位置 | 外部 `FileDrop` |
|---|---|
| 根 Widget 条目区 | **不变**：引用（`IsReference=true`），源文件不动；按配置隐藏桌面图标 |
| 根 Widget 空态 / 边距 | **不变**：现有根模式落点（若空态未覆盖，保持现状） |
| 浏览模式背景 | **新增**：复制到 `CurrentBrowsePath` |
| 浏览模式文件夹行 | **新增**：复制到该子文件夹 |
| 浏览模式文件行 | 拒绝（`None`） |
| 锁定 Widget | 拒绝（`None`） |

补充：

- 只接受 `DataFormats.FileDrop`；不接受文字 / 图片 / 内部 `KoboldItem` / browse 拖拽的
  特殊格式。
- 复制走 `ShellFileOperations.Copy(paths, targetDir, PanelHwnd)`：`FOF_ALLOWUNDO |
  FOF_RENAMEONCOLLISION`，重名自动 `(1)`，不覆盖。
- 复制完成后 `Dispatcher.BeginInvoke(UpdateUI)`，列表自动刷新；失败只把 `e.Effects`
  置 `None`，不弹窗打断（与现有 browse 拖放一致）。
- 根模式不改 `AddExternalPaths`；不改根模式的 DragOver 默认效果。

## 5. 纯策略 `Core/ExternalDropPolicy.cs`

把“这个落点接不接受外部 FileDrop、复制到哪”从 WPF 事件里抽出来，便于逐条 pin：

```csharp
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

- `browsing=false` 返回 false：根模式不走这个策略，保持现有 `AddExternalPaths`。
- `overFileRow=true` 返回 false：只有文件夹行与空白接受。
- `TargetDirectory` 对空文件夹行路径回退到当前目录，避免空目标。

## 6. 接口与文件

### 6.1 新增 `Core/ExternalDropPolicy.cs`

- 纯 BCL，无 WPF；`Accepts` / `TargetDirectory` 见 §5。
- 逐条 pin 放 `tests/DragOutCheck`（不新增 check 工程）。

### 6.2 修改 `Controls/FolderWidget.DragDrop.cs`

新增：

```csharp
private bool IsExternalFileDrop(IDataObject data)
{
    return data != null && data.GetDataPresent(DataFormats.FileDrop);
}

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
    Dispatcher.BeginInvoke(new Action(UpdateUI));
}
```

修改：

- `ItemsContainer_DragOver` 浏览分支：在现有 `IsBrowseDrag` 判断之前，若
  `IsExternalFileDrop(e.Data)`：
  ```csharp
  bool accepted = ExternalDropPolicy.Accepts(true, _data.IsLocked, true, false);
  e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
  SetBrowseDropHighlight(accepted);
  DropIndicator.Visibility = Visibility.Collapsed;
  if (!accepted) DragDropSession.Current?.MarkTargetRefused();
  e.Handled = true;
  return;
  ```
- `ItemsContainer_Drop` 的 `HandleBrowseDrop` 开头：若 `IsExternalFileDrop(e.Data)`，
  `CopyExternalFileDrop(e, CurrentBrowsePath); return;`。
- `BrowseItem_DragOver`：在现有 `IsBrowseDrag` 判断之前，若
  `IsExternalFileDrop(e.Data)`：
  ```csharp
  bool accepted = ExternalDropPolicy.Accepts(true, _data.IsLocked, true, !(target != null && target.IsDirectory));
  e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
  if (accepted) SetItemDropHighlight(border);
  else { ClearItemDropHighlight(); DragDropSession.Current?.MarkTargetRefused(); }
  return;
  ```
- `BrowseItem_Drop`：若 `IsExternalFileDrop(e.Data)`：
  ```csharp
  bool accepted = ExternalDropPolicy.Accepts(true, _data.IsLocked, true, !(target != null && target.IsDirectory));
  if (!accepted) { e.Effects = DragDropEffects.None; return; }
  CopyExternalFileDrop(e, ExternalDropPolicy.TargetDirectory(true, target.Path, CurrentBrowsePath));
  return;
  ```
- `ContentPanel` 新增处理器（空态 / 边距）：
  ```csharp
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
- 根模式分支（`ItemsContainer_Drop` 末尾的 `DataFormats.FileDrop` → `AddExternalPaths`）
  **保持不动**；`ExternalDropPolicy` 只在 `IsBrowsing` 为真时使用。

### 6.3 修改 `Controls/FolderWidget.xaml`

- `ContentPanel` 加 `AllowDrop="True"`、`Background="Transparent"`、
  `DragOver="ContentPanel_DragOver"`、`DragLeave="ContentPanel_DragLeave"`、
  `Drop="ContentPanel_Drop"`。
- `ItemsScroller` 加 `AllowDrop="True"`（不挂处理器，事件冒泡到 `ContentPanel`）。
- `EmptyState` 加 `IsHitTestVisible="False"`（纯提示，不拦截拖放）。
- `ItemsContainer` / `BrowseItem` 的既有 `AllowDrop` 与处理器保留。

### 6.4 三语 key

- **不新增**。复制失败不弹窗；`e.Effects=None` 即可。现有 `Dialog_*` 不动。

## 7. 测试与验收

### 7.1 自检矩阵（维持 32 行）

**`tests/DragOutCheck`（纯逻辑，CI）新增：**

1. `external drop: the browse background accepts a file drop`：
   `Accepts(true, false, true, false) == true`。
2. `external drop: a folder row accepts a file drop`：
   `Accepts(true, false, true, false) == true`；`TargetDirectory(true, @"C:\a\sub", @"C:\a") == @"C:\a\sub"`。
3. `external drop: a file row refuses`：
   `Accepts(true, false, true, true) == false`。
4. `external drop: a locked widget refuses`：
   `Accepts(true, true, true, false) == false`。
5. `external drop: a non-file payload refuses`：
   `Accepts(true, false, false, false) == false`。
6. `external drop: root mode never uses the browse policy`：
   `Accepts(false, false, true, false) == false`。
7. `external drop: a blank folder row falls back to the current folder`：
   `TargetDirectory(true, null, @"C:\a") == @"C:\a"`；
   `TargetDirectory(false, @"C:\a\sub", @"C:\a") == @"C:\a"`。

**`tests/XamlLoadCheck`（WPF，离屏）新增：**

8. `folder: the content panel accepts external drops`：
   `ContentPanel.AllowDrop == true`；`ItemsScroller.AllowDrop == true`；
   `EmptyState.IsHitTestVisible == false`；`ItemsContainer.AllowDrop == true`。
   （XAML 事件处理器不直接 pin，真实拖放交冒烟。）

**既有 pins 零回归**：`DragOutCheck` 的拖出策略、`StorageOpsCheck` 的存储/恢复、
`WidgetItemsCheck` 的合并、`XamlLoadCheck` 的 browse 拖拽 / 文件夹行 / 后退按钮 pins。

### 7.2 冒烟清单（用户手动）

1. Explorer → 浏览背景：文件出现在当前目录；重名自动 `(1)`，不覆盖。
2. 企微 → 浏览背景：文件出现在当前目录；字节一致。
3. 企微 → 浏览文件夹行：文件出现在该子文件夹，而不是当前目录。
4. Explorer → 浏览文件夹行：同上。
5. 锁定 Widget：拖入显示禁止光标，松手无文件。
6. 浏览文件行：禁止光标，不复制。
7. 根 Widget：Explorer / 企微拖入仍是引用，源文件不动；企微缓存条目仍能打开。
8. 回归：内部重排、跨 widget 拖拽、browse 拖拽、拖出到 Explorer、后退按钮拖放。
9. 空文件夹 / 面板空白也能接住拖放。
10. 三主题 / 高对比度 / 三语下正常。
11. 全量 `Run-Checks.ps1 -Group all` 32/32。

## 8. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 浏览模式把外部 FileDrop 当成 browse 拖拽 | 先判 `IsExternalFileDrop`，再走 `IsBrowseDrag`；外部分支统一 `Handled=true` |
| 文件夹行与背景落点混淆 | `ExternalDropPolicy.TargetDirectory` 统一决定；文件夹行 → 子文件夹，空白 → 当前目录 |
| 锁定 / 文件行仍显示 Copy | `Accepts` 统一裁决；`e.Effects=None` + `MarkTargetRefused` |
| 空态 / 边距接不住 | `ContentPanel` + `ItemsScroller` 的 `AllowDrop`，`EmptyState` 不拦截 |
| shell 复制弹冲突 / 取消 | `ShellFileOperations.Copy` 已有 `FOF_RENAMEONCOLLISION`；取消返回 false，`e.Effects=None` |
| 根模式被误改 | `ExternalDropPolicy` 只在 `IsBrowsing` 为真时调用；根模式 `AddExternalPaths` 不动 |
| 新增 key 漏语言 | 本轮不新增 key，`LangCheck` 零变化 |
| 32/32 被新 check 打破 | 不新增工程；pin 放进 `DragOutCheck` / `XamlLoadCheck` |

## 9. 交付物

- `Core/ExternalDropPolicy.cs`（新：browse 落点裁决 + 目标目录）。
- `Controls/FolderWidget.DragDrop.cs`（外部 FileDrop 的 DragOver / Drop、空态接住、复制）。
- `Controls/FolderWidget.xaml`（`ContentPanel` / `ItemsScroller` 落点、`EmptyState` 不拦截）。
- `tests/DragOutCheck/Program.cs`、`tests/XamlLoadCheck/Program.cs`（按 §7 增补）。
- `docs/superpowers/plans/2026-10-21-external-file-drop.md`（writing-plans 产出）。
- `README.md`（英/中）、`CHANGELOG.md`（Unreleased）在整支终审时统一补。
- 临时探针只存在于 Task 1，最终删除 / 不入库。

## 10. 与既有 spec 的关系

- 本 spec 不修改笔记相关 spec；不改变 `NoteData` / 笔记编辑行为。
- 本 spec 不实现 `FileGroupDescriptorW` / `FileContents`；如果后续要支持 Outlook 附件，
  另立 spec，复用本轮的 `ExternalDropPolicy` 落点裁决。
- 与既有 `DragOutPolicy` 的关系：只处理“拖入”，拖出策略零改动。
- 与 `StorageOps` 的关系：只复用唯一名 / 存储语义；不改变存储/恢复行为。
