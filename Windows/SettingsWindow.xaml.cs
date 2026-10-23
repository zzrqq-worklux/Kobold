using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Newtonsoft.Json;
using Kobold.Core;
using Kobold.Core.FileSearch;
using Kobold.Controls;
using Kobold.Helpers;
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
        private bool _originalPinStorage;

        // Storage row: the real Quick Access state (probe is read-only; the
        // pin/unpin actions only ever run from the button click).
        private bool? _storagePinned;

        // File search: the section writes the live config on Save, so Cancel
        // restores this deep copy instead of the (mutated) live object.
        private FileSearchSettings _originalFileSearch;
        private FileSearchIndexService _fileSearchService;
        private bool _fileSearchInfoPending;
        private bool _closed;

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
            _originalPinStorage = _config.PinStorageToQuickAccess;
            _originalFileSearch = CloneFileSearch(_config.FileSearch);

            // Live status only (in-memory snapshot). The service touches no
            // SQLite until a scan/read is explicitly queued.
            _fileSearchService = FileSearchIndexService.Instance;
            _fileSearchService.StatusChanged += FileSearchService_StatusChanged;
            Closed += SettingsWindow_Closed;
            
            LoadSettings();
            UpdateLocalizedText();
            UpdateFileSearchStatus(_fileSearchService.Status);
            _isLoading = false;
            RefreshFileSearchIndexInfo();
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

                // A retired config value has no item: select classic. This is
                // UI state only - the config value is not rewritten here.
                if (IconStyleCombo.SelectedItem == null && IconStyleCombo.Items.Count > 0)
                {
                    IconStyleCombo.SelectedIndex = 0;
                }
            }

            // Storage: GetStoragePath() creates the folder when missing, and
            // the probe only enumerates Quick Access (it never pins).
            _storagePinned = QuickAccess.TryIsPinned(Utils.GetStoragePath());

            // File search: defaults when the feature was never configured.
            LoadFileSearchSettings();
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
                IconStyleLabel.Text = Localization.Get("Settings_WidgetIconStyle");

            // Icon Style options (emoji prefix + localized name per style)
            string[] styleEmojis = { "📂", "✍️" };
            string[] styleKeys = { "Settings_Classic", "Settings_HandDrawn" };
            for (int i = 0; i < IconStyleCombo.Items.Count && i < styleKeys.Length; i++)
            {
                if (IconStyleCombo.Items[i] is ComboBoxItem styleItem)
                {
                    styleItem.Content = styleEmojis[i] + " " + Localization.Get(styleKeys[i]);
                }
            }

            // Kobold Storage
            StorageHeader.Text = "📦 " + Localization.Get("Settings_StorageHeader");
            StoragePathText.Text = Utils.GetStoragePath();
            StorageOpenButton.Content = Localization.Get("Settings_StorageOpen");
            UpdateStoragePinButtonText();

            // File Search
            FileSearchHeader.Text = "🔎 " + Localization.Get("Settings_FileSearch");
            FileSearchAllFilesCheck.Content = Localization.Get("Search_SettingsAllFiles");
            FileSearchExtensionlessCheck.Content = Localization.Get("Search_SettingsExtensionless");
            FileSearchFoldersCheck.Content = Localization.Get("Search_SettingsFolders");
            FileSearchTypesLabel.Text = Localization.Get("Search_SettingsTypes");
            FileSearchExcludesLabel.Text = Localization.Get("Search_SettingsExcludes");
            FileSearchReconcileLabel.Text = Localization.Get("Search_SettingsReconcile");
            FileSearchRootsLabel.Text = Localization.Get("Search_SettingsRoots");
            FileSearchRebuildButton.Content = Localization.Get("Search_SettingsRebuild");
            UpdateFileSearchPauseText();

            // The size and last-updated values are re-rendered from a
            // background read so a language switch never shows stale English.
            if (_fileSearchService != null)
            {
                UpdateFileSearchStatus(_fileSearchService.Status);
                RefreshFileSearchIndexInfo();
            }

            // About section
            AboutHeaderText.Text = "ℹ️ " + Localization.Get("Settings_About");
            AboutTaglineText.Text = Localization.Get("UI_Tagline");
            VersionText.Text = "Version " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        }

        #region File search (Task 9)

        private void SettingsWindow_Closed(object sender, EventArgs e)
        {
            _closed = true;
            if (_fileSearchService != null)
            {
                _fileSearchService.StatusChanged -= FileSearchService_StatusChanged;
            }
        }

        /// <summary>Cancel must restore the settings the window opened with, not the live object.</summary>
        private static FileSearchSettings CloneFileSearch(FileSearchSettings settings)
        {
            if (settings == null) return null;
            return JsonConvert.DeserializeObject<FileSearchSettings>(
                JsonConvert.SerializeObject(settings));
        }

        private void LoadFileSearchSettings()
        {
            FileSearchSettings settings = _config == null ? null : _config.FileSearch;

            FileSearchAllFilesCheck.IsChecked = settings != null && settings.IndexAllFiles;
            FileSearchExtensionlessCheck.IsChecked = settings == null || settings.IndexExtensionless;
            FileSearchFoldersCheck.IsChecked = settings == null || settings.IndexFolders;

            FileSearchTypesBox.Text = settings == null || settings.Types == null || settings.Types.Length == 0
                ? string.Join(", ", FileSearchSettings.DefaultTypes)
                : string.Join(", ", settings.Types);
            FileSearchExcludesBox.Text = settings == null || settings.ExcludeDirectories == null
                ? string.Empty
                : string.Join(Environment.NewLine, settings.ExcludeDirectories);
            FileSearchReconcileBox.Text = (settings == null ? 10 : settings.ReconcileMinutes)
                .ToString(CultureInfo.InvariantCulture);

            PopulateFileSearchRoots(settings);
        }

        /// <summary>
        /// One checkbox per root. An empty configured set means "every fixed
        /// NTFS drive", so all fixed drives start checked; an explicit set is
        /// listed in full (extras included) and only its entries are checked.
        /// </summary>
        private void PopulateFileSearchRoots(FileSearchSettings settings)
        {
            FileSearchRootsList.Items.Clear();

            var configured = new List<string>();
            if (settings != null && settings.Roots != null)
            {
                foreach (string raw in settings.Roots)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    configured.Add(raw.Trim());
                }
            }

            var shown = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in configured)
            {
                if (!seen.Add(RootKey(root))) continue;
                shown.Add(root);
            }

            IReadOnlyList<string> fixedRoots = SearchRoots.FixedNtfsRoots();
            foreach (string root in fixedRoots)
            {
                if (!seen.Add(RootKey(root))) continue;
                shown.Add(root);
            }

            bool allFixed = configured.Count == 0;
            foreach (string root in shown)
            {
                FileSearchRootsList.Items.Add(new CheckBox
                {
                    Content = root,
                    Tag = root,
                    IsChecked = allFixed || ContainsRoot(configured, root),
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
        }

        /// <summary>
        /// Reads the controls into a settings object. Saving the section is
        /// what marks the feature configured: Configured=true, Enabled=true.
        /// </summary>
        private FileSearchSettings BuildFileSearchSettings()
        {
            FileSearchSettings settings = FileSearchSettings.CreateDefault();
            settings.Configured = true;
            settings.Enabled = true;
            settings.IndexAllFiles = FileSearchAllFilesCheck.IsChecked == true;
            settings.IndexExtensionless = FileSearchExtensionlessCheck.IsChecked == true;
            settings.IndexFolders = FileSearchFoldersCheck.IsChecked == true;
            settings.Types = SplitList(FileSearchTypesBox.Text, ',', ';');
            settings.ExcludeDirectories = SplitList(FileSearchExcludesBox.Text, '\n', '\r', ';');
            settings.ReconcileMinutes = ParseReconcileMinutes(FileSearchReconcileBox.Text);
            settings.Roots = ReadCheckedRoots();
            settings.Normalize();
            return settings;
        }

        private static string[] SplitList(string text, params char[] separators)
        {
            if (string.IsNullOrWhiteSpace(text)) return new string[0];

            string[] parts = text.Split(separators);
            var values = new List<string>();
            foreach (string part in parts)
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                values.Add(part.Trim());
            }
            return values.ToArray();
        }

        /// <summary>A missing or out-of-range reconcile interval falls back to ten minutes.</summary>
        private static int ParseReconcileMinutes(string text)
        {
            int minutes;
            if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out minutes))
            {
                return 10;
            }
            if (minutes < 0 || minutes > 1440) return 10;
            return minutes;
        }

        /// <summary>
        /// The checked roots. "Every fixed drive checked" is stored as the
        /// empty array (the config's "all fixed drives" form), unless the user
        /// also checked an extra configured directory.
        /// </summary>
        private string[] ReadCheckedRoots()
        {
            var selected = new List<string>();
            foreach (object item in FileSearchRootsList.Items)
            {
                var box = item as CheckBox;
                if (box == null || box.IsChecked != true) continue;

                string root = box.Tag as string;
                if (!string.IsNullOrWhiteSpace(root)) selected.Add(root.Trim());
            }

            IReadOnlyList<string> fixedRoots = SearchRoots.FixedNtfsRoots();
            if (fixedRoots.Count > 0 && selected.Count == fixedRoots.Count
                && CoversEveryFixedRoot(selected, fixedRoots))
            {
                return new string[0];
            }
            return selected.ToArray();
        }

        private static bool CoversEveryFixedRoot(List<string> selected, IReadOnlyList<string> fixedRoots)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in selected) seen.Add(RootKey(root));

            foreach (string root in fixedRoots)
            {
                if (!seen.Contains(RootKey(root))) return false;
            }
            return true;
        }

        private static bool ContainsRoot(List<string> roots, string root)
        {
            string key = RootKey(root);
            foreach (string candidate in roots)
            {
                if (string.Equals(RootKey(candidate), key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Case- and trailing-separator-insensitive root comparison key.</summary>
        private static string RootKey(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return string.Empty;
            return root.Trim().TrimEnd('\\').ToLowerInvariant();
        }

        /// <summary>
        /// Save is refused with a localized warning when the user unchecked
        /// every drive: an empty root set means "all fixed drives", the exact
        /// opposite of the visible selection.
        /// </summary>
        private bool FileSearchRootsAreValid()
        {
            if (FileSearchRootsList.Items.Count == 0) return true;

            foreach (object item in FileSearchRootsList.Items)
            {
                var box = item as CheckBox;
                if (box != null && box.IsChecked == true) return true;
            }

            MessageBox.Show(this, Localization.Get("Search_SettingsNeedRoot"),
                "Kobold", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool IsFileSearchConfigured()
        {
            return _config != null && _config.FileSearch != null && _config.FileSearch.Configured;
        }

        private void FileSearchRebuildButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsFileSearchConfigured())
            {
                MessageBox.Show(this, Localization.Get("Search_NotConfigured"),
                    "Kobold", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(this, Localization.Get("Search_RebuildConfirm"), "Kobold",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            _fileSearchService.RequestRebuild();
            UpdateFileSearchStatus(_fileSearchService.Status);
        }

        private void FileSearchPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsFileSearchConfigured()) return;

            if (_fileSearchService.Status.State == FileSearchState.Paused)
            {
                _fileSearchService.Resume();
            }
            else
            {
                _fileSearchService.Pause();
            }
            UpdateFileSearchStatus(_fileSearchService.Status);
        }

        private void FileSearchService_StatusChanged(object sender, FileSearchStatus status)
        {
            if (_closed) return;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed) return;
                UpdateFileSearchStatus(status);
            }));
        }

        private void UpdateFileSearchStatus(FileSearchStatus status)
        {
            if (status == null || _closed) return;

            FileSearchStatusText.Text = Localization.Format("Search_SettingsStatus", FileSearchStateLabel(status));
            UpdateFileSearchPauseText();

            // A published scan changes the last-updated time and the database
            // size; re-read them off the UI thread.
            if (status.State == FileSearchState.Ready || status.State == FileSearchState.NoIndex)
            {
                RefreshFileSearchIndexInfo();
            }
        }

        private static string FileSearchStateLabel(FileSearchStatus status)
        {
            string label;
            switch (status.State)
            {
                case FileSearchState.Indexing:
                case FileSearchState.Reconciling:
                    label = Localization.Format("Search_Indexing",
                        status.FilesIndexed, status.DirectoriesScanned);
                    break;
                case FileSearchState.Ready:
                    label = status.FilesIndexed > 0
                        ? Localization.Format("Search_SettingsFiles", status.FilesIndexed)
                        : Localization.Get("Search_Ready");
                    break;
                case FileSearchState.Paused:
                    label = Localization.Get("Search_Paused");
                    break;
                case FileSearchState.NoIndex:
                    label = Localization.Get("Search_NoIndex");
                    break;
                case FileSearchState.Error:
                    label = Localization.Get("Search_Error");
                    break;
                default:
                    label = Localization.Get("Search_NotConfigured");
                    break;
            }

            if (status.NeedsRebuild) label += " · " + Localization.Get("Search_NeedsRebuild");
            return label;
        }

        private void UpdateFileSearchPauseText()
        {
            if (FileSearchPauseButton == null) return;

            bool paused = _fileSearchService != null
                && _fileSearchService.Status.State == FileSearchState.Paused;
            FileSearchPauseButton.Content = Localization.Get(
                paused ? "Search_SettingsResume" : "Search_SettingsPause");
            FileSearchPauseButton.IsEnabled = IsFileSearchConfigured();
        }

        /// <summary>
        /// Reads the index size and last scan time. The store (SQLite) and the
        /// filesystem work run on the thread pool; only the formatted strings
        /// land back on the UI thread through BeginInvoke.
        /// </summary>
        private void RefreshFileSearchIndexInfo()
        {
            if (_closed || _fileSearchInfoPending || _fileSearchService == null) return;
            _fileSearchInfoPending = true;

            FileSearchIndexService service = _fileSearchService;
            string databasePath = service.Status.DatabasePath;

            Task.Run(() =>
            {
                IndexStatus index = null;
                long size = 0;
                try { index = service.ReadStatus(); }
                catch (Exception) { index = null; }

                size = FileLength(databasePath) + FileLength(databasePath + "-wal")
                    + FileLength(databasePath + "-shm");

                DateTime? lastScan = index == null ? (DateTime?)null : index.LastScanUtc;
                PostFileSearchInfo(size, lastScan);
            });
        }

        private static long FileLength(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void PostFileSearchInfo(long size, DateTime? lastScan)
        {
            if (_closed) return;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _fileSearchInfoPending = false;
                if (_closed) return;

                FileSearchSizeText.Text = Localization.Format("Search_IndexSize", FormatFileSize(size));
                FileSearchLastUpdatedText.Text = Localization.Format("Search_LastUpdated",
                    lastScan.HasValue
                        ? lastScan.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        : "—");
            }));
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 0) bytes = 0;

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            string number = unit == 0
                ? bytes.ToString("N0", CultureInfo.CurrentCulture)
                : value.ToString("0.#", CultureInfo.CurrentCulture);
            return number + " " + units[unit];
        }

        #endregion

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
                if (widget is FolderWidget folder) folder.ApplyPanelOpacity();
                else if (widget is WidgetWindowBase window) window.ApplyPanelOpacity();
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
                WidgetManager.Instance.ApplyIconStyle();
            }
        }

        private void StorageOpenButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", "\"" + Utils.GetStoragePath() + "\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kobold] Open storage failed: {ex.Message}");
            }
        }

        private void StoragePinButton_Click(object sender, RoutedEventArgs e)
        {
            string path = Utils.GetStoragePath();
            bool isPinned = _storagePinned == true;

            bool changed = isPinned ? QuickAccess.Unpin(path) : QuickAccess.Pin(path);
            if (!changed)
            {
                MessageBox.Show(this,
                    Localization.Get(isPinned ? "Settings_StorageUnpinFailed" : "Settings_StoragePinFailed"),
                    "Kobold", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Re-read the real state (read-only) so the toggle never lies.
            _storagePinned = WaitForPinState(path, !isPinned);
            UpdateStoragePinButtonText();

            // The desired state follows the successful action; Save persists it.
            _config.PinStorageToQuickAccess = !isPinned;
        }

        /// <summary>
        /// Explorer applies pintohome/unpinfromhome asynchronously, so the
        /// probe can still report the old state right after a successful
        /// action; poll it briefly before trusting the result.
        /// </summary>
        private static bool? WaitForPinState(string path, bool wanted)
        {
            bool? state = null;
            for (int i = 0; i < 10; i++)
            {
                state = QuickAccess.TryIsPinned(path);
                if (state == wanted) return state;
                System.Threading.Thread.Sleep(100);
            }
            return state;
        }

        private void UpdateStoragePinButtonText()
        {
            StoragePinButton.Content = Localization.Get(
                _storagePinned == true ? "Settings_StorageUnpin" : "Settings_StoragePin");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!FileSearchRootsAreValid()) return;

            // Saving the file-search section is what configures the feature;
            // the service compares the fingerprint against the published index
            // and flags NeedsRebuild without starting a scan.
            FileSearchSettings fileSearch = BuildFileSearchSettings();
            _config.FileSearch = fileSearch;

            // Apply registry startup setting
            SetStartupRegistry(_config.StartWithWindows);

            _config.Save();
            FileSearchIndexService.Instance.ApplySettings(fileSearch);
            WidgetManager.Instance.RefreshAllWidgets();
            WidgetManager.Instance.ApplyIconStyle();
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
            _config.PinStorageToQuickAccess = _originalPinStorage;
            // The section writes the live object on save, so restore the deep
            // copy taken when the window opened and re-apply it to the service.
            _config.FileSearch = CloneFileSearch(_originalFileSearch);
            FileSearchIndexService.Instance.ApplySettings(_config.FileSearch);
            // Best-effort: re-apply a desired pin, but never auto-unpin, so a
            // manual pin done while the dialog was open survives the cancel.
            if (_originalPinStorage)
            {
                QuickAccess.EnsurePinned(Utils.GetStoragePath());
            }
            PanelOpacitySlider.Value = _originalPanelOpacity * 100.0; // re-applies opacity to widgets
            IslandDelaySlider.Value = _originalIslandDelay * 1000.0; // re-applies delay to the island
            
            // Revert UI
            Localization.SetLanguage(_originalLanguage);
            ThemeManager.SetTheme(_originalTheme);
            WidgetManager.Instance.RefreshAllWidgets();
            WidgetManager.Instance.ApplyIconStyle();
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


