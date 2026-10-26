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
using Kobold.Helpers;
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
        private FileSearchState _viewState;
        private Point _dragStart;
        private SearchResultRow _dragRow;
        private MenuItem _openMenuItem;
        private MenuItem _locationMenuItem;
        private MenuItem _copyMenuItem;

        /// <summary>
        /// Test seam (XamlLoadCheck): when set, starting the onboarding routes
        /// the scan request here instead of the process-wide service, so the
        /// configured-install path can be pinned without scanning the machine.
        /// Production never sets it.
        /// </summary>
        internal Action<FileSearchSettings, IReadOnlyList<string>> StartScanForChecks { get; set; }

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
            // Drag-out source: the handlers never mark the press handled, so
            // selection and the double-click-to-open path keep working.
            ResultsList.PreviewMouseLeftButtonDown += ResultsList_PreviewMouseLeftButtonDown;
            ResultsList.PreviewMouseMove += ResultsList_PreviewMouseMove;
            ResultsList.PreviewMouseLeftButtonUp += ResultsList_PreviewMouseLeftButtonUp;

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
                ResultsList.PreviewMouseLeftButtonDown -= ResultsList_PreviewMouseLeftButtonDown;
                ResultsList.PreviewMouseMove -= ResultsList_PreviewMouseMove;
                ResultsList.PreviewMouseLeftButtonUp -= ResultsList_PreviewMouseLeftButtonUp;
            }
            _dragRow = null;
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

            UpdatePlaceholder();
        }

        /// <summary>
        /// The single placeholder rule: only an unfocused, empty box shows the
        /// hint, so the IME composition string never sits under it. The helper
        /// is pure (XamlLoadCheck pins it) and the view only maps it to a
        /// visibility.
        /// </summary>
        private void UpdatePlaceholder()
        {
            if (_detached || PlaceholderText == null || SearchBox == null) return;

            PlaceholderText.Visibility = SearchPlaceholder.ShouldShow(
                SearchBox.Text, SearchBox.IsKeyboardFocusWithin)
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

            // Loading never scans: it only populates the onboarding choices for
            // a fresh install (or hides them) and reads the in-memory status.
            RefreshOnboarding();
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

            UpdatePlaceholder();

            if (_debounce == null) return;
            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>
        /// The hint leaves the box as soon as it takes keyboard focus: an empty
        /// box under an IME composition must not draw the placeholder over the
        /// composing text.
        /// </summary>
        private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdatePlaceholder();
        }

        private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdatePlaceholder();
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
                // Spec 3.4: Enter opens the selected row; with no selection it
                // runs the query (the first Down moves focus to the list).
                if (ResultsList.SelectedItem is SearchResultRow)
                {
                    OpenSelected();
                }
                else
                {
                    if (_debounce != null) _debounce.Stop();
                    RunQuery();
                }
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
            string text = status != null && status.ScanCancelled
                ? Localization.Get("Search_ScanCancelled")
                : Localization.Get("Search_Ready");
            if (status != null && !status.IncrementalAvailable)
            {
                text += " · " + Localization.Get("Search_ReconcileOnly");
            }
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
                // A reconcile reports its own label: after a full scan it must
                // never look like the index restarted.
                StatusText.Text = _viewState == FileSearchState.Reconciling
                    ? Localization.Get("Search_Reconciling")
                    : Localization.Format("Search_Indexing", progress.Files, progress.Directories);
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

            _viewState = status.State;
            switch (status.State)
            {
                case FileSearchState.Indexing:
                    HideOnboarding();
                    EmptyState.Visibility = Visibility.Collapsed;
                    NoIndexState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = Visibility.Collapsed;
                    ScanProgress.Visibility = Visibility.Visible;
                    StatusText.Text = Localization.Format("Search_Indexing",
                        status.FilesIndexed, status.DirectoriesScanned);
                    break;

                case FileSearchState.Reconciling:
                    HideOnboarding();
                    EmptyState.Visibility = Visibility.Collapsed;
                    NoIndexState.Visibility = Visibility.Collapsed;
                    RebuildButton.Visibility = Visibility.Collapsed;
                    ScanProgress.Visibility = Visibility.Visible;
                    // Its own status: a reconcile is not the index starting over.
                    StatusText.Text = Localization.Get("Search_Reconciling");
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
                    // The state body carries the sentence; after a user cancel the
                    // transient status line reports why nothing was published.
                    StatusText.Text = status.ScanCancelled
                        ? Localization.Get("Search_ScanCancelled")
                        : string.Empty;
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

        /// <summary>
        /// The onboarding entry point. A configured install only hides the
        /// panel; a fresh one lists the drive choices. Internal (not public)
        /// because it is a test seam for XamlLoadCheck, which reaches it by
        /// reflection like the other private view pins.
        /// </summary>
        internal void RefreshOnboarding()
        {
            if (_detached) return;

            if (IsConfigured())
            {
                HideOnboarding();
                return;
            }

            ShowOnboarding();
        }

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
        /// Seeds one checked checkbox per root: the configured roots when the
        /// config carries any, otherwise every fixed NTFS drive. This only
        /// enumerates drives - it never resolves, scans or touches SQLite.
        /// </summary>
        private void PopulateRoots()
        {
            if (_detached) return;
            if (RootsList.Items.Count > 0)
            {
                // UpdateStatus may re-show the same onboarding; rebuilding the
                // boxes would throw away a selection the user just made.
                UpdateOnboardingStartState();
                return;
            }

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

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (configured != null && configured.Length > 0)
            {
                for (int i = 0; i < configured.Length; i++)
                {
                    string root = configured[i];
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    root = root.Trim();
                    if (!seen.Add(RootKey(root))) continue;
                    AddRootCheckBox(root);
                }
            }
            else
            {
                IReadOnlyList<string> fixedRoots = SearchRoots.FixedNtfsRoots();
                for (int i = 0; i < fixedRoots.Count; i++)
                {
                    if (!seen.Add(RootKey(fixedRoots[i]))) continue;
                    AddRootCheckBox(fixedRoots[i]);
                }
            }

            UpdateOnboardingStartState();
        }

        private void AddRootCheckBox(string root)
        {
            var box = new CheckBox
            {
                Content = root,
                Tag = root,
                IsChecked = true,
                Margin = new Thickness(0, 2, 0, 2)
            };
            box.Checked += RootCheckBox_Changed;
            box.Unchecked += RootCheckBox_Changed;
            RootsList.Items.Add(box);
        }

        private void RootCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateOnboardingStartState();
        }

        /// <summary>
        /// At least one drive must stay checked: an empty roots array means
        /// "every fixed NTFS drive", which would contradict an empty selection.
        /// </summary>
        private void UpdateOnboardingStartState()
        {
            if (OnboardingStartButton == null) return;

            bool any = false;
            foreach (object item in RootsList.Items)
            {
                var box = item as CheckBox;
                if (box != null && box.IsChecked == true) { any = true; break; }
            }
            OnboardingStartButton.IsEnabled = any;
        }

        private void StartIndexButton_Click(object sender, RoutedEventArgs e)
        {
            if (_service != null && _service.Status.NeedsRebuild)
            {
                HideOnboarding();
                _service.RequestRebuild();
                return;
            }

            // Configured but no index (or unconfigured): let the user pick the
            // roots again. Nothing is written until Start is pressed.
            ShowOnboarding();
        }

        private void OnboardingStartButton_Click(object sender, RoutedEventArgs e)
        {
            var roots = new List<string>();
            foreach (object item in RootsList.Items)
            {
                var box = item as CheckBox;
                if (box == null || box.IsChecked != true) continue;
                string root = box.Tag as string;
                if (!string.IsNullOrWhiteSpace(root)) roots.Add(root.Trim());
            }

            // Defensive: the button is disabled with no selection, but a
            // checkbox change racing the click must not write an empty set.
            if (roots.Count == 0) return;

            AppConfig config = WidgetManager.Instance.Config;

            // A configured install reaches this panel through the no-index
            // Start button, so keep its other choices and replace only the
            // roots; a fresh install starts from the defaults.
            FileSearchSettings settings = config.FileSearch == null
                ? FileSearchSettings.CreateDefault()
                : config.FileSearch.Clone();
            settings.Configured = true;
            settings.Enabled = true;
            // Every fixed drive checked is the default root set, stored as the
            // empty array so newly attached fixed drives are picked up too.
            settings.Roots = SearchRoots.ToConfiguredRoots(roots);
            settings.Normalize();

            config.FileSearch = settings;
            config.Save();

            StartOnboardingScan(settings, SearchRoots.Resolve(settings.Roots));

            HideOnboarding();
            UpdateStatus(_service == null ? FileSearchIndexService.Instance.Status : _service.Status);
        }

        /// <summary>
        /// Launches the onboarding scan. The check hook replaces the service
        /// path so the configured-install pin never scans the machine.
        /// </summary>
        private void StartOnboardingScan(FileSearchSettings settings, IReadOnlyList<string> roots)
        {
            Action<FileSearchSettings, IReadOnlyList<string>> hook = StartScanForChecks;
            if (hook != null)
            {
                hook(settings, roots);
                return;
            }

            if (_service == null) _service = FileSearchIndexService.Instance;
            _service.ApplySettings(settings);
            _service.StartFullScan(settings, roots);
        }

        private void OnboardingLaterButton_Click(object sender, RoutedEventArgs e)
        {
            // "Later" only leaves the panel: no config is written and no scan
            // starts. The no-index state keeps a way back in.
            _onboardingDismissed = true;
            HideOnboarding();
            EmptyState.Visibility = Visibility.Collapsed;
            NoIndexState.Visibility = Visibility.Visible;
        }

        /// <summary>Case- and trailing-separator-insensitive root comparison key.</summary>
        private static string RootKey(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return string.Empty;
            return root.Trim().TrimEnd('\\').ToLowerInvariant();
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

        /// <summary>
        /// Arms a drag for the row under the press. The event stays unhandled so
        /// the ListBox keeps its selection and the double-click open path; a
        /// press on the empty area or a scrollbar finds no container and stays a
        /// no-op.
        /// </summary>
        private void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragRow = null;
            _dragStart = e.GetPosition(ResultsList);

            var container = ItemsControl.ContainerFromElement(
                ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (container == null) return;

            var row = container.DataContext as SearchResultRow;
            if (row == null || row.Entry == null || string.IsNullOrEmpty(row.Entry.Path)) return;
            _dragRow = row;
        }

        /// <summary>
        /// Starts the drag once the pointer leaves the system drag rectangle.
        /// The payload is a plain FileDrop list: a folder widget adds it as a
        /// reference, Explorer applies its own copy/move rules.
        /// </summary>
        private void ResultsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            SearchResultRow row = _dragRow;
            if (row == null) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragRow = null;
                return;
            }

            Point position = e.GetPosition(ResultsList);
            if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            // One drag per press: a drop inside the list must not re-arm it.
            _dragRow = null;
            if (row.Entry == null || string.IsNullOrEmpty(row.Entry.Path)) return;

            try
            {
                DragDrop.DoDragDrop(ResultsList,
                    SearchDragPayload.Create(row.Entry.Path),
                    DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                StatusText.Text = Localization.Get("Search_Error") + ": " + ex.Message;
            }
        }

        private void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragRow = null;
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
