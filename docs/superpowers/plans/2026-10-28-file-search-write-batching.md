# File Search Write Batching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raise file-search write throughput from the current ~80 rows/s without reducing indexed coverage, excluding user folders, or requiring administrator rights.

**Architecture:** Keep the existing directory-walk indexer. Batch every live upsert produced by the watcher and by a directory-rename subtree scan into `ISearchIndexStore.UpsertScanBatch`, so one SQLite connection/transaction covers up to 800 rows instead of one connection/transaction per row. Preserve event ordering by flushing the pending upsert buffer before any delete/rename. Tune the write connection for bulk work. No schema or coverage change.

**Tech Stack:** C# 7.3, .NET Framework 4.8, WPF, Microsoft.Data.Sqlite (SQLitePCLRaw e_sqlite3), console self-checks in `tests/FileSearchCheck`.

**Spec:** Diagnosis in this session: `FileSystemWatcherHost.ApplyBatch` calls `ApplyEvent` per event; `ApplyEvent`/`ScanSubtree` call `ISearchIndexStore.Upsert` per row, which opens a connection and commits per row. Measured ~80 new rows/s while a rename subtree replay ran. `DirectoryScanner.Add` bypasses batching when `live=true`.

## Global Constraints

- No new default exclusions and no change to `FileSearchSettings` coverage. WXWork/auto-saved files must stay searchable.
- No MFT/USN or other administrator-only API. The app runs as the logged-in user.
- Keep `ISearchIndexStore` unchanged; reuse `UpsertScanBatch` (it writes to `files` when `_fullScanActive == false`, which is the live path).
- Keep event order: a pending upsert batch must be flushed before a `Delete`/`DeletePathAndPrefix`/`Rename` that follows it.
- Cancellation and error behavior must remain: a failed batch must not kill the watcher worker; it asks for a reconcile as today.
- All existing `FileSearchCheck` pins must stay green.

---

### Task 1: Pin the current per-row write behavior with failing checks

**Files:**
- Modify: `tests/FileSearchCheck/Program.cs` (add `WatcherBatchWriteCases`, `ScannerSubtreeBatchCase`, call both from `RunAll`)
- Modify: `tests/FileSearchCheck/Program.cs` test fake `DiffStore` (record batched entries and calls)

**Interfaces:**
- Consumes: `FileSystemWatcherHost.ApplyBatchForChecks(IReadOnlyList<WatcherEvent>)`, `DirectoryScanner.ScanSubtree(string root, string directory, CancellationToken)`, `DiffStore`
- Produces: `DiffStore.Batched` (`List<SearchEntry>`), `DiffStore.BatchCalls` (`int`), and updated `UpsertFor`/`CountUpserts` that search both `Upserts` and `Batched`.

- [ ] **Step 1: Extend the test fake**

In `DiffStore`, add:

```csharp
public readonly List<SearchEntry> Batched = new List<SearchEntry>();
public int BatchCalls;

public void UpsertScanBatch(IReadOnlyList<SearchEntry> entries)
{
    if (entries == null) return;
    BatchCalls++;
    BatchedRows += entries.Count;
    Batched.AddRange(entries);
}
```

Update `UpsertFor` and `CountUpserts` to search `store.Upserts` and `store.Batched`.

- [ ] **Step 2: Write the failing watcher check**

Add to `RunAll` after `WatcherStatDietCases()`:

```csharp
WatcherBatchWriteCases();
```

Add:

```csharp
/// <summary>
/// A burst of Created events must reach the store as one live batch, not one
/// connection/commit per file. Regression pin for the ~80 rows/s replay.
/// </summary>
private static void WatcherBatchWriteCases()
{
    string root = Path.Combine(_root, "watch-batch-root");
    Directory.CreateDirectory(root);

    var events = new List<FileSystemWatcherHost.WatcherEvent>();
    for (int i = 0; i < 100; i++)
    {
        string path = Path.Combine(root, "zebrawatch-batch-" + i.ToString("D3") + ".txt");
        File.WriteAllText(path, "x");
        events.Add(WatchEvent(FileSystemWatcherHost.WatcherEventKind.Created, path));
    }

    var store = new DiffStore();
    using (var host = new FileSystemWatcherHost(store, SettingsForRoot(root)))
    {
        host.Start(new[] { root });
        host.ApplyBatchForChecks(events);
    }

    Check(store.BatchCalls == 1, "search batch: one watcher burst writes one live transaction");
    Check(store.Batched.Count == 100, "search batch: every created event is in the live batch");
    Check(store.Upserts.Count == 0, "search batch: a created event does not open a per-row write");
}
```

Create all files before `Start` so the real watcher does not add its own events; `ApplyBatchForChecks` then applies exactly the injected list.

- [ ] **Step 3: Write the failing subtree check**

Add to `RunAll` after `ScannerSubtreeCases()`:

```csharp
ScannerSubtreeBatchCase();
```

Add:

```csharp
/// <summary>
/// A directory rename rescan must batch its live rows. One start-directory row
/// may stay individual; the files must not each open a connection.
/// </summary>
private static void ScannerSubtreeBatchCase()
{
    string tree = Path.Combine(_root, "subtree-batch-tree");
    string docs = Path.Combine(tree, "docs");
    Directory.CreateDirectory(docs);
    for (int i = 0; i < 100; i++)
        File.WriteAllText(Path.Combine(docs, "file" + i.ToString("D3") + ".txt"), "x");

    var settings = FileSearchSettings.CreateDefault();
    settings.Roots = new[] { tree };
    var store = new DiffStore();
    var result = new DirectoryScanner(store, settings).ScanSubtree(tree, docs, CancellationToken.None);

    Check(result.Files == 100, "subtree batch: every file is counted");
    Check(store.BatchCalls == 1, "subtree batch: the live subtree writes one transaction");
    Check(store.Batched.Count == 100, "subtree batch: every file is in the live batch");
    Check(store.Upserts.Count == 1, "subtree batch: only the start directory row is written individually");
}
```

- [ ] **Step 4: Run the checks and watch them fail**

Run:

```powershell
dotnet run --project tests/FileSearchCheck/FileSearchCheck.csproj
```

Expected: the two new `search batch` / `subtree batch` checks fail; `BatchCalls` is 0 and `Batched.Count` is 0 because the code still calls `Upsert` per row. Existing checks stay green.

---

### Task 2: Batch live watcher upserts

**Files:**
- Modify: `Core/FileSearch/FileSystemWatcherHost.cs`

**Interfaces:**
- Consumes: `ISearchIndexStore.UpsertScanBatch(IReadOnlyList<SearchEntry>)`
- Produces: private `List<SearchEntry>` pending buffer in `ApplyBatch`; `TryQueueUpsert` replaces the write side of `TryUpsert`.

- [ ] **Step 1: Refactor the upsert helper**

Replace `TryUpsert(string root, string path, out bool isDirectory)` with:

```csharp
/// <summary>
/// Stats one path and, when it is indexable, queues it for the batch writer.
/// False means nothing was queued; <paramref name="isDirectory"/> tells the
/// caller whether the path still exists as a directory (so a vanished file
/// falls through to the prefix delete, but a folder event does not).
/// </summary>
private bool TryQueueUpsert(string root, string path, List<SearchEntry> pending, out bool isDirectory)
{
    isDirectory = false;

    FileSystemInfo info;
    try { info = new FileInfo(SearchPath.ToExtended(path)); }
    catch (Exception) { return false; }

    SearchEntry entry;
    if (!SearchEntry.TryCreate(root, info, out entry) || entry == null) return false;

    isDirectory = entry.IsDirectory;
    if (isDirectory && !_settings.IndexFolders) return false;

    pending.Add(entry);
    return true;
}
```

Keep the type/folder gate in `ApplyEvent` exactly as it is today before calling this helper.

- [ ] **Step 2: Buffer Created/Changed, flush before deletes/renames**

In `ApplyBatch`, create one `var pending = new List<SearchEntry>();` per batch and pass it to `ApplyEvent`. In `ApplyEvent`:

```csharp
if (item.Kind == WatcherEventKind.Deleted)
{
    FlushPending(pending);
    if (InScope(path)) _store.DeletePathAndPrefix(path);
    return;
}

if (item.Kind == WatcherEventKind.Renamed)
{
    FlushPending(pending);
    ApplyRename(item, path, token);
    return;
}
```

For Created/Changed, call `TryQueueUpsert(root, path, pending, out isDirectory)`; if it returns false and `!isDirectory`, flush first, then `_store.DeletePathAndPrefix(path)`.

Add:

```csharp
private void FlushPending(List<SearchEntry> pending)
{
    if (pending.Count == 0) return;
    List<SearchEntry> batch = new List<SearchEntry>(pending);
    pending.Clear();
    _store.UpsertScanBatch(batch);
}
```

Call `FlushPending(pending)` after the event loop (including in the `finally` before `_activeBatches--`? No: flush inside the `try` after the loop so a failed flush is reported by the existing catch; do not flush in `finally` or a failure could mask the original exception). If the loop ends normally, flush; if an exception interrupts it, the next reconcile repairs.

For `ApplyRename` file branch, call `TryQueueUpsert(..., pending, out _)`; the caller flushes before `ApplyRename`, and `ApplyBatch` flushes after the loop. For a directory rename, `ScanSubtree` handles its own batching.

- [ ] **Step 3: Run the checks**

Run:

```powershell
dotnet run --project tests/FileSearchCheck/FileSearchCheck.csproj
```

Expected: the watcher batch check passes; all existing watcher pins pass.

---

### Task 3: Batch live subtree scans

**Files:**
- Modify: `Core/FileSearch/DirectoryScanner.cs`

**Interfaces:**
- Consumes: `ISearchIndexStore.UpsertScanBatch(IReadOnlyList<SearchEntry>)`
- Produces: `Flush` gains a `live` parameter and uses the same batch call for live rows.

- [ ] **Step 1: Buffer live rows**

In `Add`, replace the `live` branch:

```csharp
if (live)
{
    // ScanSubtree: the watcher needs the rows, but not one commit per file.
    // BatchSize rows per transaction is the difference between ~80 rows/s
    // and thousands; the caller flushes the remainder when the walk ends.
    state.Batch.Add(entry);
    if (state.Batch.Count >= BatchSize) Flush(state, mode, live, token);
    return;
}
```

Update both `Flush` call sites (the `BatchSize` flush in `Add` and the final flush in `WalkCore`) to pass `live`.

Update `Flush`:

```csharp
private void Flush(ScanState state, ScanMode mode, bool live, CancellationToken token)
{
    if (state.Batch.Count == 0) return;

    token.ThrowIfCancellationRequested();

    _store.UpsertScanBatch(state.Batch);
    if (!live && mode == ScanMode.Reconcile) _store.MarkSeenBatch(state.Seen);

    state.Batch.Clear();
    state.Seen.Clear();
}
```

`UpsertScanBatch` writes to `files` when no full scan is active, which is the live path; a full scan never runs while the watcher is active.

- [ ] **Step 2: Run the checks**

Run:

```powershell
dotnet run --project tests/FileSearchCheck/FileSearchCheck.csproj
```

Expected: the subtree batch check passes; all scanner/reconcile/watch checks pass.

---

### Task 4: Tune the SQLite write path and verify the full suite

**Files:**
- Modify: `Core/FileSearch/SearchIndexStore.cs`

**Interfaces:**
- Consumes: SQLite PRAGMAs only; no API change.

- [ ] **Step 1: Use a private cache and a larger page cache**

In `OpenWrite`, change `Cache = SqliteCacheMode.Shared` to `Cache = SqliteCacheMode.Private` (WAL readers already use private; private avoids shared-cache table locks). After `busy_timeout`, add:

```csharp
Execute(connection, "PRAGMA cache_size=-32768;");
Execute(connection, "PRAGMA temp_store=MEMORY;");
Execute(connection, "PRAGMA wal_autocheckpoint=4000;");
```

Keep `synchronous=NORMAL` unchanged: the index is rebuildable, but NORMAL still protects the file from corruption.

- [ ] **Step 2: Run the full check group**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Run-Checks.ps1 -Group checks
```

Expected: all checks pass, including `FileSearchCheck`.

- [ ] **Step 3: Measure the live path**

Run the app's own `FileSearchCheck`; optionally add a temporary stopwatch around the two new cases and report rows/s. Do not commit a timing threshold; CI machines vary.

---

### Task 5: Report and hand off

- [ ] **Step 1: Summarize the before/after**

Report: current measured ~80 rows/s; after batching, the same 100-event burst is one transaction and the 100-file subtree is one transaction. The real replay of 13k WXWork cache rows should drop from ~3 minutes to seconds. A full rebuild still walks the drive; without MFT/USN it cannot match Everything's seconds, but it no longer pays per-row commit cost.

- [ ] **Step 2: Do not commit unless the user asks**

Leave the working tree changes for review. If the user asks to commit, use a conventional message such as `perf(file-search): batch watcher and subtree writes`.
