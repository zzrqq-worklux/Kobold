using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kobold.Core;
using Microsoft.Data.Sqlite;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// Lifecycle of the one global file-search index. WPF and the widgets never
    /// touch SQLite or the filesystem walk directly: they call this service, which
    /// serializes store creation, full scans and shutdown on background tasks.
    /// </summary>
    public enum FileSearchState
    {
        Unconfigured,
        NoIndex,
        Ready,
        Indexing,
        Reconciling,
        Paused,
        Error
    }

    /// <summary>
    /// Immutable-ish snapshot of the service, safe to hand to a UI event handler.
    /// The service returns copies from <see cref="FileSearchIndexService.Status"/>
    /// so a reader can never race an in-flight update.
    /// </summary>
    public sealed class FileSearchStatus
    {
        public FileSearchState State { get; set; }
        public string Message { get; set; }
        public long FilesIndexed { get; set; }
        public long DirectoriesScanned { get; set; }
        public long SkippedDirectories { get; set; }

        /// <summary>True while every configured root has a live watcher; false means "reconcile only".</summary>
        public bool IncrementalAvailable { get; set; }

        public bool NeedsRebuild { get; set; }
        public string DatabasePath { get; set; }

        public FileSearchStatus()
        {
            Message = string.Empty;
            DatabasePath = string.Empty;
        }
    }

    /// <summary>
    /// Process-wide owner of <see cref="SearchIndexStore"/> and the file-search
    /// lifecycle: full scans, incremental watcher writes and the periodic
    /// reconcile. All store and directory work runs on background tasks; callers
    /// on the UI thread only change settings/state or ask for a scan. The public
    /// string constructor exists so tests can use a throwaway database; the
    /// <see cref="Instance"/> property uses <c>%LocalAppData%\Kobold\search-index.db</c>.
    ///
    /// Incremental and full work never overlap: a full scan or reconcile pauses
    /// <see cref="FileSystemWatcherHost"/> first, so no watcher row can land in a
    /// staging table or be deleted by a reconcile that never saw it. Events that
    /// arrive while paused stay queued and are applied after the table swap.
    ///
    /// Events are raised synchronously on the thread that changes state (a worker
    /// thread for scans). UI subscribers must marshal with
    /// <c>Dispatcher.BeginInvoke</c>, never <c>Invoke</c>: a blocking Invoke can
    /// deadlock against <see cref="Shutdown"/> or a scan holding the sync gate.
    /// </summary>
    public sealed class FileSearchIndexService : IDisposable
    {
        private static readonly Lazy<FileSearchIndexService> DefaultInstance =
            new Lazy<FileSearchIndexService>(CreateInstance, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

        private readonly object _gate = new object();
        private readonly SemaphoreSlim _syncGate = new SemaphoreSlim(1, 1);
        private readonly ManualResetEventSlim _idleEvent = new ManualResetEventSlim(true);
        private readonly string _databasePath;
        private readonly FileSearchStatus _status;

        private ISearchIndexStore _store;
        private FileSearchSettings _settings;
        private CancellationTokenSource _scanCts;
        private Task _scanTask;
        private FileSystemWatcherHost _watcherHost;
        private Timer _reconcileTimer;
        private string _incrementalFingerprint;
        private IReadOnlyList<string> _indexRoots;
        private int _reconcileMinutes = -1;
        private int _busy;
        private long _generation;
        private bool _searchWanted;
        private bool _paused;
        private bool _reconcilePending;
        private bool _reconcileWanted;
        private bool _disposed;

        /// <summary>The single production service. It never loads config: WidgetManager calls ApplySettings.</summary>
        public static FileSearchIndexService Instance
        {
            get { return DefaultInstance.Value; }
        }

        /// <summary>Test/standalone entry point; the default directory is only created for <see cref="Instance"/>.</summary>
        public FileSearchIndexService(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("A database path is required.", "databasePath");

            _databasePath = Path.GetFullPath(databasePath);
            _status = new FileSearchStatus
            {
                State = FileSearchState.Unconfigured,
                Message = "File search is not configured.",
                DatabasePath = _databasePath
            };
        }

        public FileSearchStatus Status
        {
            get
            {
                lock (_gate) return SnapshotLocked();
            }
        }

        public event EventHandler<FileSearchStatus> StatusChanged;
        public event EventHandler<ScanProgress> ProgressChanged;

        /// <summary>
        /// Stores normalized settings only; no SQLite or root resolution happens on
        /// the caller's thread. A fingerprint mismatch against the published index
        /// flags <see cref="FileSearchStatus.NeedsRebuild"/> without deleting rows.
        /// The periodic reconcile interval is re-armed/stopped immediately: it is
        /// cheap and must take effect without waiting for the next scan.
        /// </summary>
        public void ApplySettings(FileSearchSettings settings)
        {
            FileSearchSettings copy = settings == null ? null : Clone(settings);
            if (copy != null) copy.Normalize();

            if (copy == null || !copy.Configured)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _settings = copy;
                }
                Deactivate("File search is not configured.");
                return;
            }

            lock (_gate)
            {
                if (_disposed) return;
                _settings = copy;
            }

            RefreshReconcileTimer(copy);

            // Compare against an already-open store in the background. If no store
            // exists yet, the startup path performs the same check when it opens one.
            Schedule(() =>
            {
                if (!IsCurrentSettings(copy)) return;

                _syncGate.Wait();
                try
                {
                    // Settings may have changed again while this task waited for the
                    // worker; the newer ApplySettings owns the flag in that case.
                    if (_disposed || _store == null || !IsCurrentSettings(copy)) return;
                    IndexStatus index = _store.ReadStatus();
                    UpdateNeedsRebuild(FingerprintDiffers(index, copy));
                }
                finally
                {
                    _syncGate.Release();
                }
            });
        }

        /// <summary>
        /// Effective periodic reconcile interval in minutes; 0 when no timer is
        /// scheduled. Reflects <see cref="ApplySettings"/> changes immediately and
        /// gives the settings UI (and tests) a way to observe the timer state.
        /// </summary>
        public int ReconcileIntervalMinutes
        {
            get
            {
                lock (_gate) return _reconcileMinutes < 0 ? 0 : _reconcileMinutes;
            }
        }

        /// <summary>
        /// Search is wanted while at least one search widget exists. Removing the
        /// last one closes the store (keeping the database); adding one again
        /// reopens the existing index instead of rescanning it.
        /// </summary>
        public void SyncWithWidgets(IReadOnlyList<WidgetData> widgets)
        {
            bool anySearch = false;
            if (widgets != null)
            {
                for (int i = 0; i < widgets.Count; i++)
                {
                    if (widgets[i] is SearchData) { anySearch = true; break; }
                }
            }

            if (!anySearch)
            {
                StopForNoWidgets();
                return;
            }

            bool paused;
            FileSearchSettings settings;
            lock (_gate)
            {
                if (_disposed) return;
                _searchWanted = true;
                paused = _paused;
                settings = _settings;
            }

            if (paused)
            {
                Transition(FileSearchState.Paused, "Paused.");
                return;
            }

            if (settings == null || !settings.Configured || !settings.Enabled)
            {
                // Same store-open semantic as ApplySettings: unconfigured keeps an
                // already-open store so onboarding can start a scan without reopening
                // it; only "no search widget" closes it.
                Deactivate("File search is not configured.");
                return;
            }

            Schedule(RunStartCore);
        }

        /// <summary>
        /// Starts a full scan on a background task. <paramref name="roots"/> may be
        /// null/empty to use the settings' resolved roots. Rows are staged by the
        /// store, so a cancel or failure leaves the published index untouched.
        /// </summary>
        public void StartFullScan(FileSearchSettings settings, IReadOnlyList<string> roots)
        {
            if (settings == null || !settings.Configured)
            {
                Transition(FileSearchState.Unconfigured, "File search is not configured.");
                return;
            }

            FileSearchSettings copy = Clone(settings);
            copy.Normalize();

            CancellationTokenSource previous;
            CancellationTokenSource current = new CancellationTokenSource();
            long generation;
            EventHandler<FileSearchStatus> handler;
            FileSearchStatus snapshot;

            lock (_gate)
            {
                if (_disposed)
                {
                    current.Dispose();
                    return;
                }

                _settings = copy;
                // An explicit scan is a user decision: leave the paused state instead
                // of starting behind a stale pause flag.
                _paused = false;
                previous = _scanCts;
                _scanCts = current;
                _generation++;
                generation = _generation;

                _status.State = FileSearchState.Indexing;
                _status.Message = "Indexing files...";
                _status.FilesIndexed = 0;
                _status.DirectoriesScanned = 0;
                _status.SkippedDirectories = 0;

                snapshot = SnapshotLocked();
                handler = StatusChanged;
            }

            TryCancel(previous);
            if (handler != null)
            {
                try { handler(this, snapshot); }
                catch (Exception) { }
            }

            Task task = Schedule(() =>
            {
                try { RunFullScan(copy, roots, generation, current.Token); }
                finally { current.Dispose(); }
            });

            lock (_gate) { _scanTask = task; }
        }

        /// <summary>Cancels the current scan but does not wait for it; the aborted scan keeps the old index.</summary>
        public void CancelScan()
        {
            CancellationTokenSource cancelled;
            lock (_gate) cancelled = _scanCts;
            TryCancel(cancelled);
        }

        /// <summary>Stops the watcher, the reconcile timer and any running scan; reports <see cref="FileSearchState.Paused"/>.</summary>
        public void Pause()
        {
            CancellationTokenSource cancelled;
            lock (_gate)
            {
                if (_disposed) return;
                _paused = true;
                cancelled = _scanCts;
            }
            TryCancel(cancelled);
            StopIncrementalQuiet();
            Transition(FileSearchState.Paused, "Paused.");
        }

        /// <summary>Clears the pause flag and reports Ready/NoIndex from the index that is already on disk.</summary>
        public void Resume()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _paused = false;
            }
            Schedule(RunStartCore);
        }

        /// <summary>Starts a full scan with the current settings and resolved roots.</summary>
        public void RequestRebuild()
        {
            FileSearchSettings settings;
            lock (_gate) settings = _settings;

            if (settings == null || !settings.Configured)
            {
                Transition(FileSearchState.Unconfigured, "File search is not configured.");
                return;
            }

            StartFullScan(settings, null);
        }

        /// <summary>
        /// Requests one background reconcile. The periodic timer, a watcher
        /// overflow/error and the settings UI all use this path; it is ignored
        /// unless the service is Ready and never runs on the caller's thread.
        /// </summary>
        public void RequestReconcile()
        {
            ScheduleReconcile();
        }

        /// <summary>
        /// Delegates to the store. Returns an empty result before the store was
        /// opened (no search widget yet) or while a scan is still being set up.
        /// A query runs on its own read connection, so it never waits for a scan.
        /// </summary>
        public SearchQueryResult Search(string text, int limit, CancellationToken token)
        {
            ISearchIndexStore store = _store;
            if (store == null) return SearchQueryResult.Empty;

            try
            {
                return store.Query(text, limit, token);
            }
            catch (ObjectDisposedException)
            {
                return SearchQueryResult.Empty;
            }
        }

        /// <summary>Delegates to the store; an unopened store reports no index.</summary>
        public IndexStatus ReadStatus()
        {
            ISearchIndexStore store = _store;
            if (store == null) return new IndexStatus();
            return store.ReadStatus();
        }

        /// <summary>
        /// Test hook (production never calls it): waits until startup, a scan and
        /// any queued settings work have all finished. Returns on timeout.
        /// </summary>
        public void WaitForIdle(TimeSpan timeout)
        {
            _idleEvent.Wait(timeout);
        }

        /// <summary>
        /// Cancels the scan, waits at most <paramref name="timeout"/> in total, then
        /// checkpoints and closes the store. Checkpoint/dispose run on a background
        /// task so an exit from the UI thread never opens SQLite itself. The single
        /// wait covers both the cancelled scan and the cleanup, so exit latency is
        /// bounded once, not once per phase.
        /// </summary>
        public void Shutdown(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;

            CancellationTokenSource cancelled;
            Task scan;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _generation++;
                _searchWanted = false;
                cancelled = _scanCts;
                scan = _scanTask;
            }

            TryCancel(cancelled);
            var clock = Stopwatch.StartNew();

            // The watcher and its timer must be gone before the store is closed or
            // a queued incremental write would race the disposal. The wait counts
            // against the one shutdown timeout.
            StopIncrementalQuiet();

            Task cleanup = Task.Run(() =>
            {
                // The cancelled scan exits quickly; waiting here (rather than on the
                // UI thread before scheduling cleanup) keeps one bounded wait below.
                if (scan != null)
                {
                    try { scan.Wait(); }
                    catch (AggregateException) { }
                }

                _syncGate.Wait();
                try
                {
                    if (_store != null)
                    {
                        try { _store.Checkpoint(); }
                        catch (Exception) { }
                        DisposeStoreLocked();
                    }
                }
                finally
                {
                    _syncGate.Release();
                }
            });

            TimeSpan remaining = timeout - clock.Elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            try { cleanup.Wait(remaining); }
            catch (AggregateException) { }

            lock (_gate) _idleEvent.Set();
        }

        public void Dispose()
        {
            Shutdown(DefaultShutdownTimeout);
        }

        private static FileSearchIndexService CreateInstance()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kobold");
            try { Directory.CreateDirectory(directory); }
            catch (Exception) { }

            return new FileSearchIndexService(Path.Combine(directory, "search-index.db"));
        }

        private void RunStartCore()
        {
            _syncGate.Wait();
            try { StartCoreLocked(); }
            finally { _syncGate.Release(); }
        }

        /// <summary>Opening/reading the store must be on the worker, never the UI thread.</summary>
        private void StartCoreLocked()
        {
            if (_disposed) return;

            bool paused;
            FileSearchSettings settings;
            lock (_gate)
            {
                paused = _paused;
                settings = _settings;
            }

            if (paused)
            {
                Transition(FileSearchState.Paused, "Paused.");
                return;
            }

            if (settings == null || !settings.Configured || !settings.Enabled)
            {
                Transition(FileSearchState.Unconfigured, "File search is not configured.");
                return;
            }

            if (!EnsureStoreLocked()) return;

            IndexStatus index;
            try
            {
                index = _store.ReadStatus();
            }
            catch (Exception ex)
            {
                Transition(FileSearchState.Error, ex.Message);
                return;
            }

            if (!index.HasIndex)
            {
                Transition(FileSearchState.NoIndex, "No index yet.");
                return;
            }

            bool needsRebuild = FingerprintDiffers(index, settings);

            // Ready is the first moment the index can absorb live rows: start the
            // watchers and the periodic reconcile before publishing the state so
            // the Ready snapshot already carries IncrementalAvailable.
            IReadOnlyList<string> roots = SearchRoots.Resolve(settings.Roots);
            StartIncrementalLocked(roots, settings);

            Transition(FileSearchState.Ready, "Ready.", status =>
            {
                status.NeedsRebuild = needsRebuild;
                status.SkippedDirectories = index.SkippedDirectories;
            });
        }

        /// <summary>
        /// Runs on the serialized worker. CompleteFullScan is only called after a
        /// successful walk; every other exit aborts the staged rows so the
        /// previously published table keeps serving queries.
        /// </summary>
        private void RunFullScan(FileSearchSettings settings, IReadOnlyList<string> roots, long generation,
            CancellationToken token)
        {
            _syncGate.Wait();
            try
            {
                if (_disposed || !IsCurrentGeneration(generation)) return;

                if (!EnsureStoreLocked()) return;

                IReadOnlyList<string> scanRoots = roots != null && roots.Count > 0
                    ? roots
                    : SearchRoots.Resolve(settings.Roots);
                string fingerprint = settings.ComputeFingerprint(scanRoots);

                // Freeze the watcher before the staging table exists. Events that
                // arrive during the walk stay queued and are applied to the new
                // table only after the swap (or to the old one after an abort).
                FileSystemWatcherHost host;
                lock (_gate) host = _watcherHost;
                if (host != null) host.PauseProcessing();

                try
                {
                    _store.BeginFullScan();
                    var scanner = new DirectoryScanner(_store, settings, ReportProgress);
                    ScanResult result = scanner.Scan(scanRoots, ScanMode.Full, token);

                    // The scanner reports its final progress snapshot before it
                    // returns; a subscriber may cancel from that callback. Honour
                    // the token before publishing the staged rows.
                    token.ThrowIfCancellationRequested();
                    _store.CompleteFullScan(result.SkippedDirectories, result.SkippedReparsePoints,
                        result.SkippedLongPaths, fingerprint);

                    // One maintenance pass per full build; a cancelled/reconcile scan never runs these.
                    try { _store.Checkpoint(); }
                    catch (Exception) { }
                    try { _store.Vacuum(); }
                    catch (Exception) { }

                    if (!IsCurrentGeneration(generation)) return;
                    // A newer ApplySettings may have replaced the settings while this
                    // scan ran; its own comparison decides NeedsRebuild.
                    bool settingsStillCurrent = IsCurrentSettings(settings);
                    StartIncrementalLocked(scanRoots, settings);
                    Transition(FileSearchState.Ready, "Ready.", status =>
                    {
                        status.FilesIndexed = result.Files;
                        status.DirectoriesScanned = result.Directories;
                        status.SkippedDirectories = result.SkippedDirectories;
                        if (settingsStillCurrent) status.NeedsRebuild = false;
                    });
                }
                catch (OperationCanceledException)
                {
                    AbortScanQuietly();
                    if (!IsCurrentGeneration(generation)) return;

                    bool paused;
                    lock (_gate) paused = _paused;

                    // The aborted scan must not leave partial counters in the status.
                    if (paused)
                    {
                        Transition(FileSearchState.Paused, "Paused.", ResetScanCounters);
                    }
                    else if (SafeHasIndexLocked())
                    {
                        // The old index is still live: resume incremental writes on it
                        // instead of leaving the service Ready without a watcher until
                        // the next launch.
                        StartIncrementalLocked(scanRoots, settings);
                        Transition(FileSearchState.Ready,
                            "Scan cancelled; the previous index is still in use.", ResetScanCounters);
                    }
                    else
                    {
                        Transition(FileSearchState.NoIndex,
                            "Scan cancelled before any index was published.", ResetScanCounters);
                    }
                }
                catch (Exception ex)
                {
                    // Includes the store's IsFullScanActive guard if a caller bypassed
                    // BeginFullScan (for example by sharing the store with other work).
                    AbortScanQuietly();
                    if (!IsCurrentGeneration(generation)) return;
                    Transition(FileSearchState.Error, ex.Message);
                }
                finally
                {
                    if (host != null) host.ResumeProcessing();
                }
            }
            finally
            {
                _syncGate.Release();
            }
        }

        /// <summary>
        /// Queues one reconcile unless one is already in flight. A request that
        /// arrives while the service is busy (scanning/reconciling) or paused is
        /// recorded in <c>_reconcileWanted</c> and replayed when the service is
        /// Ready again: an overflow during a full scan must not be lost.
        /// </summary>
        private void ScheduleReconcile()
        {
            bool start = false;
            lock (_gate)
            {
                if (_disposed) return;

                if (_paused || _status.State != FileSearchState.Ready || _store == null
                    || _reconcilePending)
                {
                    _reconcileWanted = true;
                    return;
                }

                _reconcileWanted = false;
                _reconcilePending = true;
                start = true;
            }

            if (start) QueueReconcile();
        }

        private void QueueReconcile()
        {
            Schedule(() =>
            {
                try { RunReconcile(); }
                finally
                {
                    // A request that arrived while this pass was running gets its
                    // own pass instead of being folded into the one that missed it.
                    bool again;
                    lock (_gate)
                    {
                        _reconcilePending = false;
                        again = _reconcileWanted && !_disposed && !_paused
                            && _status.State == FileSearchState.Ready && _store != null;
                        if (again)
                        {
                            _reconcileWanted = false;
                            _reconcilePending = true;
                        }
                    }
                    if (again) QueueReconcile();
                }
            });
        }

        /// <summary>Replays a reconcile that had to be deferred while the service was busy or paused.</summary>
        private void FlushWantedReconcile()
        {
            bool start = false;
            lock (_gate)
            {
                if (_disposed || _paused || !_reconcileWanted) return;
                if (_status.State != FileSearchState.Ready || _store == null || _reconcilePending) return;

                _reconcileWanted = false;
                _reconcilePending = true;
                start = true;
            }

            if (start) QueueReconcile();
        }

        /// <summary>
        /// One reconcile pass: live rows are upserted and marked seen, then
        /// CompleteReconcile drops the rows of this root that were not seen. The
        /// watcher is paused for the whole pass so its writes cannot be mistaken
        /// for stale rows, and resumed after the dead-row cleanup commits.
        /// </summary>
        private void RunReconcile()
        {
            _syncGate.Wait();
            try
            {
                if (_disposed) return;

                FileSearchSettings settings;
                IReadOnlyList<string> roots;
                ISearchIndexStore store;
                FileSystemWatcherHost host;
                var current = new CancellationTokenSource();
                CancellationTokenSource previous;

                lock (_gate)
                {
                    if (_disposed || _paused || _status.State != FileSearchState.Ready || _store == null)
                    {
                        current.Dispose();
                        return;
                    }

                    settings = _settings;
                    store = _store;
                    host = _watcherHost;
                    previous = _scanCts;
                    roots = _indexRoots;
                    _scanCts = current;
                }

                IReadOnlyList<string> normalized = NormalizeRoots(roots);
                if (settings == null || normalized.Count == 0)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_scanCts, current)) _scanCts = null;
                    }
                    current.Dispose();
                    return;
                }

                TryCancel(previous);
                Transition(FileSearchState.Reconciling, "Reconciling files...");

                bool hostPaused = false;
                try
                {
                    if (host != null)
                    {
                        host.PauseProcessing();
                        hostPaused = true;
                    }

                    store.BeginReconcile();
                    var scanner = new DirectoryScanner(store, settings);
                    scanner.Scan(normalized, ScanMode.Reconcile, current.Token);
                    current.Token.ThrowIfCancellationRequested();
                    store.CompleteReconcile(normalized);

                    if (IsCurrentReconcile(current))
                    {
                        Transition(FileSearchState.Ready, "Ready.");
                    }
                }
                catch (OperationCanceledException)
                {
                    AbortReconcileQuietly();
                    if (IsCurrentReconcile(current))
                    {
                        bool paused;
                        lock (_gate) paused = _paused;
                        if (paused)
                        {
                            Transition(FileSearchState.Paused, "Paused.");
                        }
                        else
                        {
                            Transition(FileSearchState.Ready,
                                "Reconcile cancelled; the index is unchanged.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AbortReconcileQuietly();
                    if (IsCurrentReconcile(current)) Transition(FileSearchState.Error, ex.Message);
                }
                finally
                {
                    // Resume only after CompleteReconcile/AbortReconcile returned, so
                    // the worker's queued events drain against the final table.
                    if (hostPaused && host != null) host.ResumeProcessing();

                    lock (_gate)
                    {
                        if (ReferenceEquals(_scanCts, current)) _scanCts = null;
                    }
                    current.Dispose();
                }
            }
            finally
            {
                _syncGate.Release();
            }
        }

        /// <summary>
        /// Starts (or reuses) the watcher host and the periodic reconcile timer.
        /// Must run under the sync gate on a background thread: it may create
        /// watchers and join their worker. Returns true only when every resolved
        /// root is watched; a partial/failed set keeps the healthy watchers and
        /// leaves the status degraded, with the timer as the repair path.
        /// </summary>
        private bool StartIncrementalLocked(IReadOnlyList<string> roots, FileSearchSettings settings)
        {
            if (_disposed || _store == null || settings == null) return false;

            IReadOnlyList<string> normalized = NormalizeRoots(roots);
            string fingerprint = settings.ComputeFingerprint(normalized);

            FileSystemWatcherHost existing;
            string existingFingerprint;
            lock (_gate)
            {
                if (_disposed || _paused) return false;
                existing = _watcherHost;
                existingFingerprint = _incrementalFingerprint;
                _indexRoots = normalized;
            }

            bool available;
            if (existing != null && string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
            {
                // Same roots and same content rules: keep the live watchers (and any
                // events they queued while the scan was paused).
                available = existing.IncrementalAvailable;
            }
            else
            {
                StopIncrementalQuiet();

                FileSystemWatcherHost host = null;
                available = false;
                try
                {
                    host = new FileSystemWatcherHost(_store, settings, null);
                    host.ReconcileRequested += OnWatcherReconcileRequested;
                    bool started = host.Start(normalized);
                    available = started;

                    if (host.WatchedRoots > 0)
                    {
                        lock (_gate)
                        {
                            // A pause/stop may have raced the start; never leave a
                            // watcher running behind an inactive service.
                            if (!_disposed && !_paused)
                            {
                                _watcherHost = host;
                                _incrementalFingerprint = fingerprint;
                                host = null;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    available = false;
                }

                if (host != null)
                {
                    // Nothing watches (or a stop raced): do not leak the worker thread.
                    available = false;
                    host.ReconcileRequested -= OnWatcherReconcileRequested;
                    try { host.Stop(); }
                    catch (Exception) { }
                    try { host.Dispose(); }
                    catch (Exception) { }
                }
            }

            EnsureReconcileTimerLocked(CurrentSettingsOr(settings));
            return available;
        }

        /// <summary>
        /// Re-arms the timer for a settings change made while Ready, or stops it
        /// when the service is paused/not ready or the interval is 0. Runs on the
        /// caller's thread: creating/disposing a timer is cheap and touches no
        /// SQLite or filesystem state.
        /// </summary>
        private void RefreshReconcileTimer(FileSearchSettings settings)
        {
            bool ready;
            lock (_gate)
            {
                ready = !_disposed && !_paused && _status.State == FileSearchState.Ready
                    && _store != null && settings != null && settings.Enabled;
            }

            if (ready) EnsureReconcileTimerLocked(settings);
            else StopReconcileTimerQuiet();
        }

        private FileSearchSettings CurrentSettingsOr(FileSearchSettings fallback)
        {
            lock (_gate) return _settings ?? fallback;
        }

        private void StopReconcileTimerQuiet()
        {
            Timer timer;
            lock (_gate)
            {
                timer = _reconcileTimer;
                _reconcileTimer = null;
                _reconcileMinutes = -1;
            }

            if (timer != null)
            {
                try { timer.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Stops the watcher and the timer. Called for pause, deactivate, no-widget
        /// and shutdown; safe to call when nothing is running.
        /// </summary>
        private void StopIncrementalQuiet()
        {
            FileSystemWatcherHost host;
            Timer timer;

            lock (_gate)
            {
                host = _watcherHost;
                _watcherHost = null;
                _incrementalFingerprint = null;
                timer = _reconcileTimer;
                _reconcileTimer = null;
                _reconcileMinutes = -1;
            }

            if (timer != null)
            {
                try { timer.Dispose(); }
                catch (Exception) { }
            }

            if (host != null)
            {
                try { host.Stop(); }
                catch (Exception) { }
                try { host.Dispose(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Creates the one periodic timer when the interval changes. The first tick
        /// is 30 seconds after Ready (never immediately: a launch should settle
        /// before a background walk); the timer callback only schedules.
        /// </summary>
        private void EnsureReconcileTimerLocked(FileSearchSettings settings)
        {
            int minutes = settings == null ? 0 : settings.ReconcileMinutes;
            Timer previous = null;

            lock (_gate)
            {
                if (_disposed) minutes = 0;
                if (_reconcileTimer != null && _reconcileMinutes == minutes) return;

                previous = _reconcileTimer;
                _reconcileTimer = null;
                _reconcileMinutes = minutes;

                if (minutes > 0)
                {
                    try
                    {
                        _reconcileTimer = new Timer(OnReconcileTimer, null,
                            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(minutes));
                    }
                    catch (Exception)
                    {
                        _reconcileTimer = null;
                    }
                }
            }

            if (previous != null)
            {
                try { previous.Dispose(); }
                catch (Exception) { }
            }
        }

        private void OnReconcileTimer(object state)
        {
            ScheduleReconcile();
        }

        private void OnWatcherReconcileRequested(object sender, EventArgs e)
        {
            // Overflow/OS error handlers run on a watcher or worker thread; they
            // may only ask for a reconcile, never run one inline.
            ScheduleReconcile();
        }

        private void AbortReconcileQuietly()
        {
            try
            {
                if (_store != null) _store.AbortReconcile();
            }
            catch (Exception)
            {
            }
        }

        private bool IsCurrentReconcile(CancellationTokenSource source)
        {
            lock (_gate) return !_disposed && ReferenceEquals(_scanCts, source);
        }

        private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string> roots)
        {
            var normalized = new List<string>();
            if (roots == null) return normalized;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < roots.Count; i++)
            {
                string root = SearchPath.Normalize(roots[i]);
                if (root.Length == 0 || !seen.Add(root)) continue;
                normalized.Add(root);
            }
            return normalized;
        }

        private void StopForNoWidgets()
        {
            CancellationTokenSource cancelled;
            long stopGeneration;
            lock (_gate)
            {
                if (_disposed) return;
                _searchWanted = false;
                _generation++;
                _reconcileWanted = false;
                stopGeneration = _generation;
                cancelled = _scanCts;
            }

            TryCancel(cancelled);
            StopIncrementalQuiet();
            Transition(FileSearchState.Unconfigured, "No search widget.");

            Schedule(() =>
            {
                _syncGate.Wait();
                try
                {
                    // A widget re-added before this task runs wants the store again.
                    // The quick remove/re-add must not end Ready with a closed store.
                    bool dispose;
                    lock (_gate)
                    {
                        dispose = !_disposed && !_searchWanted && _generation == stopGeneration;
                    }
                    if (dispose)
                    {
                        StopIncrementalQuiet();
                        DisposeStoreLocked();
                    }
                }
                finally
                {
                    _syncGate.Release();
                }
            });
        }

        /// <summary>
        /// Shared "not configured" path for null settings, Configured=false and
        /// Enabled=false: stop a running scan but keep an already-open store (only
        /// removing the last search widget closes it).
        /// </summary>
        private void Deactivate(string message)
        {
            CancellationTokenSource cancelled;
            lock (_gate)
            {
                if (_disposed) return;
                _generation++;
                _reconcileWanted = false;
                cancelled = _scanCts;
            }

            TryCancel(cancelled);
            StopIncrementalQuiet();
            Transition(FileSearchState.Unconfigured, message);
        }

        /// <summary>
        /// Must be called with the sync gate held. Only a genuine corruption
        /// (SQLITE_CORRUPT/SQLITE_NOTADB) moves the database aside
        /// (<c>search-index.db.failed-yyyyMMdd-HHmmss</c>) and recreates it, then
        /// reports Error so the UI can offer a rebuild. Busy/locked opens are
        /// retried once; any other failure leaves the healthy file untouched and
        /// reports Error.
        /// </summary>
        private bool EnsureStoreLocked()
        {
            if (_store != null) return true;

            var store = new SearchIndexStore(_databasePath);
            try
            {
                store.EnsureSchema();
                _store = store;
                return true;
            }
            catch (Exception openError)
            {
                try { store.Dispose(); }
                catch (Exception) { }

                var sqliteError = openError as SqliteException;
                if (!IsCorrupt(sqliteError))
                {
                    // A healthy database must never be moved for a transient lock,
                    // a full disk or an access-denied folder. Retry the common
                    // busy/locked case once; otherwise report the error untouched.
                    if (IsBusy(sqliteError))
                    {
                        Thread.Sleep(100);
                        var retry = new SearchIndexStore(_databasePath);
                        try
                        {
                            retry.EnsureSchema();
                            _store = retry;
                            return true;
                        }
                        catch (Exception retryError)
                        {
                            try { retry.Dispose(); }
                            catch (Exception) { }
                            Transition(FileSearchState.Error,
                                "The search index is busy: " + retryError.Message);
                            return false;
                        }
                    }

                    Transition(FileSearchState.Error,
                        "The search index could not be opened: " + openError.Message);
                    return false;
                }

                // Genuine corruption (SQLITE_CORRUPT / SQLITE_NOTADB): keep the
                // evidence, recreate an empty database and let the UI offer rebuild.
                string quarantine = QuarantineDatabase();
                string message = quarantine == null
                    ? "The search index was corrupt and could not be moved aside: " + openError.Message
                    : "The search index was corrupt and was reset: " + openError.Message;

                var rebuilt = new SearchIndexStore(_databasePath);
                try
                {
                    rebuilt.EnsureSchema();
                    _store = rebuilt;
                }
                catch (Exception retryError)
                {
                    try { rebuilt.Dispose(); }
                    catch (Exception) { }
                    Transition(FileSearchState.Error,
                        "The search index could not be created: " + retryError.Message);
                    return false;
                }

                Transition(FileSearchState.Error, message);
                return false;
            }
        }

        /// <summary>SQLITE_CORRUPT (11) or SQLITE_NOTADB (26); those are the only codes that justify moving the file.</summary>
        private static bool IsCorrupt(SqliteException error)
        {
            return error != null
                && (error.SqliteErrorCode == 11 || error.SqliteErrorCode == 26);
        }

        /// <summary>SQLITE_BUSY (5) or SQLITE_LOCKED (6) are worth one retry; the database is healthy.</summary>
        private static bool IsBusy(SqliteException error)
        {
            return error != null
                && (error.SqliteErrorCode == 5 || error.SqliteErrorCode == 6);
        }

        private string QuarantineDatabase()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = _databasePath + ".failed-" + stamp;
            int suffix = 1;
            while (File.Exists(target) || File.Exists(target + "-wal") || File.Exists(target + "-shm"))
            {
                target = _databasePath + ".failed-" + stamp + "-"
                    + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            try
            {
                bool moved = MoveIfExists(_databasePath, target);
                MoveIfExists(_databasePath + "-wal", target + "-wal");
                MoveIfExists(_databasePath + "-shm", target + "-shm");
                return moved ? target : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool MoveIfExists(string from, string to)
        {
            if (!File.Exists(from)) return false;
            File.Move(from, to);
            return true;
        }

        private void DisposeStoreLocked()
        {
            ISearchIndexStore store = _store;
            _store = null;
            if (store == null) return;

            try { store.Dispose(); }
            catch (Exception) { }
        }

        private void AbortScanQuietly()
        {
            try
            {
                if (_store != null && _store.IsFullScanActive) _store.AbortFullScan();
            }
            catch (Exception) { }
        }

        private bool SafeHasIndexLocked()
        {
            try
            {
                return _store != null && _store.ReadStatus().HasIndex;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReportProgress(ScanProgress progress)
        {
            if (progress == null) return;

            EventHandler<ScanProgress> handler;
            lock (_gate)
            {
                if (_disposed) return;
                _status.FilesIndexed = progress.Files;
                _status.DirectoriesScanned = progress.Directories;
                _status.SkippedDirectories = progress.SkippedDirectories;
                handler = ProgressChanged;
            }

            if (handler != null)
            {
                try { handler(this, progress); }
                catch (Exception) { }
            }
        }

        private static bool FingerprintDiffers(IndexStatus index, FileSearchSettings settings)
        {
            if (index == null || !index.HasIndex) return false;

            string stored = index.SettingsFingerprint ?? string.Empty;
            string current = settings.ComputeFingerprint(SearchRoots.Resolve(settings.Roots));
            return !string.Equals(stored, current, StringComparison.Ordinal);
        }

        private void UpdateNeedsRebuild(bool value)
        {
            EventHandler<FileSearchStatus> handler;
            FileSearchStatus snapshot;
            lock (_gate)
            {
                if (_disposed || _status.NeedsRebuild == value) return;
                _status.NeedsRebuild = value;
                snapshot = SnapshotLocked();
                handler = StatusChanged;
            }

            if (handler != null)
            {
                try { handler(this, snapshot); }
                catch (Exception) { }
            }
        }

        /// <summary>An aborted scan has no published rows, so its partial counters must not leak into the status.</summary>
        private static void ResetScanCounters(FileSearchStatus status)
        {
            status.FilesIndexed = 0;
            status.DirectoriesScanned = 0;
            status.SkippedDirectories = 0;
        }

        private void Transition(FileSearchState state, string message, Action<FileSearchStatus> update = null)
        {
            EventHandler<FileSearchStatus> handler;
            FileSearchStatus snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                _status.State = state;
                _status.Message = message ?? string.Empty;
                if (update != null) update(_status);
                snapshot = SnapshotLocked();
                handler = StatusChanged;
            }

            if (handler != null)
            {
                try { handler(this, snapshot); }
                catch (Exception) { }
            }

            // Ready is where a deferred request (overflow during a scan, a request
            // made while paused) becomes runnable again, and where a timer that was
            // stopped by an ApplySettings during a scan is re-armed. An existing
            // timer with the same interval is left untouched, so a normal reconcile
            // does not restart the 30s first-tick clock.
            if (state == FileSearchState.Ready)
            {
                FileSearchSettings current;
                lock (_gate) current = _settings;
                if (current != null) EnsureReconcileTimerLocked(current);
                FlushWantedReconcile();
            }
        }

        private FileSearchStatus SnapshotLocked()
        {
            return new FileSearchStatus
            {
                State = _status.State,
                Message = _status.Message,
                FilesIndexed = _status.FilesIndexed,
                DirectoriesScanned = _status.DirectoriesScanned,
                SkippedDirectories = _status.SkippedDirectories,
                // Read live: a watcher that reported an OS error during the session
                // degrades without a state transition to carry the change.
                IncrementalAvailable = _watcherHost != null && _watcherHost.IncrementalAvailable,
                NeedsRebuild = _status.NeedsRebuild,
                DatabasePath = _databasePath
            };
        }

        private bool IsCurrentGeneration(long generation)
        {
            lock (_gate) return !_disposed && _generation == generation;
        }

        /// <summary>The service stores a private clone per ApplySettings, so identity is the version.</summary>
        private bool IsCurrentSettings(FileSearchSettings settings)
        {
            lock (_gate) return !_disposed && ReferenceEquals(_settings, settings);
        }

        /// <summary>
        /// Queues work on the thread pool and tracks it for WaitForIdle. The busy
        /// count is changed under the same lock as the idle event, so a task
        /// scheduled while the previous one is finishing cannot lose the reset.
        /// </summary>
        private Task Schedule(Action work)
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(0);
                _busy++;
                _idleEvent.Reset();
            }

            return Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Transition(FileSearchState.Error, ex.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        _busy--;
                        if (_busy <= 0)
                        {
                            _busy = 0;
                            _idleEvent.Set();
                        }
                    }
                }
            });
        }

        private static void TryCancel(CancellationTokenSource source)
        {
            if (source == null) return;
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }

        private static FileSearchSettings Clone(FileSearchSettings source)
        {
            return new FileSearchSettings
            {
                Enabled = source.Enabled,
                Configured = source.Configured,
                Roots = Copy(source.Roots),
                IndexAllFiles = source.IndexAllFiles,
                Types = Copy(source.Types),
                IndexFolders = source.IndexFolders,
                IndexExtensionless = source.IndexExtensionless,
                ExcludeDirectories = Copy(source.ExcludeDirectories),
                ReconcileMinutes = source.ReconcileMinutes
            };
        }

        private static string[] Copy(string[] values)
        {
            return values == null ? new string[0] : (string[])values.Clone();
        }
    }
}
