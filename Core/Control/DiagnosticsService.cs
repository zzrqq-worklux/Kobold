using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Kobold.Controls;
using Kobold.Core.FileSearch;
using Newtonsoft.Json.Linq;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Read-only view of the app for <c>diag.*</c>: identity, config counters,
    /// island/memory/fullscreen state, the folder panels, file-search state and
    /// the log paths (plus bounded tails) an agent needs to look at.
    ///
    /// Every method is called on the UI thread through the control router; the
    /// service never waits for a scan and never writes the log itself.
    /// </summary>
    public sealed class DiagnosticsService
    {
        private const int DefaultTailLines = 200;
        private const int MaxTailLines = 1000;
        private const int DefaultSearchLimit = 50;
        private const int MaxSearchLimit = 200;

        private static FileSearchIndexService SearchService => FileSearchIndexService.Instance;

        public JObject Snapshot()
        {
            var config = WidgetManager.Instance.Config;
            var search = SearchService.Status;
            var index = SearchService.ReadStatus();
            string startedUtc;
            int pid;
            using (var process = Process.GetCurrentProcess())
            {
                pid = process.Id;
                startedUtc = process.StartTime.ToUniversalTime().ToString("o");
            }

            return new JObject
            {
                ["app"] = new JObject
                {
                    ["version"] = typeof(DiagnosticsService).Assembly.GetName().Version.ToString(),
                    ["pid"] = pid,
                    ["startedUtc"] = startedUtc,
                    ["installPath"] = InstallPath()
                },
                ["config"] = new JObject
                {
                    ["path"] = Utils.GetConfigPath(),
                    ["backupPath"] = Utils.GetConfigPath() + ".backup",
                    ["schemaVersion"] = config.SchemaVersion,
                    ["revision"] = ConfigRevision.Compute(config),
                    ["widgetCount"] = config.Widgets == null ? 0 : config.Widgets.Count,
                    ["groupCount"] = config.Groups == null ? 0 : config.Groups.Count
                },
                ["island"] = new JObject
                {
                    ["visible"] = IslandVisible,
                    ["expanded"] = IslandExpanded
                },
                ["search"] = SearchPayload(search, index),
                ["memory"] = MemoryPayload(),
                ["fullscreen"] = new JObject
                {
                    ["avoiding"] = FullscreenAvoiding
                },
                ["folders"] = Folders(),
                ["logs"] = new JObject
                {
                    ["app"] = AppDiagnostics.LogPath,
                    ["search"] = SearchDiagnostics.LogPath
                }
            };
        }

        /// <summary>
        /// One entry per folder view: standalone <see cref="FolderWidget"/>
        /// hosts first, then every folder tab of every group window (inactive
        /// tabs included). Must run on the UI thread.
        /// </summary>
        public JArray Folders()
        {
            var folders = new JArray();
            foreach (var widget in WidgetManager.Instance.Widgets)
            {
                if (widget == null) continue;

                var standalone = widget as FolderWidget;
                if (standalone != null)
                {
                    folders.Add(standalone.PanelView.FolderDiagnosticsSnapshot());
                    continue;
                }

                var group = widget as WidgetGroupWindow;
                if (group == null) continue;

                foreach (var view in group.Views)
                {
                    var folder = view as FolderPanelView;
                    if (folder != null) folders.Add(folder.FolderDiagnosticsSnapshot());
                }
            }
            return folders;
        }

        /// <summary>The file-search status in the same shape the snapshot carries.</summary>
        public JObject SearchStatus()
        {
            return SearchPayload(SearchService.Status, SearchService.ReadStatus());
        }

        /// <summary>
        /// Caps and caps-only query: empty text short-circuits without touching
        /// the service, and nothing here ever waits for a scan.
        /// </summary>
        public JObject SearchQuery(string text, int limit)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JObject
                {
                    ["totalMatches"] = 0,
                    ["limitReached"] = false,
                    ["items"] = new JArray()
                };
            }

            SearchQueryResult result = SearchService.Search(text, ClampSearchLimit(limit), CancellationToken.None);
            var items = new JArray();
            foreach (var entry in result.Items)
            {
                items.Add(new JObject
                {
                    ["path"] = entry.Path,
                    ["name"] = entry.Name,
                    ["isDirectory"] = entry.IsDirectory,
                    ["extension"] = entry.Extension ?? string.Empty,
                    ["sizeBytes"] = entry.SizeBytes,
                    ["modifiedUtc"] = entry.ModifiedUtc > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedUtc).UtcDateTime.ToString("o")
                        : null
                });
            }

            return new JObject
            {
                ["totalMatches"] = result.TotalMatches,
                ["limitReached"] = result.LimitReached,
                ["items"] = items
            };
        }

        // The search commands only queue or flip state; the response is the
        // status as it stands right now, never the result of the work.

        public JObject RebuildSearch()
        {
            SearchService.RequestRebuild();
            return SearchStatus();
        }

        public JObject CancelSearchScan()
        {
            SearchService.CancelScan();
            return SearchStatus();
        }

        public JObject PauseSearch()
        {
            SearchService.Pause();
            return SearchStatus();
        }

        public JObject ResumeSearch()
        {
            SearchService.Resume();
            return SearchStatus();
        }

        public JObject ReconcileSearch()
        {
            SearchService.RequestReconcile();
            return SearchStatus();
        }

        /// <summary>
        /// The last <paramref name="lines"/> lines of one fixed log. Only
        /// <c>app</c> and <c>search</c> exist; the rotated <c>.1</c> is read
        /// when the live file is absent. A missing log is an empty tail, not an
        /// error.
        /// </summary>
        public JObject TailLog(string name, int lines)
        {
            string path;
            if (string.Equals(name, "app", StringComparison.Ordinal))
            {
                path = AppDiagnostics.LogPath;
            }
            else if (string.Equals(name, "search", StringComparison.Ordinal))
            {
                path = SearchDiagnostics.LogPath;
            }
            else
            {
                throw ControlException.Validation("name must be 'app' or 'search'.");
            }

            int count = lines <= 0 ? DefaultTailLines : Math.Min(lines, MaxTailLines);
            string readPath = File.Exists(path) || !File.Exists(path + ".1") ? path : path + ".1";

            var rows = new List<string>();
            if (File.Exists(readPath))
            {
                try
                {
                    using (var stream = new FileStream(readPath, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) rows.Add(line);
                    }
                }
                catch (IOException)
                {
                    // A rotating/held log is a best-effort tail, never a failure.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            int start = Math.Max(0, rows.Count - count);
            var tail = new JArray();
            for (int i = start; i < rows.Count; i++) tail.Add(rows[i]);

            return new JObject
            {
                ["path"] = readPath,
                ["lines"] = tail
            };
        }

        private bool IslandVisible => WidgetManager.Instance.IslandVisible;

        private bool IslandExpanded => WidgetManager.Instance.IslandExpanded;

        private bool FullscreenAvoiding => WidgetManager.Instance.FullscreenAvoiding;

        private JObject MemoryPayload()
        {
            long workingSet = 0;
            long privateBytes = 0;
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    workingSet = process.WorkingSet64;
                    privateBytes = process.PrivateMemorySize64;
                }
            }
            catch (Exception)
            {
                // A status probe must still answer when the process object fails.
            }

            return new JObject
            {
                ["workingSetBytes"] = workingSet,
                ["privateBytes"] = privateBytes,
                ["lastTrimUtc"] = WidgetManager.Instance.LastTrimUtc?.ToString("o"),
                ["trimmedThisIdlePeriod"] = WidgetManager.Instance.TrimmedThisIdlePeriod
            };
        }

        private JObject SearchPayload(FileSearchStatus status, IndexStatus index)
        {
            return new JObject
            {
                ["state"] = status.State.ToString(),
                ["message"] = status.Message ?? string.Empty,
                ["filesIndexed"] = status.FilesIndexed,
                ["directoriesScanned"] = status.DirectoriesScanned,
                ["skippedDirectories"] = status.SkippedDirectories,
                ["incrementalAvailable"] = status.IncrementalAvailable,
                ["scanCancelled"] = status.ScanCancelled,
                ["needsRebuild"] = status.NeedsRebuild,
                ["databasePath"] = status.DatabasePath ?? string.Empty,
                ["reconcileMinutes"] = SearchService.ReconcileIntervalMinutes,
                ["index"] = new JObject
                {
                    ["hasIndex"] = index.HasIndex,
                    ["lastScanUtc"] = index.LastScanUtc?.ToString("o"),
                    ["lastReconcileUtc"] = index.LastReconcileUtc?.ToString("o"),
                    ["schemaVersion"] = index.SchemaVersion,
                    ["skippedDirectories"] = index.SkippedDirectories
                }
            };
        }

        private static int ClampSearchLimit(int limit)
        {
            return limit <= 0 ? DefaultSearchLimit : Math.Min(limit, MaxSearchLimit);
        }

        private static string InstallPath()
        {
            try
            {
                return Path.GetDirectoryName(typeof(DiagnosticsService).Assembly.Location);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
