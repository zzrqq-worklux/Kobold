using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using FoldRa.Core;
using FoldRa.Controls;
using Localization = FoldRa.Core.Localization;

namespace FoldRa.Windows
{
    public partial class SettingsWindow : Window
    {
        private AppConfig _config;
        private bool _isLoading = true;
        
        // Store original values for Cancel
        private string _originalLanguage;
        private string _originalTheme;
        private int _originalGridColumns;
        private bool _originalStartWithWindows;
        private string _originalIconStyle;

        private const string REGISTRY_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "FoldRa";

        public SettingsWindow()
        {
            InitializeComponent();
            _config = WidgetManager.Instance.Config;
            
            // Store original values
            _originalLanguage = _config.Language;
            _originalTheme = _config.Theme;
            _originalGridColumns = _config.DefaultGridColumns;
            _originalStartWithWindows = _config.StartWithWindows;
            _originalIconStyle = _config.IconStyle;
            
            LoadSettings();
            UpdateLocalizedText();
            _isLoading = false;
        }

        private void LoadSettings()
        {
            // Language
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if ((string)item.Tag == _config.Language)
                {
                    LanguageCombo.SelectedItem = item;
                    break;
                }
            }

            // Theme
            string theme = _config.Theme ?? "dark";
            foreach (ComboBoxItem item in ThemeCombo.Items)
            {
                if ((string)item.Tag == theme)
                {
                    ThemeCombo.SelectedItem = item;
                    break;
                }
            }

            // Grid columns
            int gridCols = _config.DefaultGridColumns > 0 ? _config.DefaultGridColumns : 3;
            foreach (ComboBoxItem item in GridColumnsCombo.Items)
            {
                if ((string)item.Tag == gridCols.ToString())
                {
                    GridColumnsCombo.SelectedItem = item;
                    break;
                }
            }

            // Startup
            StartupCheckbox.IsChecked = _config.StartWithWindows;
            
            // Icon Style
            if (IconStyleCombo != null)
            {
                string iconStyle = _config.IconStyle ?? "classic";
                foreach (ComboBoxItem item in IconStyleCombo.Items)
                {
                    if ((string)item.Tag == iconStyle)
                    {
                        IconStyleCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void UpdateLocalizedText()
        {
            // Headers
            LanguageHeader.Text = "🌐 " + Localization.Get("Settings_Language");
            ThemeHeader.Text = "🎨 " + Localization.Get("Settings_Theme");
            DefaultsHeader.Text = "📊 " + Localization.Get("Settings_Defaults");
            StartupHeader.Text = "🚀 " + Localization.Get("Settings_Startup");

            // Labels
            LanguageLabel.Text = Localization.Get("Settings_InterfaceLanguage");
            ThemeLabel.Text = Localization.Get("Settings_ColorTheme");
            GridLabel.Text = Localization.Get("Settings_DefaultGridColumns");
            StartupLabel.Text = Localization.Get("Settings_StartWithWindows");
            StartupNote.Text = "";

            // Theme options
            ThemeDark.Content = "🌙 " + Localization.Get("Settings_Dark");
            ThemeLight.Content = "☀️ " + Localization.Get("Settings_Light");

            // Buttons
            SaveButton.Content = Localization.Get("Dialog_Save");
            CancelButton.Content = Localization.Get("Dialog_Cancel");
            
            // Icon Style
            if (IconStyleHeader != null)
                IconStyleHeader.Text = "📁 " + Localization.Get("Settings_IconStyle");
            if (IconStyleLabel != null)
                IconStyleLabel.Text = Localization.Get("Settings_FolderIconStyle");
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RevertChanges();
            Close();
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            
            if (LanguageCombo.SelectedItem is ComboBoxItem item)
            {
                string lang = (string)item.Tag;
                _config.Language = lang;
                Localization.SetLanguage(lang);
                UpdateLocalizedText();
            }
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            
            if (ThemeCombo.SelectedItem is ComboBoxItem item)
            {
                string newTheme = (string)item.Tag;
                _config.Theme = newTheme;
                ThemeManager.SetTheme(newTheme);
            }
        }

        private void GridColumnsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            
            if (GridColumnsCombo.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse((string)item.Tag, out int cols))
                {
                    _config.DefaultGridColumns = cols;
                }
            }
        }

        private void StartupCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _config.StartWithWindows = StartupCheckbox.IsChecked ?? false;
        }
        
        private void IconStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            
            if (IconStyleCombo.SelectedItem is ComboBoxItem item)
            {
                _config.IconStyle = (string)item.Tag;
                // Preview immediately
                WidgetManager.Instance.RefreshAllWidgets();
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Apply registry startup setting
            SetStartupRegistry(_config.StartWithWindows);
            
            _config.Save();
            WidgetManager.Instance.RefreshAllWidgets();
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RevertChanges();
            Close();
        }
        
        private void RevertChanges()
        {
            // Revert all changes to original values
            _config.Language = _originalLanguage;
            _config.Theme = _originalTheme;
            _config.DefaultGridColumns = _originalGridColumns;
            _config.StartWithWindows = _originalStartWithWindows;
            _config.IconStyle = _originalIconStyle;
            
            // Revert UI
            Localization.SetLanguage(_originalLanguage);
            ThemeManager.SetTheme(_originalTheme);
            WidgetManager.Instance.RefreshAllWidgets();
        }
        
        private void SetStartupRegistry(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                            key.SetValue(APP_NAME, $"\"{exePath}\"");
                        }
                        else
                        {
                            key.DeleteValue(APP_NAME, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Registry error: {ex.Message}");
            }
        }
    }
}


