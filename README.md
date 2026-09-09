# Kobold — Windows Desktop Organizer

<div align="center">

![Kobold Logo](Resources/kobold-logo.png)

**桌面整理小组件** | **Windows Desktop Folder Widgets**

Windows 10/11 · WPF · .NET Framework 4.8

</div>

Kobold is a desktop organizer built from [FoldRa](https://github.com/YusufEren97/FoldRa)
as a secondary-development (二开) base. It places beautiful glassmorphism
widgets on your desktop; drop files onto a widget and they are tidied away,
then restored safely when you remove the widget.

## Features

- **Folder widgets on the desktop** — glassmorphism panels with fluid 60fps animations, auto-resizing to content
- **File magnet** — drag files onto a widget; same-drive files are moved physically (instant), cross-drive files are kept by reference
- **Safe by design** — deleting a widget moves its files back to your desktop; nothing is ever lost from the widget storage
- **Theming** — dark / light modes and per-widget custom colors
- **System tray** — quick settings and exit from the tray icon
- **Single instance** — mutex-protected launch, optional auto-start with Windows

## Build

Requires Windows with the .NET SDK (project targets `net48`, WPF).

```powershell
dotnet build Kobold.csproj -c Release
```

Output: `bin\Release\net48\Kobold.exe`

## Data

- Config: `%AppData%\Kobold\config.json` (auto-backup as `config.json.backup`)
- Widget storage (moved files): `%AppData%\Kobold\Storage`

## Development notes

- App icons (exe/tray/settings window) use the Kobold logo generated from
  `logo.jpg` (white background auto-removed; multi-size `icon.ico` 256–16 px).
- Upstream README/screenshots were dropped on purpose; current UI screenshots
  can be added here once the first 二开 changes land.

## Credits & License

Derived from [FoldRa](https://github.com/YusufEren97/FoldRa) by
[Yusuf Eren Seyrek](https://github.com/YusufEren97) and
[Mehmet Delin](https://github.com/Deleny), licensed under the MIT License.
This project keeps the MIT license; see the original repository for upstream history.

---

# 中文说明

Kobold 是基于 [FoldRa](https://github.com/YusufEren97/FoldRa) 的二次开发项目：
在桌面上放置毛玻璃风格的文件夹小组件，把文件拖进组件即可收纳整理；
删除组件时，收纳的文件会自动移回桌面，不会丢失。

## 功能

- **桌面文件夹小组件** — 毛玻璃半透明面板，60fps 流畅动画，随内容自动调整大小
- **文件磁贴收纳** — 拖拽文件到组件即可收纳；同盘物理移动（瞬时），跨盘以引用方式保留
- **安全回收** — 删除组件时文件自动归位到桌面，收纳区永不丢文件
- **主题** — 深色/浅色模式 + 每个组件可自定义颜色
- **托盘图标** — 快捷设置、退出
- **单实例运行** — 互斥锁防重复启动，可选开机自启

## 构建

需要 Windows + .NET SDK（目标框架 `net48`，WPF）：

```powershell
dotnet build Kobold.csproj -c Release
```

产物：`bin\Release\net48\Kobold.exe`

## 数据目录

- 配置：`%AppData%\Kobold\config.json`（自动备份为 `config.json.backup`）
- 收纳文件存储：`%AppData%\Kobold\Storage`

## 开发说明

- 程序图标（exe/托盘/设置窗口）已替换为 Kobold 新 logo（由 `logo.jpg` 自动去白底生成，`icon.ico` 含 256–16px 多尺寸）
- 上游 README 与截图已移除，待首批二开改动落地后再补充新截图

## 许可

上游 [FoldRa](https://github.com/YusufEren97/FoldRa)（作者 Yusuf Eren Seyrek、
Mehmet Delin）为 MIT 协议，本项目沿用 MIT 协议。
