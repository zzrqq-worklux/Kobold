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

        private const string CreateFilesSql = @"
CREATE TABLE IF NOT EXISTS files (
    Path TEXT PRIMARY KEY COLLATE NOCASE,
    Name TEXT NOT NULL,
    NameLower TEXT NOT NULL,
    Extension TEXT NOT NULL,
    IsDirectory INTEGER NOT NULL,
    SizeBytes INTEGER NOT NULL,
    ModifiedUtc INTEGER NOT NULL,
    Root TEXT NOT NULL);";

        private const string CreateMetaSql = @"
CREATE TABLE IF NOT EXISTS meta (Key TEXT PRIMARY KEY, Value TEXT);";

        private const string CreateIndexesSql = @"
CREATE INDEX IF NOT EXISTS idx_files_name_lower ON files(NameLower);
CREATE INDEX IF NOT EXISTS idx_files_extension ON files(Extension);
CREATE INDEX IF NOT EXISTS idx_files_root ON files(Root);";

        private const string CreateStagingSql = @"
CREATE TABLE files_new (
    Path TEXT PRIMARY KEY COLLATE NOCASE,
    Name TEXT NOT NULL,
    NameLower TEXT NOT NULL,
    Extension TEXT NOT NULL,
    IsDirectory INTEGER NOT NULL,
    SizeBytes INTEGER NOT NULL,
    ModifiedUtc INTEGER NOT NULL,
    Root TEXT NOT NULL);";

        private const string CreateSeenSql = @"
CREATE TABLE reconcile_seen (Path TEXT PRIMARY KEY COLLATE NOCASE);";

        // {0} is "files" or "files_new"; both are internal constants, never user input.
        private const string UpsertSql = @"
INSERT INTO {0} (Path, Name, NameLower, Extension, IsDirectory, SizeBytes, ModifiedUtc, Root)
VALUES (@path, @name, @nameLower, @extension, @isDirectory, @sizeBytes, @modifiedUtc, @root)
ON CONFLICT(Path) DO UPDATE SET
    Name = excluded.Name,
    NameLower = excluded.NameLower,
    Extension = excluded.Extension,
    IsDirectory = excluded.IsDirectory,
    SizeBytes = excluded.SizeBytes,
    ModifiedUtc = excluded.ModifiedUtc,
    Root = excluded.Root;";

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

        private readonly object _writeGate = new object();
        private readonly string _databasePath;

        private bool _fullScanActive;
        private bool _reconcileActive;
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
                lock (_writeGate)
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

        public void EnsureSchema()
        {
            lock (_writeGate)
            {
                ThrowIfDisposed();

                string directory = Path.GetDirectoryName(_databasePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using (var connection = OpenWrite())
                {
                    ExecuteScalar(connection, "PRAGMA journal_mode=WAL;");
                    Execute(connection, "PRAGMA user_version=1;");

                    Execute(connection, CreateFilesSql);
                    Execute(connection, CreateMetaSql);
                    Execute(connection, CreateIndexesSql);

                    // Read before the transaction: SqliteCommand requires an enlisted
                    // transaction for every command while one is open.
                    string sqliteVersion = Convert.ToString(
                        ExecuteScalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture);

                    using (var transaction = connection.BeginTransaction())
                    {
                        // Recorded so a bug report can tell which native library shipped.
                        SetMeta(connection, transaction, "sqlite_version", sqliteVersion);
                        SetMeta(connection, transaction, "schema_version", "1");
                        transaction.Commit();
                    }
                }
            }
        }

        public void BeginFullScan()
        {
            lock (_writeGate)
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

            lock (_writeGate)
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
            lock (_writeGate)
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
            lock (_writeGate)
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
            lock (_writeGate)
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

            lock (_writeGate)
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

        public void CompleteReconcile(IReadOnlyList<string> roots)
        {
            lock (_writeGate)
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
            lock (_writeGate)
            {
                ThrowIfDisposed();

                using (var connection = OpenWrite())
                {
                    Execute(connection, "DROP TABLE IF EXISTS reconcile_seen;");
                }
                _reconcileActive = false;
            }
        }

        public void Upsert(SearchEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Path))
                throw new ArgumentException("An entry with a path is required.", "entry");

            lock (_writeGate)
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

            lock (_writeGate)
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

            lock (_writeGate)
            {
                ThrowIfDisposed();

                // A directory delete removes the row itself and its whole subtree. The
                // trailing separator is stripped so "C:\p\sub\" also matches the stored
                // "C:\p\sub" row, while the pattern keeps it escaped: C:\p\sub never
                // claims C:\p\submarine. A drive root keeps its separator.
                string directory = path.TrimEnd('\\');
                if (directory.Length == 2 && directory[1] == ':') directory += "\\";
                string exact = directory.Length == 0 ? path : directory;
                string prefix = SearchPath.EscapeLike(exact.TrimEnd('\\') + "\\") + "%";

                using (var connection = OpenWrite())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"DELETE FROM files WHERE Path = @path OR Path LIKE @prefix ESCAPE '\';";
                    command.Parameters.AddWithValue("@path", exact);
                    command.Parameters.AddWithValue("@prefix", prefix);
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
            lock (_writeGate)
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
            lock (_writeGate)
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
    }
}
