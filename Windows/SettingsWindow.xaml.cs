using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Kobold.Core;
using Kobold.Controls;
using Localization = Kobold.Core.Localization;

namespace Kobold.Windows
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
        private double _originalPanelOpacity;
        private double _originalIslandDelay;

        private const string REGISTRY_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "Kobold";

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
            _originalPanelOpacity = _config.PanelOpacity;
            _originalIslandDelay = _config.IslandCollapseDelay;
            
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
            
            // Storing behaviour
            HideDesktopCheckbox.IsChecked = _config.HideDesktopSourceOnStore;

            // Panel opacity
            PanelOpacitySlider.Value = Math.Max(20, Math.Min(100, _config.PanelOpacity * 100.0));

            // Island auto-collapse delay (slider is in ms)
            double islandDelayMs = _config.IslandCollapseDelay * 1000.0;
            IslandDelaySlider.Value = Math.Max(IslandDelaySlider.Minimum, Math.Min(IslandDelaySlider.Maximum, islandDelayMs));
            
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
            // Window title & header
            Title = Localization.Get("UI_AppName") + " " + Localization.Get("Settings_Title");
            SettingsHeaderText.Text = "⚙️ " + Localization.Get("Settings_Title");

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
            HideDesktopLabel.Text = Localization.Get("Settings_HideDesktopSource");
            PanelOpacityLabel.Text = Localization.Get("Settings_PanelOpacity");
            PanelOpacityValue.Text = (int)Math.Round(PanelOpacitySlider.Value) + "%";

            // Island
            IslandHeader.Text = "🏝️ " + Localization.Get("Settings_Island");
            IslandDelayLabel.Text = Localization.Get("Settings_IslandCollapseDelay");
            IslandDelayValue.Text = (IslandDelaySlider.Value / 1000.0).ToString("0.0") + "s";

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

            // Icon Style options (emoji prefix + localized name per style)
            string[] styleEmojis = { "📂", "🗂️", "📁", "🟦", "▬", "🌈" };
            string[] styleKeys = { "Settings_Classic", "Settings_Modern", "Settings_Minimal", "Settings_Rounded", "Settings_Flat", "Settings_Gradient" };
            for (int i = 0; i < IconStyleCombo.Items.Count && i < styleKeys.Length; i++)
            {
                if (IconStyleCombo.Items[i] is ComboBoxItem styleItem)
                {
                    styleItem.Content = styleEmojis[i] + " " + Localization.Get(styleKeys[i]);
                }
            }

            // About section
            AboutHeaderText.Text = "ℹ️ " + Localization.Get("Settings_About");
            AboutTaglineText.Text = Localization.Get("UI_Tagline");
        }

        /// <summary>
        /// Re-applies localized text to all open widgets (empty-state hint, pin tooltip)
        /// </summary>
        private void RefreshWidgetTexts()
        {
            foreach (var widget in WidgetManager.Instance.Widgets)
            {
                widget.UpdateUI();
            }
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

        private void HideDesktopCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _config.HideDesktopSourceOnStore = HideDesktopCheckbox.IsChecked ?? true;
        }

        private void PanelOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            _config.PanelOpacity = Math.Max(0.2, Math.Min(1.0, e.NewValue / 100.0));
            PanelOpacityValue.Text = (int)Math.Round(e.NewValue) + "%";
            // Live preview on pinned panels (visible next to the modal settings window)
            foreach (var widget in WidgetManager.Instance.Widgets)
            {
                widget.ApplyPanelOpacity();
            }
        }

        private void IslandDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            _config.IslandCollapseDelay = Math.Max(0.3, Math.Min(5.0, e.NewValue / 1000.0));
            IslandDelayValue.Text = (e.NewValue / 1000.0).ToString("0.0") + "s";
            // Apply to the live island immediately
            WidgetManager.Instance.ApplyIslandSettings();
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
            RefreshWidgetTexts();
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
            _config.PanelOpacity = _originalPanelOpacity;
            _config.IslandCollapseDelay = _originalIslandDelay;
            PanelOpacitySlider.Value = _originalPanelOpacity * 100.0; // re-applies opacity to widgets
            IslandDelaySlider.Value = _originalIslandDelay * 1000.0; // re-applies delay to the island
            
            // Revert UI
            Localization.SetLanguage(_originalLanguage);
            ThemeManager.SetTheme(_originalTheme);
            WidgetManager.Instance.RefreshAllWidgets();
            RefreshWidgetTexts();
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


