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
                ["UI_DefaultFolderName"] = "My Folder",

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
                ["Menu_CopyPath"] = "📋 Copy Path"
            },

            ["zh"] = new Dictionary<string, string>
            {
                // UI
                ["UI_AppName"] = "Kobold",
                ["UI_Tagline"] = "桌面文件夹整理工具",
                ["UI_DropHere"] = "将文件拖放到此处",
                ["UI_PinTooltip"] = "固定面板（重启后保持展开）",
                ["UI_DefaultFolderName"] = "我的文件夹",

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
                ["Menu_CopyPath"] = "📋 复制路径"
            },

            ["ja"] = new Dictionary<string, string>
            {
                // UI
                ["UI_AppName"] = "Kobold",
                ["UI_Tagline"] = "デスクトップのフォルダー整理ツール",
                ["UI_DropHere"] = "ここにファイルをドロップ",
                ["UI_PinTooltip"] = "パネルを固定（再起動後も開いたままにする）",
                ["UI_DefaultFolderName"] = "マイ フォルダー",

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
