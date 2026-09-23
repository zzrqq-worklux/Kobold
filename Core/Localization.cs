using System.Collections.Generic;

namespace Kobold.Core
{
    /// <summary>
    /// UI string resources. Canonical key set is defined by the "en" dictionary;
    /// zh/ja must keep identical keys (checked by tests/LangCheck).
    /// Missing keys fall back to English, then to the raw key.
    /// </summary>
    public static class Localization
    {
        private static string _currentLanguage = "en";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string>
            {
                // UI
                ["UI_AppName"] = "Kobold",
                ["UI_Tagline"] = "Desktop Folder Widgets",
                ["UI_DropHere"] = "Drop files here",
                ["UI_PinTooltip"] = "Pin panel (keep open after restart)",
                ["UI_UnpinTooltip"] = "Unpin panel (close on focus loss)",
                ["UI_LockTooltip"] = "Lock widget (content cannot move in/out)",
                ["UI_UnlockTooltip"] = "Unlock widget",
                ["UI_DefaultFolderName"] = "My Folder",
                ["UI_BadgeHelpTitle"] = "Badge meanings",
                ["UI_BadgeHelpStored"] = "Colored cabinet: the file is stored in Kobold (no longer at its original place)",
                ["UI_BadgeHelpMissing"] = "Grey !: the source file is missing (moved or deleted outside Kobold)",

                // Context Menu - Folder
                ["Menu_Rename"] = "✏️ Rename",
                ["Menu_ChangeColor"] = "🎨 Change Color",
                ["Menu_LockWidget"] = "🔒 Lock Widget",
                ["Menu_UnlockWidget"] = "🔓 Unlock Widget",
                ["Menu_Delete"] = "❌ Delete Widget",

                // Context Menu - Item
                ["Menu_Open"] = "📂 Open",
                ["Menu_OpenLocation"] = "📁 Open File Location",
                ["Menu_RenameItem"] = "✏️ Rename",
                ["Menu_RemoveItem"] = "🗑️ Remove from Widget",
                ["Menu_StoreItem"] = "📦 Store in Kobold",
                ["Menu_UnstoreItem"] = "↩️ Move back to original place",

                // Tray Menu
                ["Tray_AddWidget"] = "➕ Add New Widget",
                ["Tray_ShowAll"] = "👁️ Show All Widgets",
                ["Tray_HideAll"] = "👁️‍🗨️ Hide All Widgets",
                ["Tray_Settings"] = "⚙️ Settings",
                ["Tray_Exit"] = "🚪 Exit",

                // Dialogs
                ["Dialog_Rename"] = "Rename Folder",
                ["Dialog_EnterName"] = "Enter new name:",
                ["Dialog_OK"] = "OK",
                ["Dialog_Cancel"] = "Cancel",
                ["Dialog_PickColor"] = "Pick Color",
                ["Dialog_DeleteWidget"] = "Are you sure you want to delete '{0}'?",
                ["Dialog_Confirm"] = "Confirm",
                ["Dialog_Save"] = "Save",
                ["Dialog_HideIconsFailed"] = "Couldn't change the desktop-icon setting on this system.",
                ["Dialog_StoreFailed"] = "Couldn't store this item. It may need administrator rights (for example shortcuts on the public desktop), or the file is in use.",
                ["Dialog_NewFile_Title"] = "New File",
                ["Dialog_NewFile_Prompt"] = "Enter file name:",
                ["Dialog_NewFolder_Title"] = "New Folder",
                ["Dialog_NewFolder_Prompt"] = "Enter folder name:",
                ["Dialog_RenameItem_Title"] = "Rename",
                ["Dialog_RenameItem_Prompt"] = "Enter new name:",

                // Panel Context Menu
                ["Menu_NewFile"] = "📄 New File",
                ["Menu_NewFolder"] = "📁 New Folder",

                // Settings
                ["Settings_Title"] = "Settings",
                ["Settings_About"] = "About",
                ["Settings_Language"] = "Language",
                ["Settings_StartWithWindows"] = "Start with Windows",
                ["Settings_HideDesktopSource"] = "Hide source on desktop when stored",
                ["Settings_PanelOpacity"] = "Panel Opacity",
                ["Settings_Island"] = "Island",
                ["Settings_IslandCollapseDelay"] = "Auto-collapse delay",
                ["Island_DesktopClickHide"] = "Click: hide desktop icons",
                ["Island_DesktopClickShow"] = "Click: show desktop icons",
                ["Island_DesktopOpenHint"] = "Double-click: open Desktop",
                ["Island_AddWidget"] = "Add widget",
                ["Settings_Theme"] = "Theme",
                ["Settings_Defaults"] = "Widget Defaults",
                ["Settings_Startup"] = "Startup",
                ["Settings_InterfaceLanguage"] = "Interface Language",
                ["Settings_ColorTheme"] = "Color Theme",
                ["Settings_DefaultGridColumns"] = "Default Grid Columns",
                ["Settings_Dark"] = "Dark",
                ["Settings_Light"] = "Light",
                ["Settings_IconStyle"] = "Icon Style",
                ["Settings_FolderIconStyle"] = "Folder Icon Style",
                ["Settings_Classic"] = "Classic",
                ["Settings_Modern"] = "Modern",
                ["Settings_Minimal"] = "Minimal",
                ["Settings_Rounded"] = "Rounded",
                ["Settings_Flat"] = "Flat",
                ["Settings_Gradient"] = "Gradient",

                // Grid
                ["Menu_GridSize"] = "📊 Grid Size",
                ["Menu_Columns"] = "Columns",
                ["Menu_ItemSize"] = "📐 Item Size",
                ["Size_Small"] = "Small",
                ["Size_Normal"] = "Normal",
                ["Size_Default"] = "Default",
                ["Size_Medium"] = "Medium",
                ["Size_Large"] = "Large",
                ["Size_ExtraLarge"] = "Extra Large",

                // Copy Path
                ["UI_BrowseBack"] = "Back to the widget",
                ["UI_FolderEmpty"] = "This folder is empty",
                ["UI_FolderAccessDenied"] = "This folder cannot be opened",
                ["UI_BrowseMoreItems"] = "{0} more items - click to open in Explorer",
                ["Menu_OpenTerminal"] = "💻 Open in Terminal",
                ["Menu_OpenInExplorer"] = "📁 Open in Explorer",
                ["Menu_ShowMoreOptions"] = "Show more options",
                ["Menu_Refresh"] = "🔄 Refresh",
                ["Menu_NewTextFile"] = "📄 New Text Document",
                ["Menu_DeleteItem"] = "🗑️ Delete",
                ["Dialog_NewTextFile_Title"] = "New Text Document",
                ["Dialog_RecycleItem"] = "Move '{0}' to the Recycle Bin?",
                ["Dialog_RecycleItems"] = "Move {0} items to the Recycle Bin?",
                ["Dialog_RecycleFailed"] = "{0} item(s) could not be moved to the Recycle Bin.",
                ["Dialog_RecycleNotLocal"] = "Only items on local fixed drives can be moved to the Recycle Bin.",
                ["Dialog_NameInvalid"] = "That name can't be used.",
                ["Dialog_NameExists"] = "An item with that name already exists.",
                ["Dialog_CreateFailed"] = "Couldn't create the item: {0}",
                ["Dialog_RenameFailed"] = "Couldn't rename the item: {0}",
                ["Dialog_TerminalNotFound"] = "No terminal was found (Windows Terminal, PowerShell or cmd).",
                ["UI_NewFolderDefault"] = "New Folder",
                ["UI_NewTextFileDefault"] = "New Text Document.txt",
                ["Menu_FolderColor"] = "Folder color",
                ["Menu_FolderColorDefault"] = "Default color",
                ["Dialog_FolderColorRefused"] = "This folder uses a system icon (Downloads, Documents, ...). Kobold leaves it alone because restoring it later would be guesswork.",
                ["Dialog_FolderColorFailed"] = "The folder color could not be changed.",
                ["Color_Red"] = "Red",
                ["Color_Orange"] = "Orange",
                ["Color_Yellow"] = "Yellow",
                ["Color_Lime"] = "Lime",
                ["Color_Green"] = "Green",
                ["Color_Cyan"] = "Cyan",
                ["Color_Blue"] = "Blue",
                ["Color_Purple"] = "Purple",
                ["Color_Gray"] = "Gray",
                ["Menu_CopyPath"] = "📋 Copy Path"
            },

            ["zh"] = new Dictionary<string, string>
            {
                // UI
                ["UI_AppName"] = "Kobold",
                ["UI_Tagline"] = "桌面文件夹整理工具",
                ["UI_DropHere"] = "将文件拖放到此处",
                ["UI_PinTooltip"] = "固定面板（重启后保持展开）",
                ["UI_UnpinTooltip"] = "取消固定（失去焦点时收起）",
                ["UI_LockTooltip"] = "锁定组件（禁止内容移入移出）",
                ["UI_UnlockTooltip"] = "解锁组件",
                ["UI_DefaultFolderName"] = "我的文件夹",
                ["UI_BadgeHelpTitle"] = "角标说明",
                ["UI_BadgeHelpStored"] = "彩色文件柜：文件已收纳到 Kobold 存储（不在原位置）",
                ["UI_BadgeHelpMissing"] = "灰色 !：源文件已丢失（在 Kobold 之外被移动或删除）",

                // Context Menu - Folder
                ["Menu_Rename"] = "✏️ 重命名",
                ["Menu_ChangeColor"] = "🎨 更改颜色",
                ["Menu_LockWidget"] = "🔒 锁定组件",
                ["Menu_UnlockWidget"] = "🔓 解锁组件",
                ["Menu_Delete"] = "❌ 删除组件",

                // Context Menu - Item
                ["Menu_Open"] = "📂 打开",
                ["Menu_OpenLocation"] = "📁 打开文件位置",
                ["Menu_RenameItem"] = "✏️ 重命名",
                ["Menu_RemoveItem"] = "🗑️ 从组件中移除",
                ["Menu_StoreItem"] = "📦 收纳到 Kobold",
                ["Menu_UnstoreItem"] = "↩️ 移回原位置",

                // Tray Menu
                ["Tray_AddWidget"] = "➕ 新建组件",
                ["Tray_ShowAll"] = "👁️ 显示全部组件",
                ["Tray_HideAll"] = "👁️‍🗨️ 隐藏全部组件",
                ["Tray_Settings"] = "⚙️ 设置",
                ["Tray_Exit"] = "🚪 退出",

                // Dialogs
                ["Dialog_Rename"] = "重命名文件夹",
                ["Dialog_EnterName"] = "输入新名称：",
                ["Dialog_OK"] = "确定",
                ["Dialog_Cancel"] = "取消",
                ["Dialog_PickColor"] = "选择颜色",
                ["Dialog_DeleteWidget"] = "确定要删除“{0}”吗？",
                ["Dialog_Confirm"] = "确认",
                ["Dialog_Save"] = "保存",
                ["Dialog_HideIconsFailed"] = "无法在这台电脑上更改桌面图标设置。",
                ["Dialog_StoreFailed"] = "无法收纳：可能没有权限移动该文件（例如公共桌面上的快捷方式需要管理员权限），或文件正被占用。",
                ["Dialog_NewFile_Title"] = "新建文件",
                ["Dialog_NewFile_Prompt"] = "输入文件名：",
                ["Dialog_NewFolder_Title"] = "新建文件夹",
                ["Dialog_NewFolder_Prompt"] = "输入文件夹名：",
                ["Dialog_RenameItem_Title"] = "重命名",
                ["Dialog_RenameItem_Prompt"] = "输入新名称：",

                // Panel Context Menu
                ["Menu_NewFile"] = "📄 新建文件",
                ["Menu_NewFolder"] = "📁 新建文件夹",

                // Settings
                ["Settings_Title"] = "设置",
                ["Settings_About"] = "关于",
                ["Settings_Language"] = "语言",
                ["Settings_StartWithWindows"] = "开机自动启动",
                ["Settings_HideDesktopSource"] = "收纳后隐藏桌面源文件",
                ["Settings_PanelOpacity"] = "面板不透明度",
                ["Settings_Island"] = "灵动岛",
                ["Settings_IslandCollapseDelay"] = "自动收起延迟",
                ["Island_DesktopClickHide"] = "单击：隐藏桌面图标",
                ["Island_DesktopClickShow"] = "单击：显示桌面图标",
                ["Island_DesktopOpenHint"] = "双击：打开桌面",
                ["Island_AddWidget"] = "添加组件",
                ["Settings_Theme"] = "主题",
                ["Settings_Defaults"] = "组件默认设置",
                ["Settings_Startup"] = "启动",
                ["Settings_InterfaceLanguage"] = "界面语言",
                ["Settings_ColorTheme"] = "颜色主题",
                ["Settings_DefaultGridColumns"] = "默认网格列数",
                ["Settings_Dark"] = "深色",
                ["Settings_Light"] = "浅色",
                ["Settings_IconStyle"] = "图标样式",
                ["Settings_FolderIconStyle"] = "文件夹图标样式",
                ["Settings_Classic"] = "经典",
                ["Settings_Modern"] = "现代",
                ["Settings_Minimal"] = "极简",
                ["Settings_Rounded"] = "圆角",
                ["Settings_Flat"] = "扁平",
                ["Settings_Gradient"] = "渐变",

                // Grid
                ["Menu_GridSize"] = "📊 网格大小",
                ["Menu_Columns"] = "列",
                ["Menu_ItemSize"] = "📐 图标大小",
                ["Size_Small"] = "小",
                ["Size_Normal"] = "普通",
                ["Size_Default"] = "默认",
                ["Size_Medium"] = "中",
                ["Size_Large"] = "大",
                ["Size_ExtraLarge"] = "特大",

                // Copy Path
                ["UI_BrowseBack"] = "返回组件",
                ["UI_FolderEmpty"] = "此文件夹为空",
                ["UI_FolderAccessDenied"] = "无法访问此文件夹",
                ["UI_BrowseMoreItems"] = "还有 {0} 项未显示 - 点击在资源管理器中打开",
                ["Menu_OpenTerminal"] = "💻 在终端中打开",
                ["Menu_OpenInExplorer"] = "📁 在资源管理器中打开",
                ["Menu_ShowMoreOptions"] = "显示更多选项",
                ["Menu_Refresh"] = "🔄 刷新",
                ["Menu_NewTextFile"] = "📄 新建文本文档",
                ["Menu_DeleteItem"] = "🗑️ 删除",
                ["Dialog_NewTextFile_Title"] = "新建文本文档",
                ["Dialog_RecycleItem"] = "将“{0}”移入回收站？",
                ["Dialog_RecycleItems"] = "将 {0} 个项目移入回收站？",
                ["Dialog_RecycleFailed"] = "有 {0} 项未能移入回收站。",
                ["Dialog_RecycleNotLocal"] = "只有本机固定磁盘上的项目才能移入回收站。",
                ["Dialog_NameInvalid"] = "该名称不可用。",
                ["Dialog_NameExists"] = "已存在同名的项目。",
                ["Dialog_CreateFailed"] = "无法创建：{0}",
                ["Dialog_RenameFailed"] = "无法重命名：{0}",
                ["Dialog_TerminalNotFound"] = "未找到可用的终端（Windows Terminal / PowerShell / cmd）。",
                ["UI_NewFolderDefault"] = "新建文件夹",
                ["UI_NewTextFileDefault"] = "新建文本文档.txt",
                ["Menu_FolderColor"] = "文件夹颜色",
                ["Menu_FolderColorDefault"] = "恢复默认颜色",
                ["Dialog_FolderColorRefused"] = "这个文件夹用的是系统自带图标（下载、文档等）。还原它们很麻烦，所以 Kobold 不动它。",
                ["Dialog_FolderColorFailed"] = "文件夹颜色修改失败。",
                ["Color_Red"] = "红色",
                ["Color_Orange"] = "橙色",
                ["Color_Yellow"] = "黄色",
                ["Color_Lime"] = "青柠色",
                ["Color_Green"] = "绿色",
                ["Color_Cyan"] = "青色",
                ["Color_Blue"] = "蓝色",
                ["Color_Purple"] = "紫色",
                ["Color_Gray"] = "灰色",
                ["Menu_CopyPath"] = "📋 复制路径"
            },

            ["ja"] = new Dictionary<string, string>
            {
                // UI
                ["UI_AppName"] = "Kobold",
                ["UI_Tagline"] = "デスクトップのフォルダー整理ツール",
                ["UI_DropHere"] = "ここにファイルをドロップ",
                ["UI_PinTooltip"] = "パネルを固定（再起動後も開いたままにする）",
                ["UI_UnpinTooltip"] = "パネルを固定解除（フォーカスを失うと閉じる）",
                ["UI_LockTooltip"] = "ウィジェットをロック（内容の移動を禁止）",
                ["UI_UnlockTooltip"] = "ロックを解除",
                ["UI_DefaultFolderName"] = "マイ フォルダー",
                ["UI_BadgeHelpTitle"] = "バッジの説明",
                ["UI_BadgeHelpStored"] = "カラフルなキャビネット: ファイルは Kobold ストレージに収納済み（元の場所にはありません）",
                ["UI_BadgeHelpMissing"] = "グレーの !: 元ファイルが見つかりません（Kobold 以外で移動または削除）",

                // Context Menu - Folder
                ["Menu_Rename"] = "✏️ 名前の変更",
                ["Menu_ChangeColor"] = "🎨 色の変更",
                ["Menu_LockWidget"] = "🔒 ウィジェットをロック",
                ["Menu_UnlockWidget"] = "🔓 ロックを解除",
                ["Menu_Delete"] = "❌ ウィジェットを削除",

                // Context Menu - Item
                ["Menu_Open"] = "📂 開く",
                ["Menu_OpenLocation"] = "📁 ファイルの場所を開く",
                ["Menu_RenameItem"] = "✏️ 名前の変更",
                ["Menu_RemoveItem"] = "🗑️ ウィジェットから外す",
                ["Menu_StoreItem"] = "📦 Kobold に収納",
                ["Menu_UnstoreItem"] = "↩️ 元の場所に戻す",

                // Tray Menu
                ["Tray_AddWidget"] = "➕ ウィジェットを追加",
                ["Tray_ShowAll"] = "👁️ すべて表示",
                ["Tray_HideAll"] = "👁️‍🗨️ すべて非表示",
                ["Tray_Settings"] = "⚙️ 設定",
                ["Tray_Exit"] = "🚪 終了",

                // Dialogs
                ["Dialog_Rename"] = "フォルダー名の変更",
                ["Dialog_EnterName"] = "新しい名前:",
                ["Dialog_OK"] = "OK",
                ["Dialog_Cancel"] = "キャンセル",
                ["Dialog_PickColor"] = "色の選択",
                ["Dialog_DeleteWidget"] = "「{0}」を削除してもよろしいですか?",
                ["Dialog_Confirm"] = "確認",
                ["Dialog_Save"] = "保存",
                ["Dialog_HideIconsFailed"] = "このシステムではデスクトップ アイコンの設定を変更できませんでした。",
                ["Dialog_StoreFailed"] = "収納できませんでした。管理者権限が必要か（例: 共有デスクトップのショートカット）、またはファイルが使用中です。",
                ["Dialog_NewFile_Title"] = "新規ファイル",
                ["Dialog_NewFile_Prompt"] = "ファイル名:",
                ["Dialog_NewFolder_Title"] = "新規フォルダー",
                ["Dialog_NewFolder_Prompt"] = "フォルダー名:",
                ["Dialog_RenameItem_Title"] = "名前の変更",
                ["Dialog_RenameItem_Prompt"] = "新しい名前:",

                // Panel Context Menu
                ["Menu_NewFile"] = "📄 新規ファイル",
                ["Menu_NewFolder"] = "📁 新規フォルダー",

                // Settings
                ["Settings_Title"] = "設定",
                ["Settings_About"] = "バージョン情報",
                ["Settings_Language"] = "言語",
                ["Settings_StartWithWindows"] = "サインイン時に自動的に起動",
                ["Settings_HideDesktopSource"] = "収納後、デスクトップの元ファイルを隠す",
                ["Settings_PanelOpacity"] = "パネルの不透明度",
                ["Settings_Island"] = "アイランド",
                ["Settings_IslandCollapseDelay"] = "自動折りたたみの遅延",
                ["Island_DesktopClickHide"] = "クリック: デスクトップ アイコンを非表示",
                ["Island_DesktopClickShow"] = "クリック: デスクトップ アイコンを表示",
                ["Island_DesktopOpenHint"] = "ダブルクリック: デスクトップを開く",
                ["Island_AddWidget"] = "ウィジェットを追加",
                ["Settings_Theme"] = "テーマ",
                ["Settings_Defaults"] = "ウィジェットの既定設定",
                ["Settings_Startup"] = "起動",
                ["Settings_InterfaceLanguage"] = "表示言語",
                ["Settings_ColorTheme"] = "カラーテーマ",
                ["Settings_DefaultGridColumns"] = "グリッドの既定列数",
                ["Settings_Dark"] = "ダーク",
                ["Settings_Light"] = "ライト",
                ["Settings_IconStyle"] = "アイコン スタイル",
                ["Settings_FolderIconStyle"] = "フォルダー アイコン スタイル",
                ["Settings_Classic"] = "クラシック",
                ["Settings_Modern"] = "モダン",
                ["Settings_Minimal"] = "ミニマル",
                ["Settings_Rounded"] = "ラウンド",
                ["Settings_Flat"] = "フラット",
                ["Settings_Gradient"] = "グラデーション",

                // Grid
                ["Menu_GridSize"] = "📊 グリッド サイズ",
                ["Menu_Columns"] = "列",
                ["Menu_ItemSize"] = "📐 アイテム サイズ",
                ["Size_Small"] = "小",
                ["Size_Normal"] = "標準",
                ["Size_Default"] = "既定",
                ["Size_Medium"] = "中",
                ["Size_Large"] = "大",
                ["Size_ExtraLarge"] = "特大",

                // Copy Path
                ["UI_BrowseBack"] = "ウィジェットに戻る",
                ["UI_FolderEmpty"] = "このフォルダーは空です",
                ["UI_FolderAccessDenied"] = "このフォルダーを開けません",
                ["UI_BrowseMoreItems"] = "他に {0} 件あります - クリックでエクスプローラーで開く",
                ["Menu_OpenTerminal"] = "💻 ターミナルで開く",
                ["Menu_OpenInExplorer"] = "📁 エクスプローラーで開く",
                ["Menu_ShowMoreOptions"] = "その他のオプションを表示",
                ["Menu_Refresh"] = "🔄 更新",
                ["Menu_NewTextFile"] = "📄 新しいテキスト ドキュメント",
                ["Menu_DeleteItem"] = "🗑️ 削除",
                ["Dialog_NewTextFile_Title"] = "新しいテキスト ドキュメント",
                ["Dialog_RecycleItem"] = "「{0}」をゴミ箱に移動しますか？",
                ["Dialog_RecycleItems"] = "{0} 個の項目をゴミ箱に移動しますか？",
                ["Dialog_RecycleFailed"] = "{0} 個の項目をゴミ箱に移動できませんでした。",
                ["Dialog_RecycleNotLocal"] = "ローカル固定ドライブ上の項目のみゴミ箱に移動できます。",
                ["Dialog_NameInvalid"] = "その名前は使用できません。",
                ["Dialog_NameExists"] = "同じ名前の項目が既に存在します。",
                ["Dialog_CreateFailed"] = "作成できませんでした: {0}",
                ["Dialog_RenameFailed"] = "名前を変更できませんでした: {0}",
                ["Dialog_TerminalNotFound"] = "使用できるターミナルが見つかりません（Windows Terminal / PowerShell / cmd）。",
                ["UI_NewFolderDefault"] = "新しいフォルダー",
                ["UI_NewTextFileDefault"] = "新しいテキスト ドキュメント.txt",
                ["Menu_FolderColor"] = "フォルダーの色",
                ["Menu_FolderColorDefault"] = "既定の色に戻す",
                ["Dialog_FolderColorRefused"] = "このフォルダーはシステム標準のアイコン（ダウンロード、ドキュメントなど）を使っています。復元が困難なため、Kobold は変更しません。",
                ["Dialog_FolderColorFailed"] = "フォルダーの色を変更できませんでした。",
                ["Color_Red"] = "赤",
                ["Color_Orange"] = "オレンジ",
                ["Color_Yellow"] = "黄",
                ["Color_Lime"] = "ライム",
                ["Color_Green"] = "緑",
                ["Color_Cyan"] = "シアン",
                ["Color_Blue"] = "青",
                ["Color_Purple"] = "紫",
                ["Color_Gray"] = "グレー",
                ["Menu_CopyPath"] = "📋 パスのコピー"
            }
        };

        public static string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (Strings.ContainsKey(value))
                {
                    _currentLanguage = value;
                }
            }
        }

        public static void SetLanguage(string lang)
        {
            CurrentLanguage = lang;
        }

        public static string Get(string key)
        {
            if (Strings.TryGetValue(_currentLanguage, out var langStrings) &&
                langStrings.TryGetValue(key, out var value))
            {
                return value;
            }
            
            // Fallback to English
            if (Strings.TryGetValue("en", out var enStrings) &&
                enStrings.TryGetValue(key, out var enValue))
            {
                return enValue;
            }
            
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            string template = Get(key);
            return string.Format(template, args);
        }
    }
}
