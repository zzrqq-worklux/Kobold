using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Kobold.FileSearchSpike
{
    /// <summary>
    /// Spike for the file-search widget. Proves Microsoft.Data.Sqlite + a non-bundled e_sqlite3
    /// works on net48, then measures 300k-row insert cost, prefix/infix query P95, an FTS5 trigram
    /// comparison, real directory-scan throughput and C:\ FileSystemWatcher stability.
    /// Touches the real filesystem (temp trees, a read-only scan of Documents, a C:\ watcher).
    /// Run: dotnet run --project tests/FileSearchSpike -c Release --nologo
    /// </summary>
    internal static class Program
    {
        private const int SyntheticRows = 300000;
        private const int QueryRounds = 20;
        private const int SyntheticDirs = 2000;
        private const int FilesPerDir = 10;
        private const int WatcherStableSeconds = 30;
        private const int WatcherProbeTimeoutMs = 10000;
        private const double TargetP95Ms = 300;
        private const long TargetDbBytes = 300L * 1024 * 1024;

        // Mixed selectivity on purpose: "txt"/"spike" hit every row, others hit a few or none.
        private static readonly string[] Keywords =
        {
            "100", "999", "spike", "txt", "25", "0", "5", "123", "888", "42",
            "24", "a", "z", "7", "33", "555", "77", "9", "15", "2500"
        };

        private static readonly byte[] Empty = new byte[0];

        private static int Main()
        {
            string tag = Guid.NewGuid().ToString("N");
            string temp = Path.GetTempPath();
            string dbPath = Path.Combine(temp, "kobold-spike-" + tag + ".db");
            string ftsDbPath = Path.Combine(temp, "kobold-spike-fts-" + tag + ".db");
            string scanRoot = Path.Combine(temp, "kobold-spike-scan-" + tag);
            string probeFile = Path.Combine(temp, "kobold-spike-watch-" + tag + ".txt");

            try
            {
                Console.WriteLine("spike_begin");
                Console.OutputEncoding = Encoding.UTF8;

                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                SQLitePCL.raw.FreezeProvider();

                Console.WriteLine("== provider ==");
                PrintProvider();

                Console.WriteLine();
                Console.WriteLine("== insert (" + SyntheticRows + " rows) ==");
                var insert = InsertPhase(dbPath);

                Console.WriteLine();
                Console.WriteLine("== queries (LIKE) ==");
                var like = QueryPhase(dbPath);

                Console.WriteLine();
                Console.WriteLine("== queries (FTS5 trigram) ==");
                var fts = FtsPhase(dbPath, ftsDbPath);

                Console.WriteLine();
                Console.WriteLine("== scan throughput ==");
                var synthetic = ScanPhase(scanRoot);
                var real = RealScanPhase();

                Console.WriteLine();
                Console.WriteLine("== C:\\ watcher ==");
                var watcher = WatcherPhase(probeFile);

                Console.WriteLine();
                Summary(insert, like, fts, synthetic, real, watcher);

                Cleanup(dbPath, ftsDbPath, scanRoot, probeFile);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("spike_error=" + Describe(ex));
                Console.WriteLine(ex.StackTrace);
                Cleanup(dbPath, ftsDbPath, scanRoot, probeFile);
                return 1;
            }
        }

        private static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        private static void PrintProvider()
        {
            Console.WriteLine("provider=Microsoft.Data.Sqlite.Core 8.0.30 + SQLitePCLRaw.provider.e_sqlite3 2.1.12");
            string native = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "e_sqlite3.dll");
            Console.WriteLine("native_dll=" + native);
            Console.WriteLine("native_dll_exists=" + File.Exists(native));
            if (File.Exists(native))
            {
                Console.WriteLine("native_dll_bytes=" + new FileInfo(native).Length);
            }
            Console.WriteLine("intptr_size=" + IntPtr.Size);
        }

        // ---------- insert ----------

        private sealed class InsertStats
        {
            public long InsertMs;
            public long DbBytes;
            public long WalBytes;
            public long PageBytes;
        }

        private static InsertStats InsertPhase(string dbPath)
        {
            var stats = new InsertStats();
            string cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                Console.WriteLine("sqlite_version=" + Scalar(c, "SELECT sqlite_version();"));
                Exec(c, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA synchronous=NORMAL;");
                Exec(c, @"CREATE TABLE files (Path TEXT PRIMARY KEY COLLATE NOCASE, Name TEXT NOT NULL,
                    NameLower TEXT NOT NULL, Extension TEXT NOT NULL, IsDirectory INTEGER NOT NULL,
                    SizeBytes INTEGER NOT NULL, ModifiedUtc INTEGER NOT NULL, Root TEXT NOT NULL);
                    CREATE INDEX idx_files_name_lower ON files(NameLower);");

                var sw = Stopwatch.StartNew();
                using (var tx = c.BeginTransaction())
                {
                    var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO files VALUES (@p,@n,@nl,@e,0,@s,@m,@r)";
                    for (int i = 0; i < SyntheticRows; i++)
                    {
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@p", @"C:\spike\" + i + ".txt");
                        cmd.Parameters.AddWithValue("@n", i + ".txt");
                        cmd.Parameters.AddWithValue("@nl", (i + ".txt").ToLowerInvariant());
                        cmd.Parameters.AddWithValue("@e", "txt");
                        cmd.Parameters.AddWithValue("@s", (long)i);
                        cmd.Parameters.AddWithValue("@m", 1700000000L + i);
                        cmd.Parameters.AddWithValue("@r", @"C:\");
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                stats.InsertMs = sw.ElapsedMilliseconds;
                stats.PageBytes = ToLong(Scalar(c, "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();"));
                Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            }

            SqliteConnection.ClearAllPools();
            stats.DbBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
            string wal = dbPath + "-wal";
            stats.WalBytes = File.Exists(wal) ? new FileInfo(wal).Length : 0;

            Console.WriteLine("insert_ms=" + stats.InsertMs);
            Console.WriteLine("db_bytes=" + stats.DbBytes);
            Console.WriteLine("wal_bytes=" + stats.WalBytes);
            Console.WriteLine("page_bytes=" + stats.PageBytes);
            return stats;
        }

        // ---------- LIKE queries ----------

        private sealed class QueryStats
        {
            public string Label;
            public bool Ok;
            public string Error;
            public int Samples;
            public double MinMs;
            public double MedianMs;
            public double P95Ms;
            public double MaxMs;
        }

        private sealed class LikeStats
        {
            public QueryStats Prefix = new QueryStats { Label = "prefix" };
            public QueryStats PrefixRange = new QueryStats { Label = "prefix_range" };
            public QueryStats Infix = new QueryStats { Label = "infix" };
            public QueryStats Infix3Plus = new QueryStats { Label = "infix_3plus" };
            public QueryStats Count = new QueryStats { Label = "count" };
            public string FirstInfixKeyword;
            public int FirstInfixRows;
            public string PlanPrefix;
            public string PlanInfix;
        }

        private sealed class Run
        {
            public double Ms;
            public int Rows;
        }

        private static LikeStats QueryPhase(string dbPath)
        {
            var stats = new LikeStats();
            string cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();

            const string prefixSql =
                "SELECT Path, Name FROM files WHERE NameLower LIKE @prefix ORDER BY NameLower LIMIT 300";
            const string prefixRangeSql =
                "SELECT Path, Name FROM files WHERE NameLower >= @lo AND NameLower <= @hi ORDER BY NameLower LIMIT 300";
            const string infixSql =
                "SELECT Path, Name FROM files WHERE NameLower LIKE @contains OR Path LIKE @contains ORDER BY Name LIMIT 300";
            const string countSql =
                "SELECT COUNT(*) FROM files WHERE NameLower LIKE @contains OR Path LIKE @contains";

            using (var c = new SqliteConnection(cs))
            {
                c.Open();

                stats.PlanPrefix = Plan(c, prefixSql, "@prefix", Keywords[0] + "%");
                stats.PlanInfix = Plan(c, infixSql, "@contains", "%" + Keywords[0] + "%");
                Console.WriteLine("plan_prefix=" + stats.PlanPrefix);
                Console.WriteLine("plan_infix=" + stats.PlanInfix);

                // Warm-up so the JIT/prepared-statement path does not land in the first sample.
                for (int i = 0; i < 3; i++)
                {
                    RunList(c, prefixSql, "@prefix", Keywords[0] + "%", null, null);
                    RunList(c, prefixRangeSql, "@lo", Keywords[0], "@hi", Keywords[0] + "\uFFFF");
                    RunList(c, infixSql, "@contains", "%" + Keywords[0] + "%", null, null);
                    ScalarLong(c, countSql, "@contains", "%" + Keywords[0] + "%");
                }

                var prefix = new List<double>();
                var prefixRange = new List<double>();
                var infix = new List<double>();
                var infix3Plus = new List<double>();
                var count = new List<double>();

                foreach (string kw in Keywords)
                {
                    for (int r = 0; r < QueryRounds; r++)
                    {
                        prefix.Add(RunList(c, prefixSql, "@prefix", kw + "%", null, null).Ms);
                        prefixRange.Add(RunList(c, prefixRangeSql, "@lo", kw, "@hi", kw + "\uFFFF").Ms);
                        var run = RunList(c, infixSql, "@contains", "%" + kw + "%", null, null);
                        infix.Add(run.Ms);
                        // Same keyword set the trigram phase can answer, so the P95s are comparable.
                        if (kw.Length >= 3) infix3Plus.Add(run.Ms);

                        if (stats.FirstInfixRows == 0)
                        {
                            stats.FirstInfixKeyword = kw;
                            stats.FirstInfixRows = run.Rows;
                        }

                        var sw = Stopwatch.StartNew();
                        ScalarLong(c, countSql, "@contains", "%" + kw + "%");
                        count.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }

                Fill(stats.Prefix, prefix);
                Fill(stats.PrefixRange, prefixRange);
                Fill(stats.Infix, infix);
                Fill(stats.Infix3Plus, infix3Plus);
                Fill(stats.Count, count);
            }

            PrintQuery(stats.Prefix);
            PrintQuery(stats.PrefixRange);
            PrintQuery(stats.Infix);
            PrintQuery(stats.Infix3Plus);
            PrintQuery(stats.Count);
            Console.WriteLine("infix_first_kw=" + stats.FirstInfixKeyword + " rows=" + stats.FirstInfixRows);
            return stats;
        }

        // ---------- FTS5 trigram ----------

        private sealed class FtsStats
        {
            public bool Available;
            public string Error;
            public long BuildMs;
            public long DbBytes;
            public int IndexedRows;
            public QueryStats Infix = new QueryStats { Label = "fts_infix" };
            public long LikeCount;
            public long FtsCount;
            public string ParityKeyword;
        }

        private static FtsStats FtsPhase(string filesDbPath, string ftsDbPath)
        {
            var stats = new FtsStats();
            string cs = new SqliteConnectionStringBuilder
            {
                DataSource = ftsDbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                using (var c = new SqliteConnection(cs))
                {
                    c.Open();
                    Exec(c, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA synchronous=NORMAL;");
                    Exec(c, "CREATE VIRTUAL TABLE files_fts USING fts5(Name, Path, tokenize='trigram');");

                    var sw = Stopwatch.StartNew();
                    Exec(c, "ATTACH DATABASE '" + filesDbPath.Replace("'", "''") + "' AS src;");
                    Exec(c, "INSERT INTO files_fts(Name, Path) SELECT Name, Path FROM src.files;");
                    Exec(c, "DETACH DATABASE src;");
                    stats.BuildMs = sw.ElapsedMilliseconds;
                    stats.IndexedRows = (int)ToLong(Scalar(c, "SELECT COUNT(*) FROM files_fts;"));

                    var samples = new List<double>();
                    string parity = "spike";
                    stats.LikeCount = ScalarLikeCount(filesDbPath, parity);
                    stats.FtsCount = ScalarLong(c, "SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH @q;", "@q", parity);
                    stats.ParityKeyword = parity;

                    for (int i = 0; i < 3; i++)
                    {
                        RunList(c, "SELECT Path, Name FROM files_fts WHERE files_fts MATCH @q ORDER BY Name LIMIT 300", "@q", parity, null, null);
                    }

                    foreach (string kw in Keywords)
                    {
                        if (kw.Length < 3) continue; // the trigram tokenizer cannot answer shorter queries
                        for (int r = 0; r < QueryRounds; r++)
                        {
                            samples.Add(RunList(c,
                                "SELECT Path, Name FROM files_fts WHERE files_fts MATCH @q ORDER BY Name LIMIT 300",
                                "@q", kw, null, null).Ms);
                        }
                    }

                    Fill(stats.Infix, samples);
                    stats.Available = true;
                }
            }
            catch (Exception ex)
            {
                stats.Available = false;
                stats.Error = ex.GetType().Name + ": " + ex.Message;
            }

            SqliteConnection.ClearAllPools();
            stats.DbBytes = File.Exists(ftsDbPath) ? new FileInfo(ftsDbPath).Length : 0;

            Console.WriteLine("fts5_trigram_available=" + stats.Available);
            if (!stats.Available)
            {
                Console.WriteLine("fts5_error=" + stats.Error);
                return stats;
            }
            Console.WriteLine("fts_build_ms=" + stats.BuildMs);
            Console.WriteLine("fts_indexed_rows=" + stats.IndexedRows);
            Console.WriteLine("fts_db_bytes=" + stats.DbBytes);
            Console.WriteLine("fts_count_parity=" + stats.ParityKeyword + ":" + stats.LikeCount + "=" + stats.FtsCount);
            PrintQuery(stats.Infix);
            return stats;
        }

        private static long ScalarLikeCount(string filesDbPath, string keyword)
        {
            string cs = new SqliteConnectionStringBuilder { DataSource = filesDbPath, Pooling = false }.ToString();
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                return ScalarLong(c,
                    "SELECT COUNT(*) FROM files WHERE NameLower LIKE @contains OR Path LIKE @contains;",
                    "@contains", "%" + keyword + "%");
            }
        }

        // ---------- scans ----------

        private sealed class ScanStats
        {
            public string Root;
            public long Files;
            public long Dirs;
            public long SkippedDirectories;   // directory could not be opened / enumerated
            public long SkippedReparsePoints; // reparse point pruned by the walker
            public long SkippedEntries;       // entry attributes unreadable (type unknown)
            public double Ms;
            public double EntriesPerSecond;
            public double CreateMs;

            public long Skipped
            {
                get { return SkippedDirectories + SkippedReparsePoints + SkippedEntries; }
            }
        }

        private static ScanStats ScanPhase(string scanRoot)
        {
            var stats = new ScanStats { Root = scanRoot };

            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(scanRoot);
            for (int d = 0; d < SyntheticDirs; d++)
            {
                string dir = Path.Combine(scanRoot, "d" + d.ToString("D4"));
                Directory.CreateDirectory(dir);
                for (int f = 0; f < FilesPerDir; f++)
                {
                    File.WriteAllBytes(Path.Combine(dir, "f" + f.ToString("D3") + ".txt"), Empty);
                }
            }
            stats.CreateMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine("created_entries=" + (SyntheticDirs + SyntheticDirs * FilesPerDir) +
                " create_ms=" + (long)stats.CreateMs);

            var walked = Walk(stats.Root);
            stats.Files = walked.Files;
            stats.Dirs = walked.Dirs;
            stats.SkippedDirectories = walked.SkippedDirectories;
            stats.SkippedReparsePoints = walked.SkippedReparsePoints;
            stats.SkippedEntries = walked.SkippedEntries;
            stats.Ms = walked.Ms;
            stats.EntriesPerSecond = walked.EntriesPerSecond;
            Console.WriteLine("scan_files=" + stats.Files);
            Console.WriteLine("scan_dirs=" + stats.Dirs);
            Console.WriteLine("scan_skipped_dirs=" + stats.SkippedDirectories);
            Console.WriteLine("scan_skipped_reparse=" + stats.SkippedReparsePoints);
            Console.WriteLine("scan_skipped_entries=" + stats.SkippedEntries);
            Console.WriteLine("scan_ms=" + (long)stats.Ms);
            Console.WriteLine("scan_entries_per_s=" + (long)stats.EntriesPerSecond);
            return stats;
        }

        private static ScanStats RealScanPhase()
        {
            string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            string target;
            if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents))
            {
                target = documents;
            }
            else
            {
                string local = Path.Combine(Environment.CurrentDirectory, "docs");
                target = Directory.Exists(local) ? local : AppDomain.CurrentDomain.BaseDirectory;
            }

            var walked = Walk(target);
            var stats = new ScanStats
            {
                Root = target,
                Files = walked.Files,
                Dirs = walked.Dirs,
                SkippedDirectories = walked.SkippedDirectories,
                SkippedReparsePoints = walked.SkippedReparsePoints,
                SkippedEntries = walked.SkippedEntries,
                Ms = walked.Ms,
                EntriesPerSecond = walked.EntriesPerSecond
            };
            Console.WriteLine("real_scan_root=" + stats.Root);
            Console.WriteLine("real_scan_files=" + stats.Files);
            Console.WriteLine("real_scan_dirs=" + stats.Dirs);
            Console.WriteLine("real_scan_skipped_dirs=" + stats.SkippedDirectories);
            Console.WriteLine("real_scan_skipped_reparse=" + stats.SkippedReparsePoints);
            Console.WriteLine("real_scan_skipped_entries=" + stats.SkippedEntries);
            Console.WriteLine("real_scan_ms=" + (long)stats.Ms);
            Console.WriteLine("real_scan_entries_per_s=" + (long)stats.EntriesPerSecond);
            return stats;
        }

        private static ScanStats Walk(string root)
        {
            var result = new ScanStats { Root = root };
            var stack = new Stack<DirectoryInfo>();
            var sw = Stopwatch.StartNew();

            if (Directory.Exists(root)) stack.Push(new DirectoryInfo(root));

            while (stack.Count > 0)
            {
                DirectoryInfo dir = stack.Pop();
                IEnumerator<FileSystemInfo> entries;
                try
                {
                    entries = dir.EnumerateFileSystemInfos().GetEnumerator();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    result.SkippedDirectories++;
                    continue;
                }

                try
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = entries.MoveNext();
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                        {
                            result.SkippedDirectories++;
                            break;
                        }
                        if (!moved) break;

                        FileSystemInfo entry = entries.Current;
                        try
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                result.SkippedReparsePoints++;
                                continue;
                            }
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                        {
                            result.SkippedEntries++;
                            continue;
                        }

                        var sub = entry as DirectoryInfo;
                        if (sub != null)
                        {
                            result.Dirs++;
                            stack.Push(sub);
                        }
                        else
                        {
                            result.Files++;
                        }
                    }
                }
                finally
                {
                    entries.Dispose();
                }
            }

            result.Ms = sw.Elapsed.TotalMilliseconds;
            double entriesSeen = result.Files + result.Dirs;
            result.EntriesPerSecond = result.Ms > 0 ? entriesSeen / (result.Ms / 1000.0) : 0;
            return result;
        }

        // ---------- C:\ watcher ----------

        private sealed class WatcherStats
        {
            public bool ProbeEvent;
            public bool Stable;
            public int Events;
            public int Errors;
            public string LastError;
        }

        private static WatcherStats WatcherPhase(string probeFile)
        {
            var stats = new WatcherStats();
            string tempProbe = Path.GetTempPath();
            int events = 0;
            int errors = 0;
            string lastError = null;

            try
            {
                using (var w = new FileSystemWatcher(@"C:\"))
                {
                    w.IncludeSubdirectories = true;
                    w.InternalBufferSize = 64 * 1024;
                    w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.Size | NotifyFilters.LastWrite;

                    var seen = new ManualResetEventSlim();
                    w.Created += (s, e) =>
                    {
                        Interlocked.Increment(ref events);
                        if (e.FullPath.StartsWith(tempProbe, StringComparison.OrdinalIgnoreCase)) seen.Set();
                    };
                    w.Error += (s, e) =>
                    {
                        Interlocked.Increment(ref errors);
                        lastError = e.GetException().Message;
                    };

                    w.EnableRaisingEvents = true;

                    File.WriteAllText(probeFile, "x");
                    stats.ProbeEvent = seen.Wait(WatcherProbeTimeoutMs);

                    Interlocked.Exchange(ref events, 0);
                    Thread.Sleep(WatcherStableSeconds * 1000);

                    stats.Events = Volatile.Read(ref events);
                    stats.Errors = Volatile.Read(ref errors);
                    stats.LastError = lastError;
                    stats.Stable = stats.ProbeEvent && stats.Errors == 0;
                }
            }
            catch (Exception ex)
            {
                stats.Stable = false;
                stats.LastError = ex.GetType().Name + ": " + ex.Message;
            }

            Console.WriteLine("watcher_c_root_probe_event=" + stats.ProbeEvent);
            Console.WriteLine("watcher_c_root_stable_" + WatcherStableSeconds + "s=" + stats.Stable);
            Console.WriteLine("watcher_events_" + WatcherStableSeconds + "s=" + stats.Events);
            Console.WriteLine("watcher_errors=" + stats.Errors);
            if (stats.LastError != null) Console.WriteLine("watcher_last_error=" + stats.LastError);
            return stats;
        }

        // ---------- summary ----------

        private static void Summary(InsertStats insert, LikeStats like, FtsStats fts,
            ScanStats synthetic, ScanStats real, WatcherStats watcher)
        {
            Console.WriteLine("== summary ==");
            Console.WriteLine("| 项目 | 结果 |");
            Console.WriteLine("|---|---|");
            Console.WriteLine("| provider / 版本 | Microsoft.Data.Sqlite.Core 8.0.30 + SQLitePCLRaw.provider.e_sqlite3 2.1.12 + SQLitePCLRaw.lib.e_sqlite3 2.1.12 |");
            Console.WriteLine("| native dll | runtimes\\win-x64\\native\\e_sqlite3.dll |");
            Console.WriteLine("| insert_" + SyntheticRows + "_ms | " + insert.InsertMs + " |");
            Console.WriteLine("| db_bytes | " + insert.DbBytes + " (" + Mb(insert.DbBytes) + " MB) |");
            Console.WriteLine("| prefix_p95_ms | " + Fmt(like.Prefix.P95Ms) + " |");
            Console.WriteLine("| prefix_range_p95_ms | " + Fmt(like.PrefixRange.P95Ms) + " |");
            Console.WriteLine("| infix_p95_ms | " + Fmt(like.Infix.P95Ms) + " |");
            Console.WriteLine("| infix_3plus_p95_ms | " + Fmt(like.Infix3Plus.P95Ms) + " |");
            Console.WriteLine("| count_p95_ms | " + Fmt(like.Count.P95Ms) + " |");
            if (fts.Available)
            {
                Console.WriteLine("| fts_build_ms | " + fts.BuildMs + " |");
                Console.WriteLine("| fts_db_bytes | " + fts.DbBytes + " (" + Mb(fts.DbBytes) + " MB) |");
                Console.WriteLine("| fts_infix_p95_ms | " + Fmt(fts.Infix.P95Ms) + " |");
            }
            else
            {
                Console.WriteLine("| fts5 | unavailable: " + fts.Error + " |");
            }
            Console.WriteLine("| scan_files | " + synthetic.Files + " |");
            Console.WriteLine("| scan_ms | " + (long)synthetic.Ms + " |");
            Console.WriteLine("| scan_entries_per_s | " + (long)synthetic.EntriesPerSecond + " |");
            Console.WriteLine("| real_scan_root | " + real.Root + " |");
            Console.WriteLine("| real_scan_files | " + real.Files + " |");
            Console.WriteLine("| real_scan_skipped_dirs | " + real.SkippedDirectories + " |");
            Console.WriteLine("| real_scan_skipped_reparse | " + real.SkippedReparsePoints + " |");
            Console.WriteLine("| real_scan_skipped_entries | " + real.SkippedEntries + " |");
            Console.WriteLine("| watcher_probe_event | " + watcher.ProbeEvent + " |");
            Console.WriteLine("| watcher_stable_" + WatcherStableSeconds + "s | " + watcher.Stable + " |");
            Console.WriteLine("| watcher_events_" + WatcherStableSeconds + "s | " + watcher.Events + " |");

            bool queryTargetOk = like.Prefix.Ok && like.Infix.Ok
                && like.Prefix.P95Ms < TargetP95Ms && like.Infix.P95Ms < TargetP95Ms;
            bool sizeTargetOk = insert.DbBytes < TargetDbBytes;
            Console.WriteLine("targets_ok=" + (queryTargetOk && sizeTargetOk));
            Console.WriteLine("targets_detail=query_p95_lt_" + TargetP95Ms + "ms:" + queryTargetOk
                + " db_lt_" + (TargetDbBytes / 1024 / 1024) + "MB:" + sizeTargetOk);
        }

        // ---------- helpers ----------

        private static void Exec(SqliteConnection c, string sql)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;
                cmd.ExecuteNonQuery();
            }
        }

        private static string Scalar(SqliteConnection c, string sql)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;
                object value = cmd.ExecuteScalar();
                return value == null ? "" : Convert.ToString(value);
            }
        }

        private static long ScalarLong(SqliteConnection c, string sql, string param, string value)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;
                cmd.Parameters.AddWithValue(param, value);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        private static Run RunList(SqliteConnection c, string sql, string p1, string v1, string p2, string v2)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;
                cmd.Parameters.AddWithValue(p1, v1);
                if (p2 != null) cmd.Parameters.AddWithValue(p2, v2);

                var sw = Stopwatch.StartNew();
                int rows = 0;
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        r.GetString(0);
                        r.GetString(1);
                        rows++;
                    }
                }
                return new Run { Ms = sw.Elapsed.TotalMilliseconds, Rows = rows };
            }
        }

        private static string Plan(SqliteConnection c, string sql, string param, string value)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                cmd.Parameters.AddWithValue(param, value);
                var sb = new StringBuilder();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (sb.Length > 0) sb.Append(" / ");
                        sb.Append(r.GetString(r.FieldCount - 1));
                    }
                }
                return sb.ToString();
            }
        }

        private static void Fill(QueryStats stats, List<double> samples)
        {
            stats.Samples = samples.Count;
            stats.Ok = samples.Count > 0;
            stats.Error = stats.Ok ? null : "no samples collected";
            if (!stats.Ok) return;

            var sorted = samples.OrderBy(v => v).ToList();
            stats.MinMs = sorted[0];
            stats.MaxMs = sorted[sorted.Count - 1];
            stats.MedianMs = sorted[sorted.Count / 2];
            int index = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Count) index = sorted.Count - 1;
            stats.P95Ms = sorted[index];
        }

        private static void PrintQuery(QueryStats stats)
        {
            if (!stats.Ok)
            {
                Console.WriteLine(stats.Label + "_unavailable=" + stats.Error);
                return;
            }
            Console.WriteLine(stats.Label + "_samples=" + stats.Samples);
            Console.WriteLine(stats.Label + "_min_ms=" + Fmt(stats.MinMs));
            Console.WriteLine(stats.Label + "_median_ms=" + Fmt(stats.MedianMs));
            Console.WriteLine(stats.Label + "_p95_ms=" + Fmt(stats.P95Ms));
            Console.WriteLine(stats.Label + "_max_ms=" + Fmt(stats.MaxMs));
        }

        private static string Fmt(double value)
        {
            return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static long ToLong(string value)
        {
            long parsed;
            return long.TryParse(value, out parsed) ? parsed : 0;
        }

        private static void Cleanup(string dbPath, string ftsDbPath, string scanRoot, string probeFile)
        {
            SqliteConnection.ClearAllPools();
            TryDelete(probeFile);
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
            TryDelete(ftsDbPath);
            TryDelete(ftsDbPath + "-wal");
            TryDelete(ftsDbPath + "-shm");
            if (Directory.Exists(scanRoot))
            {
                var sw = Stopwatch.StartNew();
                try { Directory.Delete(scanRoot, true); }
                catch (Exception ex) { Console.WriteLine("cleanup_scan_error=" + ex.Message); }
                Console.WriteLine("cleanup_scan_ms=" + sw.ElapsedMilliseconds);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("cleanup_error=" + path + " " + ex.Message);
            }
        }
    }
}
