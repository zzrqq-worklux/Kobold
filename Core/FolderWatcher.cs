using System;
using System.IO;
using System.Threading;

namespace Kobold.Core
{
    /// <summary>
    /// Follows one directory and reports changes through a debounced Changed
    /// event. Core-only (no UI, no WPF): the owner marshals to its own thread and
    /// decides what a change means. Some network shares refuse a watcher - Start
    /// reports that instead of throwing, so the caller can keep its manual
    /// refresh.
    /// </summary>
    public sealed class FolderWatcher : IDisposable
    {
        private readonly object _gate = new object();
        private readonly int _debounceMs;
        private readonly Timer _debounce;

        private FileSystemWatcher _watcher;
        private string _path;
        private bool _disposed;

        public FolderWatcher(int debounceMs = WidgetConstants.BROWSE_WATCH_DEBOUNCE_MS)
        {
            _debounceMs = Math.Max(50, debounceMs);
            _debounce = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Raised once per burst of changes, on a pool thread.</summary>
        public event Action Changed;

        /// <summary>The folder currently being watched, or null.</summary>
        public string Path
        {
            get { lock (_gate) { return _path; } }
        }

        public bool IsWatching
        {
            get { lock (_gate) { return _watcher != null; } }
        }

        /// <summary>
        /// Watches the directory; false when it cannot be watched (missing path,
        /// or the filesystem refuses a watcher). Watching the same folder twice
        /// is a no-op that returns true.
        /// </summary>
        public bool Start(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            lock (_gate)
            {
                if (_disposed) return false;
                if (_watcher != null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                StopLocked();
                return StartLocked(path);
            }
        }

        public void Stop()
        {
            lock (_gate) { StopLocked(); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                StopLocked();
            }
            _debounce.Dispose();
        }

        private bool StartLocked(string path)
        {
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    // Only what the listing shows: names and the hidden/system
                    // flags. Content writes and timestamps must not refresh.
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.Attributes
                };

                watcher.Created += OnFolderEvent;
                watcher.Deleted += OnFolderEvent;
                watcher.Renamed += OnFolderEvent;
                watcher.Changed += OnFolderEvent;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;
                _path = path;
                return true;
            }
            catch (Exception)
            {
                // ArgumentException / FileNotFoundException / IOException / ...:
                // no watcher here - the caller keeps its manual refresh.
                StopLocked();
                return false;
            }
        }

        private void StopLocked()
        {
            _debounce.Change(Timeout.Infinite, Timeout.Infinite); // drop a pending tick

            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnFolderEvent;
                    _watcher.Deleted -= OnFolderEvent;
                    _watcher.Renamed -= OnFolderEvent;
                    _watcher.Changed -= OnFolderEvent;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                }
                catch (Exception)
                {
                    // The directory is already gone; nothing left to release.
                }
                _watcher = null;
            }

            _path = null;
        }

        private void OnFolderEvent(object sender, FileSystemEventArgs e)
        {
            Schedule();
        }

        private void Schedule()
        {
            lock (_gate)
            {
                if (_disposed || _watcher == null) return;
                _debounce.Change(_debounceMs, Timeout.Infinite);
            }
        }

        private void OnDebounceElapsed(object state)
        {
            bool watching;
            lock (_gate) { watching = !_disposed && _watcher != null; }

            if (watching) RaiseChanged();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // Buffer overflow or the folder itself vanished: report once so the
            // view shows the final state, then try to keep following the folder
            // (a deleted folder simply fails to restart).
            lock (_gate)
            {
                if (_disposed || _watcher == null) return;

                string path = _path;
                StopLocked();
                StartLocked(path);
            }

            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
