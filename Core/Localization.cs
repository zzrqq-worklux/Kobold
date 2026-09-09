using System.Collections.Generic;

namespace FoldRa.Core
{
    public static class Localization
    {
        private static string _currentLanguage = "en";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string>
            {
                // UI
                ["UI_AppName"] = "FoldRa",
                ["UI_DropHere"] = "Drop files here",
                ["UI_Empty"] = "Empty folder",
                ["UI_Items"] = "{0} items",
                
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
                ["Settings_Language"] = "Language",
                ["Settings_StartWithWindows"] = "Start with Windows",
                ["Settings_Theme"] = "Theme",
                ["Settings_Defaults"] = "Widget Defaults",
                ["Settings_Startup"] = "Startup",
                ["Settings_InterfaceLanguage"] = "Interface Language",
                ["Settings_ColorTheme"] = "Color Theme",
                ["Settings_DefaultGridColumns"] = "Default Grid Columns",
                ["Settings_ConfiguredInSetup"] = "Configured in Setup",
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
                ["Menu_CopyPath"] = "📋 Copy Path",
                
                // Languages
                ["Lang_English"] = "English",
                ["Lang_Turkish"] = "Türkçe",
            },
            
            ["tr"] = new Dictionary<string, string>
            {
                // UI
                ["UI_AppName"] = "FoldRa",
                ["UI_DropHere"] = "Dosyaları buraya bırak",
                ["UI_Empty"] = "Boş klasör",
                ["UI_Items"] = "{0} öğe",
                
                // Context Menu - Folder
                ["Menu_Rename"] = "✏️ Yeniden Adlandır",
                ["Menu_ChangeColor"] = "🎨 Renk Değiştir",
                ["Menu_LockWidget"] = "🔒 Widget'ı Kilitle",
                ["Menu_UnlockWidget"] = "🔓 Kilidi Aç",
                ["Menu_Delete"] = "❌ Widget'ı Sil",
                
                // Context Menu - Item
                ["Menu_Open"] = "📂 Aç",
                ["Menu_OpenLocation"] = "📁 Dosya Konumunu Aç",
                ["Menu_RenameItem"] = "✏️ Yeniden Adlandır",
                ["Menu_RemoveItem"] = "🗑️ Widget'tan Kaldır",
                
                // Tray Menu
                ["Tray_AddWidget"] = "➕ Yeni Widget Ekle",
                ["Tray_ShowAll"] = "👁️ Tüm Widget'ları Göster",
                ["Tray_HideAll"] = "👁️‍🗨️ Tüm Widget'ları Gizle",
                ["Tray_Settings"] = "⚙️ Ayarlar",
                ["Tray_Exit"] = "🚪 Çıkış",
                
                // Dialogs
                ["Dialog_Rename"] = "Klasörü Yeniden Adlandır",
                ["Dialog_EnterName"] = "Yeni ismi girin:",
                ["Dialog_OK"] = "Tamam",
                ["Dialog_Cancel"] = "İptal",
                ["Dialog_PickColor"] = "Renk Seç",
                ["Dialog_DeleteWidget"] = "'{0}' widget'ını silmek istediğinizden emin misiniz?",
                ["Dialog_Confirm"] = "Onayla",
                ["Dialog_Save"] = "Kaydet",
                ["Dialog_NewFile_Title"] = "Yeni Dosya",
                ["Dialog_NewFile_Prompt"] = "Dosya adını girin:",
                ["Dialog_NewFolder_Title"] = "Yeni Klasör",
                ["Dialog_NewFolder_Prompt"] = "Klasör adını girin:",
                ["Dialog_RenameItem_Title"] = "Yeniden Adlandır",
                ["Dialog_RenameItem_Prompt"] = "Yeni adı girin:",
                
                // Panel Context Menu
                ["Menu_NewFile"] = "📄 Yeni Dosya",
                ["Menu_NewFolder"] = "📁 Yeni Klasör",
                
                // Settings
                ["Settings_Language"] = "Dil",
                ["Settings_StartWithWindows"] = "Windows ile başlat",
                ["Settings_Theme"] = "Tema",
                ["Settings_Defaults"] = "Widget Varsayılanları",
                ["Settings_Startup"] = "Başlangıç",
                ["Settings_InterfaceLanguage"] = "Arayüz Dili",
                ["Settings_ColorTheme"] = "Renk Teması",
                ["Settings_DefaultGridColumns"] = "Varsayılan Sütun Sayısı",
                ["Settings_ConfiguredInSetup"] = "Kurulumda Ayarlanır",
                ["Settings_Dark"] = "Koyu",
                ["Settings_Light"] = "Açık",
                ["Settings_IconStyle"] = "İkon Stili",
                ["Settings_FolderIconStyle"] = "Klasör İkon Stili",
                ["Settings_Classic"] = "Klasik",
                ["Settings_Modern"] = "Modern",
                ["Settings_Minimal"] = "Minimal",
                ["Settings_Rounded"] = "Yuvarlak",
                ["Settings_Flat"] = "Düz",
                ["Settings_Gradient"] = "Gradyan",
                
                // Grid
                ["Menu_GridSize"] = "📊 Izgara Boyutu",
                ["Menu_Columns"] = "Sütun",
                ["Menu_ItemSize"] = "📐 Öğe Boyutu",
                ["Size_Small"] = "Küçük",
                ["Size_Normal"] = "Normal",
                ["Size_Default"] = "Varsayılan",
                ["Size_Medium"] = "Orta",
                ["Size_Large"] = "Büyük",
                ["Size_ExtraLarge"] = "Çok Büyük",
                
                // Copy Path
                ["Menu_CopyPath"] = "📋 Yolu Kopyala",
                
                // Languages
                ["Lang_English"] = "English",
                ["Lang_Turkish"] = "Türkçe",
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


