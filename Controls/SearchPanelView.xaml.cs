using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kobold.Core;
using Kobold.Core.FileSearch;
using Localization = Kobold.Core.Localization;

namespace Kobold.Controls
{
    /// <summary>
    /// One rendered search result. The view builds these off the background
    /// query result; <see cref="Entry"/> keeps the path the open/copy actions
    /// need while the text fields are already display-ready.
    /// </summary>
    public sealed class SearchResultRow
    {
        public SearchEntry Entry { get; set; }
        public string Glyph { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string SizeText { get; set; }
        public string ModifiedText { get; set; }
    }

    /// <summary>
    /// The search widget's content view. The query loop is debounced (200ms)
    /// and every query runs on the thread pool through
    /// <see cref="FileSearchIndexService.Search"/>; the UI thread only builds
    /// rows from the returned snapshot. Each new query cancels the previous
    /// <see cref="CancellationTokenSource"/>, and the service's status events
    /// are marshaled with <see cref="Dispatcher.BeginInvoke"/> (never Invoke:
    /// a blocking call can deadlock against a scan or shutdown).
    /// </summary>
    public partial class SearchPanelView : UserControl
    {
        private const int DebounceMilliseconds = 200;
        private const int ResultLimit = 300;

        private SearchData _data;
        private WidgetWindowBase _host;
        private FileSearchIndexService _service;
        private DispatcherTimer _debounce;
        private CancellationTokenSource _queryCts;
        private bool _attached;
        private bool _subscribed;
        private bool _detached;
        private bool _hasQuery;
        private bool _onboardingDismissed;
        private bool _rootsLoaded;
        private bool _rootsLoading;
        private MenuItem _openMenuItem;
        private MenuItem _locationMenuItem;
        private MenuItem _copyMenuItem;

        public SearchPanelView()
        {
            InitializeComponent();
        }

        /// <summary>The widget data this view was attached to.</summary>
        public SearchData Data => _data;

        /// <summary>The window that hosts the view in its chrome content slot.</summary>
        public WidgetWindowBase HostWindow => _host;

        /// <summary>
        /// Binds the view to its data and host. No service work happens here:
        /// the service is only touched once the view is in a live tree.
        /// </summary>
        public void Attach(SearchData data, WidgetWindowBase host)
        {
            _data = data;
            _host = host;

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
            _debounce.Tick += Debounce_Tick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            ResultsList.ContextMenu = BuildResultMenu();
            ResultsList.PreviewMouseRightButtonDown += ResultsList_PreviewMouseRightButtonDown;

            ApplyLocalization();
            _attached = true;
        }

        /// <summary>
        /// Drops every subscription, stops the debounce timer and cancels the
        /// in-flight query. Called when the hosting window closes.
        /// </summary>
        public void Detach()
        {
            if (_detached) return;
            _detached = true;

            if (_debounce != null)
            {
                _debounce.Stop();
                _debounce.Tick -= Debounce_Tick;
            }

            CancelQuery();

            if (_service != null && _subscribed)
            {
                _service.StatusChanged -= Service_StatusChanged;
                _service.ProgressChanged -= Service_ProgressChanged;
            }
            _subscribed = false;
            _service = null;

            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            if (ResultsList != null)
            {
                ResultsList.PreviewMouseRightButtonDown -= ResultsList_PreviewMouseRightButtonDown;
            }
        }

        /// <summary>Re-reads the service's in-memory status snapshot for a re-open.</summary>
        public void RefreshStatus()
        {
            if (_detached) return;
            if (_service == null) return;
            UpdateStatus(_service.Status);
        }

        /// <summary>Puts the caret in the box when the panel opens.</summary>
        public void FocusSearch()
        {
            if (_detached) return;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }

        /// <summary>Re-applies the localized labels after a language change.</summary>
        public void RefreshLocalization()
        {
            if (_detached) return;
            ApplyLocalization();
        }

        private void ApplyLocalization()
        {
            PlaceholderText.Text = Localization.Get("Search_Placeholder");
            ClearButton.ToolTip = Localization.Get("Search_Clear");

            EmptyText.Text = Localization.Get("Search_Empty");
            NoIndexText.Text = Localization.Get("Search_NoIndex");
            StartIndexButton.Content = Localization.Get("Search_StartIndexing");

            ScopeHint.Text = Localization.Get("Search_ConfigureHint");
            OnboardingDrivesText.Text = Localization.Get("Search_Drives");
            OnboardingStartButton.Content = Localization.Get("Search_StartIndexing");
            OnboardingLaterButton.Content = Localization.Get("Search_Later");

            RebuildButton.Content = Localization.Get("Search_Rebuild");
            if (_openMenuItem != null) _openMenuItem.Header = Localization.Get("Search_Open");
            if (_locationMenuItem != null) _locationMenuItem.Header = Localization.Get("Search_OpenLocation");
            if (_copyMenuItem != null) _copyMenuItem.Header = Localization.Get("Search_CopyPath");

            PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_detached) return;

            if (_service == null) _service = FileSearchIndexService.Instance;
            if (!_subscribed)
            {
                _service.StatusChanged += Service_StatusChanged;
                _service.ProgressChanged += Service_ProgressChanged;
                _subscribed = true;
            }

            // Loading never scans: it only reads the in-memory status and, for
            // a fresh install, offers the onboarding panel (Task 9 fills it).
            if (!IsConfigured())
            {
                if (!_onboardingDismissed) ShowOnboarding();
                UpdateStatus(_service.Status);
                return;
            }

            UpdateStatus(_service.Status);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // A hidden panel keeps its subscriptions (the window is only
            // collapsed), but a pending debounce must not fire while unseen.
            if (_debounce != null) _debounce.Stop();
        }

        private static bool IsConfigured()
        {
            try
            {
                AppConfig config = WidgetManager.Instance.Config;
                return config != null && config.FileSearch != null
                    && config.FileSearch.Configured && config.FileSearch.Enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #region Query loop

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_attached) return;

            PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (_debounce == null) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Debounce_Tick(object sender, EventArgs e)
        {
            if (_debounce != null) _debounce.Stop();
            RunQuery();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_debounce != null) _debounce.Stop();
                RunQuery();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down && ResultsList.Items.Count > 0)
            {
                ResultsList.Focus();
                if (ResultsList.SelectedIndex < 0) ResultsList.SelectedIndex = 0;
                e.Handled = true;
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            SearchBox.Focus();
            if (_debounce != null) _debounce.Stop();
            RunQuery();
        }

        private void RunQuery()
        {
            if (_detached) return;

            string text = (SearchBox.Text ?? string.Empty).Trim();
            CancelQuery();

            if (text.Length == 0)
            {
                _hasQuery = false;
                ResultsList.ItemsSource = null;
                EmptyState.Visibility = Visibility.Collapsed;
                ResultStatusText.Text = string.Empty;
                UpdateStatus(_service == null ? null : _service.Status);
                return;
            }

            if (_service == null) _service = FileSearchIndexService.Instance;

            FileSearchState state = _service.Status.State;
            if (state == FileSearchState.NoIndex || state == FileSearchState.Unconfigured
                || state == FileSearchState.Paused || state == FileSearchState.Error)
            {
                // Nothing to query yet: keep the state panel and its message.
                UpdateStatus(_service.Status);
                return;
            }

            var cts = new CancellationTokenSource();
            _queryCts = cts;
            CancellationToken token = cts.Token;
            string query = text;
            FileSearchIndexService service = _service;

            _hasQuery = true;
            EmptyState.Visibility = Visibility.Collapsed;
            // The query outcome owns its own line; the persistent service /
            // scan status stays in StatusText and keeps indexing precedence.
            ResultStatusText.Text = Localization.Get("Search_Searching");

            Task.Run(() =>
            {
                SearchQueryResult result;
                Exception error = null;
                try
                {
                    result = service.Search(query, ResultLimit, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    result = null;
                    error = ex;
                }

                if (token.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_detached || token.IsCancellationRequested) return;
                    if (!ReferenceEquals(_queryCts, cts)) return;
                    ApplyQueryResult(result, error);
                }));
            }, token);
        }

        private void CancelQuery()
        {
            CancellationTokenSource previous = _queryCts;
            _queryCts = null;
            if (previous == null) return;

            try { previous.Cancel(); }
            catch (Exception) { }
            previous.Dispose();
        }

        /// <summary>Stops an in-flight query and empties the list for a state that cannot show results.</summary>
        private void ClearResults()
        {
            CancelQuery();
            _hasQuery = false;
            if (ResultsList != null) ResultsList.ItemsSource = null;
            if (EmptyState != null) EmptyState.Visibility = Visibility.Collapsed;
            if (ResultStatusText != null) ResultStatusText.Text = string.Empty;
        }

        private void ApplyQueryResult(SearchQueryResult result, Exception error)
        {
            if (_detached) return;

            if (error != null)
            {
                ResultsList.ItemsSource = null;
                EmptyState.Visibility = Visibility.Collapsed;
                ResultStatusText.Text = Localization.Get("Search_Error") + ": " + error.Message;
                return;
            }

            var rows = new List<SearchResultRow>();
            if (result != null)
            {
                for (int i = 0; i < result.Items.Count; i++)
                {
                    SearchResultRow row = BuildRow(result.Items[i]);
                    if (row != null) rows.Add(row);
                }
            }

            ResultsList.ItemsSource = rows;
            if (rows.Count == 0)
            {
                // The body carries the "no matches" sentence; a second copy on
                // the status line would only repeat it.
                EmptyState.Visibility = Visibility.Visible;
                ResultStatusText.Text = string.Empty;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            // Only a truncated set needs a count line; a full result set is
            // self-evident from the list itself.
            ResultStatusText.Text = result.LimitReached || result.TotalMatches > rows.Count
                ? Localization.Format("Search_ShowingFirst", rows.Count, result.TotalMatches)
                : string.Empty;
        }

        private static string ReadyText(FileSearchStatus status)
        {
            string text = Localization.Get("Search_Ready");
            if (status != null && status.SkippedDirectories > 0)
            {
                text += " · " + Localization.Format("Search_SkippedFolders", status.SkippedDirectories);
            }
            return text;
        }

        private static SearchResultRow BuildRow(SearchEntry entry)
        {
            if (entry == null) return null;

            string parent;
            try { parent = Path.GetDirectoryName(entry.Path); }
            catch (Exception) { parent = null; }
            if (string.IsNullOrEmpty(parent)) parent = entry.Path;

            string modified = string.Empty;
            if (entry.ModifiedUtc > 0)
            {
                try
                {
                    modified = DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedUtc)
                        .LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                    modified = string.Empty;
                }
            }

            return new SearchResultRow
            {
                Entry = entry,
                Glyph = entry.IsDirectory ? "📁" : "📄",
                Name = entry.Name ?? string.Empty,
                Path = parent ?? string.Empty,
                SizeText = entry.IsDirectory ? string.Empty : FormatSize(entry.SizeBytes),
                ModifiedText = modified
            };
        }

        private static string FormatSize(long bytes)
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

        #region Status

        private void Service_StatusChanged(object sender, FileSearchStatus status)
        {
            PostToUi(() => UpdateStatus(status));
        }

        private void Service_ProgressChanged(object sender, ScanProgress progress)
        {
            if (progress == null) return;
            PostToUi(() =>
            {
                if (_detached || ScanProgress.Visibility != Visibility.Visible) return;
                StatusText.Text = Localization.Format("Search_Indexing", progress.Files, progress.Directories);
            });
        }

        private void PostToUi(Action action)
        {
            if (_detached || action == null) return;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(action);
        }

        private void UpdateStatus(FileSearchStatus status)
        {
            if (_detached || status == null) return;

            switch (status.State)
            {
                case FileSearchState.Indexing:
                case FileSearchState.Reconciling:
                    HideOnboarding();
                    EmptyState.Visibility = Visibility.Collapsed;
                    NoIndexState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = Visibility.Collapsed;
                    ScanProgress.Visibility = Visibility.Visible;
                    StatusText.Text = Localization.Format("Search_Indexing",
                        status.FilesIndexed, status.DirectoriesScanned);
                    break;

                case FileSearchState.Ready:
                    HideOnboarding();
                    ScanProgress.Visibility = Visibility.Collapsed;
                    NoIndexState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = status.NeedsRebuild ? Visibility.Visible : Visibility.Collapsed;
                    if (!_hasQuery)
                    {
                        ResultsList.ItemsSource = null;
                        EmptyState.Visibility = Visibility.Collapsed;
                    }
                    StatusText.Text = ReadyText(status);
                    break;

                case FileSearchState.NoIndex:
                    ClearResults();
                    HideOnboarding();
                    ScanProgress.Visibility = Visibility.Collapsed;
                    EmptyState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = Visibility.Collapsed;
                    NoIndexState.Visibility = Visibility.Visible;
                    // The state body carries the sentence; repeating it on the
                    // status line would just say the same thing twice.
                    StatusText.Text = string.Empty;
                    break;

                case FileSearchState.Paused:
                    ClearResults();
                    HideOnboarding();
                    ScanProgress.Visibility = Visibility.Collapsed;
                    EmptyState.Visibility = Visibility.Collapsed;
                    NoIndexState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = Visibility.Collapsed;
                    StatusText.Text = Localization.Get("Search_Paused");
                    break;

                case FileSearchState.Error:
                    ClearResults();
                    HideOnboarding();
                    ScanProgress.Visibility = Visibility.Collapsed;
                    EmptyState.Visibility = Visibility.Collapsed;
                    NoIndexState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = Visibility.Visible;
                    StatusText.Text = string.IsNullOrEmpty(status.Message)
                        ? Localization.Get("Search_Error")
                        : Localization.Get("Search_Error") + ": " + status.Message;
                    break;

                default:
                    ClearResults();
                    HideOnboarding();
                    ScanProgress.Visibility = Visibility.Collapsed;
                    EmptyState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = Visibility.Collapsed;
                    if (IsConfigured())
                    {
                        // The config is in place but the service has not
                        // published its first state yet: never flash the
                        // onboarding panel over a configured install.
                        NoIndexState.Visibility = Visibility.Visible;
                        StatusText.Text = string.Empty;
                        break;
                    }

                    StatusText.Text = Localization.Get("Search_NotConfigured");
                    if (!_onboardingDismissed)
                    {
                        ShowOnboarding();
                    }
                    else
                    {
                        // "Later" left the onboarding panel: keep a way back.
                        NoIndexState.Visibility = Visibility.Visible;
                    }
                    break;
            }
        }

        #endregion

        #region Onboarding

        private void ShowOnboarding()
        {
            if (_detached) return;
            _onboardingDismissed = false;
            NoIndexState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            OnboardingPanel.Visibility = Visibility.Visible;
            PopulateRoots();
        }

        private void HideOnboarding()
        {
            if (OnboardingPanel != null) OnboardingPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Lists the roots a scan would walk. Drive enumeration and the
        /// configured-root existence probes run off the UI thread.
        /// </summary>
        private void PopulateRoots()
        {
            if (_rootsLoaded || _rootsLoading) return;
            _rootsLoading = true;

            string[] configured = null;
            try
            {
                FileSearchSettings settings = WidgetManager.Instance.Config == null
                    ? null
                    : WidgetManager.Instance.Config.FileSearch;
                if (settings != null) configured = settings.Roots;
            }
            catch (Exception)
            {
                configured = null;
            }

            Task.Run(() => SearchRoots.Resolve(configured)).ContinueWith(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    // Leave _rootsLoaded false so the next onboarding show
                    // retries instead of keeping a permanently empty list.
                    PostToUi(() => { _rootsLoading = false; });
                    return;
                }

                IReadOnlyList<string> roots = task.Result;
                PostToUi(() =>
                {
                    _rootsLoading = false;
                    _rootsLoaded = true;
                    ApplyRoots(roots);
                });
            });
        }

        private void ApplyRoots(IReadOnlyList<string> roots)
        {
            if (_detached) return;
            RootsList.Items.Clear();
            if (roots == null) return;

            for (int i = 0; i < roots.Count; i++)
            {
                RootsList.Items.Add(new TextBlock
                {
                    Text = roots[i],
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
        }

        private void StartIndexButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsConfigured())
            {
                HideOnboarding();
                if (_service != null) _service.RequestRebuild();
                return;
            }

            // No settings yet: Task 9 replaces this with the settings form.
            ShowOnboarding();
            StatusText.Text = Localization.Get("Search_NotConfigured");
        }

        private void OnboardingStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsConfigured())
            {
                HideOnboarding();
                if (_service != null) _service.RequestRebuild();
                return;
            }

            // Task 9 owns the settings editor; until then this stays a stub
            // that explains why nothing can start yet.
            StatusText.Text = Localization.Get("Search_NotConfigured");
        }

        private void OnboardingLaterButton_Click(object sender, RoutedEventArgs e)
        {
            _onboardingDismissed = true;
            HideOnboarding();
            UpdateStatus(_service == null ? null : _service.Status);
        }

        private void RebuildButton_Click(object sender, RoutedEventArgs e)
        {
            if (_service != null) _service.RequestRebuild();
        }

        #endregion

        #region Open / context menu

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // A double-click on the empty area below the rows must not open
            // the first result.
            var container = ItemsControl.ContainerFromElement(
                ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (container == null) return;
            container.IsSelected = true;
            OpenSelected();
        }

        private void ResultsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            OpenSelected();
            e.Handled = true;
        }

        private void OpenSelected()
        {
            var row = ResultsList.SelectedItem as SearchResultRow;
            if (row == null && ResultsList.Items.Count > 0)
            {
                row = ResultsList.Items[0] as SearchResultRow;
                if (row != null) ResultsList.SelectedIndex = 0;
            }
            if (row != null) OpenRow(row);
        }

        private void OpenRow(SearchResultRow row)
        {
            if (row == null || row.Entry == null) return;

            try
            {
                if (row.Entry.IsDirectory)
                {
                    Process.Start("explorer.exe", "\"" + row.Entry.Path + "\"");
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = row.Entry.Path,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                // Never a modal dialog from a result row: the status line is
                // the error surface.
                StatusText.Text = Localization.Get("Search_Error") + ": " + ex.Message;
            }
        }

        private ContextMenu BuildResultMenu()
        {
            _openMenuItem = new MenuItem { Header = Localization.Get("Search_Open") };
            _openMenuItem.Click += (s, e) => OpenSelected();

            _locationMenuItem = new MenuItem { Header = Localization.Get("Search_OpenLocation") };
            _locationMenuItem.Click += (s, e) => OpenLocationSelected();

            _copyMenuItem = new MenuItem { Header = Localization.Get("Search_CopyPath") };
            _copyMenuItem.Click += (s, e) => CopyPathSelected();

            var menu = new ContextMenu();
            menu.Items.Add(_openMenuItem);
            menu.Items.Add(_locationMenuItem);
            menu.Items.Add(_copyMenuItem);
            menu.Opened += (s, e) =>
            {
                bool enabled = ResultsList.SelectedItem is SearchResultRow;
                _openMenuItem.IsEnabled = enabled;
                _locationMenuItem.IsEnabled = enabled;
                _copyMenuItem.IsEnabled = enabled;
            };
            return menu;
        }

        private void ResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var container = ItemsControl.ContainerFromElement(
                ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (container != null) container.IsSelected = true;
        }

        private void OpenLocationSelected()
        {
            var row = ResultsList.SelectedItem as SearchResultRow;
            if (row == null || row.Entry == null) return;

            try
            {
                if (row.Entry.IsDirectory)
                {
                    Process.Start("explorer.exe", "\"" + row.Entry.Path + "\"");
                }
                else
                {
                    Process.Start("explorer.exe", "/select,\"" + row.Entry.Path + "\"");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = Localization.Get("Search_Error") + ": " + ex.Message;
            }
        }

        private void CopyPathSelected()
        {
            var row = ResultsList.SelectedItem as SearchResultRow;
            if (row == null || row.Entry == null) return;

            try
            {
                Clipboard.SetText(row.Entry.Path);
            }
            catch (Exception ex)
            {
                StatusText.Text = Localization.Get("Search_Error") + ": " + ex.Message;
            }
        }

        #endregion
    }
}
