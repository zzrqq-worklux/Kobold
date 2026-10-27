using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Kobold.Core.FileSearch
{
    /// <summary>
    /// SQLite implementation of the file-search index. This is the only file besides
    /// <see cref="SqliteBootstrap"/> that touches SQLite types.
    ///
    /// Layout: a <c>files</c> table plus a <c>meta</c> key/value table. A full scan
    /// fills the identically shaped <c>files_new</c> staging table and publishes it
    /// with a short drop/rename transaction, so an aborted scan leaves the queried
    /// index untouched. Writes share one <see cref="_writeGate"/> (one writer in the
    /// process); queries open their own read-only connection and never wait on it.
    /// </summary>
    public sealed class SearchIndexStore : ISearchIndexStore
    {
        private const int BusyTimeoutMs = 10000;

        /// <summary>
        /// Schema generation. 1 was the first published table; 2 adds the derived
        /// <c>Parent</c> column (and its index) that makes a directory's direct
        /// children one equality seek instead of a subtree scan. The numeric form is
        /// what <c>PRAGMA user_version</c> carries; keep the two in step.
        /// </summary>
        private const int SchemaVersion = 2;
        private const string SchemaVersionText = "2";

        private const string CreateFilesSql = @"
CREATE TABLE IF NOT EXISTS files (
    Path TEXT PRIMARY KEY COLLATE NOCASE,
    Name TEXT NOT NULL,
    NameLower TEXT NOT NULL,
    Extension TEXT NOT NULL,
    IsDirectory INTEGER NOT NULL,
    SizeBytes INTEGER NOT NULL,
    ModifiedUtc INTEGER NOT NULL,
    Root TEXT NOT NULL,
    Parent TEXT NOT NULL DEFAULT '' COLLATE NOCASE);";

        private const string CreateMetaSql = @"
CREATE TABLE IF NOT EXISTS meta (Key TEXT PRIMARY KEY, Value TEXT);";

        private const string CreateIndexesSql = @"
CREATE INDEX IF NOT EXISTS idx_files_name_lower ON files(NameLower);
CREATE INDEX IF NOT EXISTS idx_files_extension ON files(Extension);
CREATE INDEX IF NOT EXISTS idx_files_root ON files(Root);
CREATE INDEX IF NOT EXISTS idx_files_parent ON files(Parent);";

        private const string CreateStagingSql = @"
CREATE TABLE files_new (
    Path TEXT PRIMARY KEY COLLATE NOCASE,
    Name TEXT NOT NULL,
    NameLower TEXT NOT NULL,
    Extension TEXT NOT NULL,
    IsDirectory INTEGER NOT NULL,
    SizeBytes INTEGER NOT NULL,
    ModifiedUtc INTEGER NOT NULL,
    Root TEXT NOT NULL,
    Parent TEXT NOT NULL DEFAULT '' COLLATE NOCASE);";

        private const string CreateSeenSql = @"
CREATE TABLE reconcile_seen (Path TEXT PRIMARY KEY COLLATE NOCASE);";

        // v1 -> v2, in place. Parent is derived, so the default is only what existing
        // rows carry between the ALTER and the UPDATE that follows it. The fresh
        // tables declare the same default: a v1 8-column INSERT into a v2 file then
        // succeeds with Parent='', and the version-guarded backfill repairs those rows
        // on the next v2 launch instead of wedging the old build.
        private const string AddParentColumnSql =
            @"ALTER TABLE files ADD COLUMN Parent TEXT NOT NULL DEFAULT '' COLLATE NOCASE;";

        // Written in the same transaction as the backfill. A v2 file reopened by a v1
        // build is stamped back to 1 while the v1 8-column INSERT leaves Parent at the
        // '' default, so the version alone is not enough to see those rows; the pair
        // (user_version, stamp) is, and re-running the idempotent backfill repairs them.
        private const string ParentBackfillStampKey = "parent_filled";

        // ParentOf in SQL, for every row whose Parent may be missing or stale.
        //
        // Step 1 is the plain last-separator rule: rtrim(Path, '\') drops any trailing
        // separator, and rtrim(<that>, <every non-separator character>) trims back to the
        // last separator, leaving the directory plus exactly one separator. A drive root
        // ("C:\") trims all the way to "" here, so the rule already yields the parentless
        // value for both "C:\" and "C:\\".
        //
        // Step 2 extends that to a UNC share root: "\\server\share" computes to
        // "\\server\" in step 1, but the server namespace is not a directory inside the
        // index, so it has no parent either (exactly what ParentOf returns). A parent key
        // that is only the server root has exactly three separators: the two leading ones
        // and the one before the share.
        //
        // Both forms are pinned against each other on seeded v1 tables, including the
        // double-separator and UNC cases.
        private const string BackfillParentSql = @"
UPDATE files SET Parent = rtrim(rtrim(Path, '\'), replace(rtrim(Path, '\'), '\', ''));
UPDATE files SET Parent = ''
WHERE substr(Parent, 1, 2) = '\\'
  AND length(Parent) - length(replace(Parent, '\', '')) <= 3;";

        // The direct children of one directory, by its parent key: every row stores the
        // containing directory with one trailing separator (see ParentOf), so this is
        // one equality seek on idx_files_parent instead of a range scan that returns
        // the whole subtree (42.5k directories read ~2.0M descendant rows that way).
        // The column is NOCASE like Path, so a caller's case variant still matches.
        private const string ChildrenSql = @"
SELECT Path, Name, NameLower, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root
FROM files
WHERE Parent = @p;";

        private const string DeleteSql = @"DELETE FROM files WHERE Path = @path;";

        // A directory delete removes the row itself (Path = @path) plus its whole
        // subtree. The subtree is the prefix range [@start, @end): @start is the
        // directory with one trailing separator (so "C:\p\sub" never claims
        // "C:\p\submarine") and @end is its exclusive successor. Path is the NOCASE
        // primary key, so this seeks; "Path LIKE @prefix ESCAPE '\'" could not. The
        // reconcile "mark seen under" copy uses the same predicate.
        private const string DeletePrefixSql =
            @"DELETE FROM files WHERE Path = @path OR (Path >= @start AND Path < @end);";

        // {0} is "files" or "files_new"; both are internal constants, never user input.
        // The DO UPDATE is guarded so a reconcile over an unchanged tree performs no
        // write at all: an UPDATE would otherwise touch every row's MVCC page even
        // when the values are identical. Path stays the conflict key and is never
        // rewritten.
        private const string UpsertSql = @"
INSERT INTO {0} (Path, Name, NameLower, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root, Parent)
VALUES (@path, @name, @nameLower, @extension, @isDirectory, @sizeBytes, @modifiedUtc, @root, @parent)
ON CONFLICT(Path) DO UPDATE SET
    Name = excluded.Name,
    NameLower = excluded.NameLower,
    Extension = excluded.Extension,
    IsDirectory = excluded.IsDirectory,
    SizeBytes = excluded.SizeBytes,
    ModifiedUtc = excluded.ModifiedUtc,
    Root = excluded.Root,
    Parent = excluded.Parent
WHERE {0}.Name IS NOT excluded.Name
   OR {0}.NameLower IS NOT excluded.NameLower
   OR {0}.Extension IS NOT excluded.Extension
   OR {0}.IsDirectory IS NOT excluded.IsDirectory
   OR {0}.SizeBytes IS NOT excluded.SizeBytes
   OR {0}.ModifiedUtc IS NOT excluded.ModifiedUtc
   OR {0}.Root IS NOT excluded.Root
   OR {0}.Parent IS NOT excluded.Parent;";

        private const string SetMetaSql = @"
INSERT INTO meta (Key, Value) VALUES (@key, @value)
ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

        private const string CountSql = @"
SELECT COUNT(*) FROM files
WHERE NameLower LIKE @contains ESCAPE '\' OR Path LIKE @contains ESCAPE '\';";

        // Prefix phase: the range predicate is equivalent to LIKE 'q%' but seeks the
        // NameLower index (spike: 0.5ms vs 22.9ms for the LIKE form).
        private const string PrefixSql = @"
SELECT Path, Name, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root
FROM files
WHERE NameLower >= @q AND NameLower <= @q || char(0x10FFFF)
ORDER BY NameLower, Path
LIMIT @limit;";

        // Suffix phase: name-infix rows first, then path-only rows.
        private const string SuffixSql = @"
SELECT Path, Name, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root,
       CASE WHEN NameLower LIKE @contains ESCAPE '\' THEN 1 ELSE 2 END AS MatchRank
FROM files
WHERE (NameLower LIKE @contains ESCAPE '\' OR Path LIKE @contains ESCAPE '\')
  AND NOT (NameLower >= @q AND NameLower <= @q || char(0x10FFFF))
ORDER BY MatchRank, NameLower, Path
LIMIT @remaining;";

        // A semaphore, not a Monitor: a reconcile session holds the gate for a whole
        // pass, and its commit/dispose must be able to release it even when the caller
        // hands the pass to another thread. Monitor.Exit on a foreign thread throws
        // SynchronizationLockException and would wedge every later writer forever.
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly string _databasePath;

        private bool _fullScanActive;
        private bool _reconcileActive;
        private bool _reconcileSessionActive;
        private bool _disposed;

        public SearchIndexStore(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("A database path is required.", "databasePath");

            SqliteBootstrap.EnsureInitialized();
            _databasePath = Path.GetFullPath(databasePath);
        }

        public string DatabasePath
        {
            get { return _databasePath; }
        }

        public bool IsFullScanActive
        {
            get
            {
                using (EnterWriteGate())
                {
                    ThrowIfDisposed();
                    return _fullScanActive;
                }
            }
        }

        public void Dispose()
        {
            // Connections are opened per operation, so there is no handle to release.
            _disposed = true;
        }

        /// <summary>
        /// Holds the single-writer gate for the scope of a <c>using</c>. Released on
        /// any thread, which is what a reconcile session needs: it owns the gate for a
        /// whole pass and must release it from commit/dispose (and even from a
        /// finalizer-like cleanup path) without thread affinity.
        /// </summary>
        private WriteLock EnterWriteGate()
        {
            _writeGate.Wait();
            return new WriteLock(_writeGate);
        }

        /// <summary>One acquisition of <see cref="_writeGate"/>; release exactly once.</summary>
        private struct WriteLock : IDisposable
        {
            private SemaphoreSlim _gate;

            public WriteLock(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                SemaphoreSlim gate = _gate;
                if (gate == null) return;
                _gate = null;
                gate.Release();
            }
        }

        public void EnsureSchema()
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                string directory = Path.GetDirectoryName(_databasePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using (var connection = OpenWrite())
                {
                    ExecuteScalar(connection, "PRAGMA journal_mode=WAL;");

                    Execute(connection, CreateFilesSql);
                    Execute(connection, CreateMetaSql);
                    // A v1 table is upgraded in place: the parent column is additive, so
                    // an existing index must not cost a rebuild just for it. A fresh
                    // database already has the column and pays one idempotent UPDATE.
                    MigrateParentColumn(connection);
                    Execute(connection, CreateIndexesSql);
                    // A process that died mid-scan leaves its staging table behind.
                    // It is never trusted: the next full scan recreates it from scratch.
                    Execute(connection, "DROP TABLE IF EXISTS files_new;");

                    // Read before the transaction: SqliteCommand requires an enlisted
                    // transaction for every command while one is open.
                    string sqliteVersion = Convert.ToString(
                        ExecuteScalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture);

                    using (var transaction = connection.BeginTransaction())
                    {
                        // Recorded so a bug report can tell which native library shipped.
                        SetMeta(connection, transaction, "sqlite_version", sqliteVersion);
                        SetMeta(connection, transaction, "schema_version", SchemaVersionText);
                        transaction.Commit();
                    }

                    // Stamped last, after the migration committed: a database that was
                    // interrupted between the ALTER and this PRAGMA keeps the old number
                    // and is simply migrated again (the backfill is idempotent).
                    Execute(connection, "PRAGMA user_version=" + SchemaVersionText + ";");
                }
            }
        }

        /// <summary>
        /// Adds the derived parent column to a table that predates it and fills it for
        /// every existing row, in one transaction: no row is re-read, no scan metadata
        /// is touched and nothing is rebuilt.
        ///
        /// The guard is deliberately not the column alone. A v1 build that opens a v2
        /// file adds nothing (it does not know the column) but inserts its 8 columns,
        /// which leaves <c>Parent=''</c> because of the NOT NULL default, and its
        /// <c>EnsureSchema</c> stamps <c>user_version</c> back to 1. Column-presence
        /// alone would leave those rows invisible to <c>ReadChildren</c> forever. So the
        /// backfill also runs when the file is not at version 2 or does not carry the
        /// migration stamp; it is idempotent, so running it over correct rows is a
        /// no-op in value and costs one UPDATE over the table.
        /// </summary>
        private static void MigrateParentColumn(SqliteConnection connection)
        {
            bool hasColumn = ColumnExists(connection, "files", "Parent");
            long userVersion = ScalarLong(ExecuteScalar(connection, "PRAGMA user_version;"));
            string stamp = ReadMetaValue(connection, ParentBackfillStampKey);

            bool needsBackfill = !hasColumn
                || userVersion < SchemaVersion
                || !string.Equals(stamp, SchemaVersionText, StringComparison.Ordinal);
            if (!needsBackfill) return;

            using (var transaction = connection.BeginTransaction())
            {
                if (!hasColumn) Execute(connection, AddParentColumnSql, transaction);
                Execute(connection, BackfillParentSql, transaction);
                SetMeta(connection, transaction, ParentBackfillStampKey, SchemaVersionText);
                transaction.Commit();
            }
        }

        public void BeginFullScan()
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                {
                    Execute(connection, "DROP TABLE IF EXISTS files_new;");
                    Execute(connection, CreateStagingSql);
                }
                _fullScanActive = true;
            }
        }

        public void UpsertScanBatch(IReadOnlyList<SearchEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                string table = _fullScanActive ? "files_new" : "files";
                using (var connection = OpenWrite())
                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = CreateUpsertCommand(connection, transaction, table))
                    {
                        foreach (var entry in entries)
                        {
                            if (entry == null || string.IsNullOrEmpty(entry.Path)) continue;
                            BindUpsert(command, entry);
                            command.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
            }
        }

        public void CompleteFullScan(long skippedDirectories, long skippedReparsePoints, long skippedLongPaths,
            string fingerprint)
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();
                if (!_fullScanActive) throw new InvalidOperationException("No full scan is in progress.");

                using (var connection = OpenWrite())
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, "DROP TABLE IF EXISTS files;", transaction);
                    Execute(connection, "ALTER TABLE files_new RENAME TO files;", transaction);
                    Execute(connection, "CREATE INDEX idx_files_name_lower ON files(NameLower);", transaction);
                    Execute(connection, "CREATE INDEX idx_files_extension ON files(Extension);", transaction);
                    Execute(connection, "CREATE INDEX idx_files_root ON files(Root);", transaction);
                    Execute(connection, "CREATE INDEX idx_files_parent ON files(Parent);", transaction);

                    SetMeta(connection, transaction, "last_scan_utc", UtcNowText());
                    SetMeta(connection, transaction, "skipped_dirs", AsText(skippedDirectories));
                    SetMeta(connection, transaction, "skipped_reparse", AsText(skippedReparsePoints));
                    SetMeta(connection, transaction, "skipped_long_paths", AsText(skippedLongPaths));
                    SetMeta(connection, transaction, "settings_fingerprint", fingerprint);
                    SetMeta(connection, transaction, "row_count",
                        AsText(ScalarLong(connection, transaction, "SELECT COUNT(*) FROM files;")));
                    SetMeta(connection, transaction, "last_error", string.Empty);

                    transaction.Commit();
                }

                _fullScanActive = false;
            }
        }

        public void AbortFullScan()
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                {
                    Execute(connection, "DROP TABLE IF EXISTS files_new;");
                }
                _fullScanActive = false;
            }
        }

        public void BeginReconcile()
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                {
                    Execute(connection, "DROP TABLE IF EXISTS reconcile_seen;");
                    Execute(connection, CreateSeenSql);
                }
                _reconcileActive = true;
            }
        }

        public void MarkSeenBatch(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;

            using (EnterWriteGate())
            {
                ThrowIfDisposed();
                if (!_reconcileActive) throw new InvalidOperationException("No reconcile is in progress.");

                using (var connection = OpenWrite())
                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT OR IGNORE INTO reconcile_seen (Path) VALUES (@path);";
                        var path = command.Parameters.Add("@path", SqliteType.Text);
                        foreach (var value in paths)
                        {
                            if (string.IsNullOrEmpty(value)) continue;
                            path.Value = value;
                            command.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
            }
        }

        public void MarkSeenPathsUnder(IReadOnlyList<string> prefixes)
        {
            if (prefixes == null || prefixes.Count == 0) return;

            using (EnterWriteGate())
            {
                ThrowIfDisposed();
                if (!_reconcileActive) throw new InvalidOperationException("No reconcile is in progress.");

                using (var connection = OpenWrite())
                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT OR IGNORE INTO reconcile_seen (Path)
SELECT Path FROM files
 WHERE Path = @path OR (Path >= @start AND Path < @end);";
                        var exact = command.Parameters.Add("@path", SqliteType.Text);
                        var start = command.Parameters.Add("@start", SqliteType.Text);
                        var end = command.Parameters.Add("@end", SqliteType.Text);
                        foreach (var value in prefixes)
                        {
                            if (string.IsNullOrEmpty(value)) continue;

                            string path;
                            string from;
                            string to;
                            BuildPrefix(value, out path, out from, out to);
                            exact.Value = path;
                            start.Value = from;
                            end.Value = to;
                            command.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
            }
        }

        public void CompleteReconcile(IReadOnlyList<string> roots)
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();
                if (!_reconcileActive) throw new InvalidOperationException("No reconcile is in progress.");

                using (var connection = OpenWrite())
                using (var transaction = connection.BeginTransaction())
                {
                    if (roots != null)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
DELETE FROM files
 WHERE Root = @root
   AND NOT EXISTS (SELECT 1 FROM reconcile_seen s WHERE s.Path = files.Path);";
                            var root = command.Parameters.Add("@root", SqliteType.Text);
                            foreach (var value in roots)
                            {
                                if (string.IsNullOrEmpty(value)) continue;
                                root.Value = value;
                                command.ExecuteNonQuery();
                            }
                        }
                    }

                    Execute(connection, "DROP TABLE IF EXISTS reconcile_seen;", transaction);
                    SetMeta(connection, transaction, "last_reconcile_utc", UtcNowText());
                    transaction.Commit();
                }

                _reconcileActive = false;
            }
        }

        public void AbortReconcile()
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                {
                    Execute(connection, "DROP TABLE IF EXISTS reconcile_seen;");
                }
                _reconcileActive = false;
            }
        }

        /// <summary>
        /// Opens a reconcile session. The single-writer gate is held for the session's
        /// whole lifetime, so no other write can interleave with the pass. A second
        /// session on any thread throws instead of sharing the connection. Commit and
        /// dispose release the gate from whatever thread calls them.
        /// </summary>
        public IReconcileSession OpenReconcileSession()
        {
            // Fail fast before waiting: a semaphore is not re-entrant, so a second
            // session on the opening thread would otherwise block forever instead of
            // reporting the programming error. The check inside the gate is the
            // authoritative one (another thread may hold the session right now).
            ThrowIfDisposed();
            if (_reconcileSessionActive)
                throw new InvalidOperationException("A reconcile session is already open.");

            _writeGate.Wait();
            bool opened = false;
            SqliteConnection connection = null;
            SqliteTransaction transaction = null;
            try
            {
                ThrowIfDisposed();
                if (_reconcileSessionActive)
                    throw new InvalidOperationException("A reconcile session is already open.");

                // WAL + one transaction for the whole pass: a cancel or an error rolls
                // back to the exact pre-pass index, and queries keep reading the last
                // committed snapshot until the commit.
                connection = OpenWrite();
                transaction = connection.BeginTransaction();
                var session = new ReconcileSession(this, connection, transaction);
                _reconcileSessionActive = true;
                opened = true;
                return session;
            }
            catch (Exception)
            {
                // Dispose commands first: they are children of the connection.
                if (transaction != null) transaction.Dispose();
                if (connection != null) connection.Dispose();
                throw;
            }
            finally
            {
                if (!opened) _writeGate.Release();
            }
        }

        /// <summary>Called exactly once by the session's commit/dispose; any thread.</summary>
        private void EndReconcileSession()
        {
            _reconcileSessionActive = false;
            _writeGate.Release();
        }

        public void Upsert(SearchEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Path))
                throw new ArgumentException("An entry with a path is required.", "entry");

            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                using (var transaction = connection.BeginTransaction())
                using (var command = CreateUpsertCommand(connection, transaction, "files"))
                {
                    BindUpsert(command, entry);
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
        }

        public void Delete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM files WHERE Path = @path;";
                    command.Parameters.AddWithValue("@path", path);
                    command.ExecuteNonQuery();
                }
            }
        }

        public void DeletePathAndPrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                // A directory delete removes the row itself and its whole subtree. The
                // trailing separator is stripped so "C:\p\sub\" also matches the stored
                // "C:\p\sub" row, while the range starts at "C:\p\sub\" so it never
                // claims "C:\p\submarine". A drive root keeps its separator.
                string exact;
                string start;
                string end;
                BuildPrefix(path, out exact, out start, out end);

                using (var connection = OpenWrite())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = DeletePrefixSql;
                    command.Parameters.AddWithValue("@path", exact);
                    command.Parameters.AddWithValue("@start", start);
                    command.Parameters.AddWithValue("@end", end);
                    command.ExecuteNonQuery();
                }
            }
        }

        public SearchQueryResult Query(string text, int limit, CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();

            string term = text == null ? string.Empty : text.Trim();
            if (term.Length == 0 || limit <= 0 || !File.Exists(_databasePath))
                return SearchQueryResult.Empty;

            string q = term.ToLowerInvariant();
            string contains = "%" + SearchPath.EscapeLike(q) + "%";

            using (var connection = OpenRead())
            // Deferred (not IMMEDIATE) so a read-only connection can take it; the first
            // SELECT pins one WAL snapshot for the count and both row phases, so a
            // concurrent full-scan swap cannot mix generations.
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                long total;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = CountSql;
                    command.Parameters.AddWithValue("@contains", contains);
                    total = ScalarLong(command);
                }
                if (total == 0)
                {
                    transaction.Commit();
                    return new SearchQueryResult(new SearchEntry[0], 0, false);
                }

                var items = new List<SearchEntry>(Math.Min(limit, ToInt(total)));
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = PrefixSql;
                    command.Parameters.AddWithValue("@q", q);
                    command.Parameters.AddWithValue("@limit", limit);
                    ReadEntries(command, items, token);
                }

                if (items.Count < limit)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = SuffixSql;
                        command.Parameters.AddWithValue("@q", q);
                        command.Parameters.AddWithValue("@contains", contains);
                        command.Parameters.AddWithValue("@remaining", limit - items.Count);
                        ReadEntries(command, items, token);
                    }
                }

                transaction.Commit();
                int totalMatches = ToInt(total);
                return new SearchQueryResult(items, totalMatches, totalMatches > items.Count);
            }
        }

        public IndexStatus ReadStatus()
        {
            ThrowIfDisposed();

            var status = new IndexStatus();
            if (!File.Exists(_databasePath)) return status;

            using (var connection = OpenRead())
            {
                status.SchemaVersion = ToInt(ScalarLong(connection, "PRAGMA user_version;"));
                if (!TableExists(connection, "meta")) return status;

                var meta = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Key, Value FROM meta;";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(1)) continue;
                            meta[reader.GetString(0)] = reader.GetString(1);
                        }
                    }
                }

                status.SkippedDirectories = ParseLong(GetMeta(meta, "skipped_dirs"));
                status.SettingsFingerprint = GetMeta(meta, "settings_fingerprint");
                status.LastError = GetMeta(meta, "last_error");
                status.LastScanUtc = ParseUtc(GetMeta(meta, "last_scan_utc"));
                status.LastReconcileUtc = ParseUtc(GetMeta(meta, "last_reconcile_utc"));
                status.HasIndex = status.LastScanUtc.HasValue && TableExists(connection, "files");
            }
            return status;
        }

        public long GetDatabaseSizeBytes()
        {
            ThrowIfDisposed();

            return FileSize(_databasePath) + FileSize(_databasePath + "-wal") + FileSize(_databasePath + "-shm");
        }

        public void Checkpoint()
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                {
                    // TRUNCATE empties the -wal file so the on-disk size is meaningful.
                    ExecuteScalar(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                }
            }
        }

        public void Vacuum()
        {
            using (EnterWriteGate())
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                {
                    Execute(connection, "VACUUM;");
                }
            }
        }

        private SqliteConnection OpenWrite()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                // One connection per operation; a pool would keep the file handle alive
                // after Dispose and block replacing/deleting the index.
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ToString());
            try
            {
                connection.Open();

                Execute(connection, "PRAGMA busy_timeout=" + BusyTimeoutMs.ToString(CultureInfo.InvariantCulture) + ";");
                // WAL + NORMAL: an index can be rebuilt, so a power cut may lose the last
                // commits but cannot corrupt the file. Not persisted in the database.
                Execute(connection, "PRAGMA synchronous=NORMAL;");
                return connection;
            }
            catch (Exception)
            {
                // A failed open/PRAGMA still holds the native file handle; release it
                // before the caller tries to quarantine a corrupt database.
                connection.Dispose();
                throw;
            }
        }

        private SqliteConnection OpenRead()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                // Readers keep a private cache: a shared-cache reader would hold table
                // locks and turn a concurrent write into SQLITE_LOCKED instead of a
                // short wait on the WAL.
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            var connection = new SqliteConnection(builder.ToString());
            try
            {
                connection.Open();

                Execute(connection, "PRAGMA busy_timeout=" + BusyTimeoutMs.ToString(CultureInfo.InvariantCulture) + ";");
                return connection;
            }
            catch (Exception)
            {
                // Do not leak the native handle when the read-only open fails.
                connection.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The parent key of one indexed row: <c>C:\dir\file.txt</c> becomes
        /// <c>C:\dir\</c>, <c>C:\dir</c> becomes <c>C:\</c>, a drive root becomes
        /// "" (the value a parentless root row stores), and so does a UNC share root
        /// (<c>\\server\share</c>: its only remaining "parent" is the server
        /// namespace, which is not a directory in the index). The key is the
        /// containing directory with exactly one trailing separator, which is what
        /// makes the parent lookup an index equality seek instead of a subtree scan.
        /// Trailing separators are all stripped first, so <c>C:\dir\\</c> keys to
        /// <c>C:\</c> and can never yield <c>C:\dir\\</c>.
        ///
        /// The v1 -> v2 backfill mirrors this rule in SQL; the two must stay equal.
        /// </summary>
        internal static string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            string value = path.TrimEnd('\\');

            // "C:" is all that is left of a drive root: it has no parent.
            if (value.Length == 2 && value[1] == ':') return string.Empty;

            int separator = value.LastIndexOf('\\');
            if (separator < 0) return string.Empty;

            string parent = value.Substring(0, separator + 1);
            // "\\server\" is what is left of a UNC share root: a server namespace
            // has no parent inside the index. Three separators are the two leading
            // ones plus the one before the share.
            if (parent.Length >= 2 && parent[0] == '\\' && parent[1] == '\\'
                && parent.Length - parent.Replace("\\", string.Empty).Length <= 3)
            {
                return string.Empty;
            }
            return parent;
        }

        private static void BuildPrefix(string path, out string exact, out string start, out string end)
        {
            string directory = path.TrimEnd('\\');
            if (directory.Length == 2 && directory[1] == ':') directory += "\\";
            exact = directory.Length == 0 ? path : directory;
            // The subtree is the prefix range [@start, @end): every stored path that
            // begins with "<directory>\" sorts at or after @start and before the
            // successor of that separator (0x5C + 1 = ']'), and nothing else does.
            start = exact.TrimEnd('\\') + "\\";
            end = start.Substring(0, start.Length - 1) + (char)(start[start.Length - 1] + 1);
        }

        private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction,
            string table)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = string.Format(CultureInfo.InvariantCulture, UpsertSql, table);
            command.Parameters.Add("@path", SqliteType.Text);
            command.Parameters.Add("@name", SqliteType.Text);
            command.Parameters.Add("@nameLower", SqliteType.Text);
            command.Parameters.Add("@extension", SqliteType.Text);
            command.Parameters.Add("@isDirectory", SqliteType.Integer);
            command.Parameters.Add("@sizeBytes", SqliteType.Integer);
            command.Parameters.Add("@modifiedUtc", SqliteType.Integer);
            command.Parameters.Add("@root", SqliteType.Text);
            command.Parameters.Add("@parent", SqliteType.Text);
            return command;
        }

        private static void BindUpsert(SqliteCommand command, SearchEntry entry)
        {
            string name = entry.Name ?? string.Empty;
            command.Parameters["@path"].Value = entry.Path;
            command.Parameters["@name"].Value = name;
            command.Parameters["@nameLower"].Value =
                string.IsNullOrEmpty(entry.NameLower) ? name.ToLowerInvariant() : entry.NameLower;
            command.Parameters["@extension"].Value = entry.Extension ?? string.Empty;
            command.Parameters["@isDirectory"].Value = entry.IsDirectory ? 1 : 0;
            command.Parameters["@sizeBytes"].Value = entry.SizeBytes;
            command.Parameters["@modifiedUtc"].Value = entry.ModifiedUtc;
            command.Parameters["@root"].Value = entry.Root ?? string.Empty;
            // Derived here (not on SearchEntry): the scanner never has to know about
            // the storage layout, and every write path gets the same value.
            command.Parameters["@parent"].Value = ParentOf(entry.Path);
        }

        private static void ReadEntries(SqliteCommand command, List<SearchEntry> items, CancellationToken token)
        {
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if ((items.Count & 63) == 0) token.ThrowIfCancellationRequested();

                    string name = reader.GetString(1);
                    items.Add(new SearchEntry
                    {
                        Path = reader.GetString(0),
                        Name = name,
                        NameLower = name.ToLowerInvariant(),
                        Extension = reader.GetString(2),
                        IsDirectory = reader.GetInt64(3) != 0,
                        SizeBytes = reader.GetInt64(4),
                        ModifiedUtc = reader.GetInt64(5),
                        Root = reader.GetString(6)
                    });
                }
            }
        }

        private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key,
            string value)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = SetMetaSql;
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private static string GetMeta(Dictionary<string, string> meta, string key)
        {
            string value;
            return meta.TryGetValue(key, out value) && value != null ? value : string.Empty;
        }

        /// <summary>One meta value read on its own, or null when the key is absent.</summary>
        private static string ReadMetaValue(SqliteConnection connection, string key)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Value FROM meta WHERE Key = @key;";
                command.Parameters.AddWithValue("@key", key);
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static long ParseLong(string value)
        {
            long parsed;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static DateTime? ParseUtc(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            DateTime parsed;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                return null;
            return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }

        private static string UtcNowText()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        private static string AsText(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static int ToInt(long value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static long FileSize(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>
        /// True when <paramref name="table"/> has <paramref name="column"/>. The
        /// migration needs this as well as the version stamp: a table that a previous
        /// build created without the stamp still gets the column, and a database whose
        /// stamp says 2 but whose table lost the column is still repaired.
        /// </summary>
        private static bool ColumnExists(SqliteConnection connection, string table, string column)
        {
            using (var command = connection.CreateCommand())
            {
                // The table name is an internal constant, never user input.
                command.CommandText = "PRAGMA table_info(" + table + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        // Column 1 of table_info is the column name.
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }

        private static bool TableExists(SqliteConnection connection, string name)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
                command.Parameters.AddWithValue("@name", name);
                return ScalarLong(command) > 0;
            }
        }

        private static void Execute(SqliteConnection connection, string sql, SqliteTransaction transaction = null)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                if (transaction != null) command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
        }

        private static object ExecuteScalar(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            return ScalarLong(ExecuteScalar(connection, sql));
        }

        private static long ScalarLong(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                return ScalarLong(command.ExecuteScalar());
            }
        }

        private static long ScalarLong(SqliteCommand command)
        {
            return ScalarLong(command.ExecuteScalar());
        }

        private static long ScalarLong(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            if (value is long) return (long)value;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().Name);
        }

        /// <summary>
        /// One write connection and transaction for a whole reconcile pass. The
        /// commands are created once and only rebound, so reading a directory's rows
        /// is one parent-key equality seek and writing a difference is one statement.
        /// </summary>
        private sealed class ReconcileSession : IReconcileSession
        {
            private readonly SearchIndexStore _store;
            private readonly SqliteConnection _connection;
            private readonly SqliteTransaction _transaction;
            private readonly SqliteCommand _children;
            private readonly SqliteCommand _upsert;
            private readonly SqliteCommand _delete;
            private readonly SqliteCommand _deletePrefix;
            private bool _committed;
            private int _closed;

            public ReconcileSession(SearchIndexStore store, SqliteConnection connection,
                SqliteTransaction transaction)
            {
                _store = store;
                _connection = connection;
                _transaction = transaction;

                _children = connection.CreateCommand();
                _children.Transaction = transaction;
                _children.CommandText = ChildrenSql;
                _children.Parameters.Add("@p", SqliteType.Text);

                _upsert = CreateUpsertCommand(connection, transaction, "files");

                _delete = connection.CreateCommand();
                _delete.Transaction = transaction;
                _delete.CommandText = DeleteSql;
                _delete.Parameters.Add("@path", SqliteType.Text);

                _deletePrefix = connection.CreateCommand();
                _deletePrefix.Transaction = transaction;
                _deletePrefix.CommandText = DeletePrefixSql;
                _deletePrefix.Parameters.Add("@path", SqliteType.Text);
                _deletePrefix.Parameters.Add("@start", SqliteType.Text);
                _deletePrefix.Parameters.Add("@end", SqliteType.Text);
            }

            public IReadOnlyList<SearchEntry> ReadChildren(string directory, CancellationToken token)
            {
                ThrowIfDone();
                if (string.IsNullOrEmpty(directory)) return new SearchEntry[0];

                // The parent key of the directory's children is the directory itself
                // plus one separator; a drive root already ends with its separator
                // ("C:\"), so appending a second one would read nothing.
                string parent = directory.EndsWith("\\", StringComparison.Ordinal)
                    ? directory
                    : directory + "\\";
                _children.Parameters["@p"].Value = parent;

                var children = new List<SearchEntry>();
                int rows = 0;
                using (var reader = _children.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        // A reconcile pass spans every directory: a cancel must not wait
                        // for one directory to finish marshalling.
                        if ((rows++ & 1023) == 0) token.ThrowIfCancellationRequested();

                        string path = reader.GetString(0);
                        // Belt and braces: the parent key already excludes descendants,
                        // so this only guards a row whose Parent was written by hand.
                        if (path.Length <= parent.Length) continue;
                        if (path.IndexOf('\\', parent.Length) >= 0) continue;

                        children.Add(new SearchEntry
                        {
                            Path = path,
                            Name = reader.GetString(1),
                            NameLower = reader.GetString(2),
                            Extension = reader.GetString(3),
                            IsDirectory = reader.GetInt64(4) != 0,
                            SizeBytes = reader.GetInt64(5),
                            ModifiedUtc = reader.GetInt64(6),
                            Root = reader.GetString(7)
                        });
                    }
                }
                return children;
            }

            public void Upsert(SearchEntry entry)
            {
                ThrowIfDone();
                if (entry == null || string.IsNullOrEmpty(entry.Path))
                    throw new ArgumentException("An entry with a path is required.", "entry");

                BindUpsert(_upsert, entry);
                _upsert.ExecuteNonQuery();
            }

            public void Delete(string path)
            {
                ThrowIfDone();
                if (string.IsNullOrEmpty(path)) return;

                _delete.Parameters["@path"].Value = path;
                _delete.ExecuteNonQuery();
            }

            public void DeletePathAndPrefix(string path)
            {
                ThrowIfDone();
                if (string.IsNullOrEmpty(path)) return;

                string exact;
                string start;
                string end;
                BuildPrefix(path, out exact, out start, out end);
                _deletePrefix.Parameters["@path"].Value = exact;
                _deletePrefix.Parameters["@start"].Value = start;
                _deletePrefix.Parameters["@end"].Value = end;
                _deletePrefix.ExecuteNonQuery();
            }

            public void Commit()
            {
                ThrowIfDone();

                // The timestamp the legacy CompleteReconcile wrote: ReadStatus keeps
                // reporting the last pass, so nothing downstream sees it as stale.
                SetMeta(_connection, _transaction, "last_reconcile_utc", UtcNowText());
                _transaction.Commit();
                _committed = true;
                Close();
            }

            public void Dispose()
            {
                Close();
            }

            private void ThrowIfDone()
            {
                if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(GetType().Name);
                if (_committed)
                    throw new InvalidOperationException("The reconcile session is already committed.");
            }

            private void Close()
            {
                // Exactly once, whichever thread gets here first: the gate is a counting
                // semaphore, so a second Release would silently widen the writer gate.
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;

                DisposeQuietly(_children);
                DisposeQuietly(_upsert);
                DisposeQuietly(_delete);
                DisposeQuietly(_deletePrefix);

                try
                {
                    // No commit means no pass: roll every write of this session back.
                    if (!_committed) _transaction.Rollback();
                }
                catch (Exception)
                {
                }

                try { _transaction.Dispose(); }
                catch (Exception) { }
                try { _connection.Dispose(); }
                catch (Exception) { }

                _store.EndReconcileSession();
            }

            private static void DisposeQuietly(IDisposable value)
            {
                if (value == null) return;
                try { value.Dispose(); }
                catch (Exception) { }
            }
        }
    }
}
