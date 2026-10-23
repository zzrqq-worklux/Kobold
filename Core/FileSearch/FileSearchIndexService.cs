using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kobold.Core;

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

        /// <summary>False until the watcher/reconcile host lands; a full scan is the only refresher in v1 so far.</summary>
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
    /// Process-wide owner of <see cref="SearchIndexStore"/> and the full-scan
    /// lifecycle. All store and directory work runs on background tasks; callers
    /// on the UI thread only change settings/state or ask for a scan. The public
    /// string constructor exists so tests can use a throwaway database; the
    /// <see cref="Instance"/> property uses <c>%LocalAppData%\Kobold\search-index.db</c>.
    ///
    /// The watcher queue and periodic reconcile belong to a later task: this class
    /// deliberately has no FileSystemWatcher timer yet, so every state transition
    /// here comes from a full scan or a widget/settings change.
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
        private int _busy;
        private long _generation;
        private bool _paused;
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
        /// </summary>
        public void ApplySettings(FileSearchSettings settings)
        {
            FileSearchSettings copy = settings == null ? null : Clone(settings);
            if (copy != null) copy.Normalize();

            if (copy == null || !copy.Configured)
            {
                CancellationTokenSource cancelled;
                lock (_gate)
                {
                    if (_disposed) return;
                    _settings = copy;
                    _generation++;
                    cancelled = _scanCts;
                }
                TryCancel(cancelled);
                Transition(FileSearchState.Unconfigured, "File search is not configured.");
                return;
            }

            lock (_gate)
            {
                if (_disposed) return;
                _settings = copy;
            }

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

        /// <summary>Stops the scan/refresh hooks and reports <see cref="FileSearchState.Paused"/>.</summary>
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
        /// Cancels the scan, waits for it up to <paramref name="timeout"/>, then
        /// checkpoints and closes the store. Disposal runs on a background task so
        /// an exit from the UI thread never opens SQLite itself.
        /// </summary>
        public void Shutdown(TimeSpan timeout)
        {
            CancellationTokenSource cancelled;
            Task scan;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _generation++;
                cancelled = _scanCts;
                scan = _scanTask;
            }

            TryCancel(cancelled);

            if (scan != null)
            {
                try { scan.Wait(timeout); }
                catch (AggregateException) { }
            }

            Task cleanup = Task.Run(() =>
            {
                _syncGate.Wait();
                try
                {
                    DisposeStoreLocked();
                }
                finally
                {
                    _syncGate.Release();
                }
            });

            try { cleanup.Wait(timeout); }
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

                try
                {
                    _store.BeginFullScan();
                    var scanner = new DirectoryScanner(_store, settings, ReportProgress);
                    ScanResult result = scanner.Scan(scanRoots, ScanMode.Full, token);
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

                    if (paused) Transition(FileSearchState.Paused, "Paused.");
                    else if (SafeHasIndexLocked())
                        Transition(FileSearchState.Ready, "Scan cancelled; the previous index is still in use.");
                    else
                        Transition(FileSearchState.NoIndex, "Scan cancelled before any index was published.");
                }
                catch (Exception ex)
                {
                    // Includes the store's IsFullScanActive guard if a caller bypassed
                    // BeginFullScan (for example by sharing the store with other work).
                    AbortScanQuietly();
                    if (!IsCurrentGeneration(generation)) return;
                    Transition(FileSearchState.Error, ex.Message);
                }
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private void StopForNoWidgets()
        {
            CancellationTokenSource cancelled;
            lock (_gate)
            {
                if (_disposed) return;
                _generation++;
                cancelled = _scanCts;
            }

            TryCancel(cancelled);
            Transition(FileSearchState.Unconfigured, "No search widget.");

            Schedule(() =>
            {
                _syncGate.Wait();
                try
                {
                    if (!_disposed) DisposeStoreLocked();
                }
                finally
                {
                    _syncGate.Release();
                }
            });
        }

        /// <summary>
        /// Must be called with the sync gate held. A corrupt/unreadable database is
        /// moved aside (<c>search-index.db.failed-yyyyMMdd-HHmmss</c>) and recreated,
        /// then reported as Error so the UI can offer a rebuild; the old rows are
        /// gone only after the move, never silently deleted in place.
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

                string quarantine = QuarantineDatabase();
                string message = quarantine == null
                    ? "The search index could not be opened: " + openError.Message
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
                IncrementalAvailable = false,
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
