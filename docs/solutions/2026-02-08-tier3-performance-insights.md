---
title: "Tier 3 Performance Insights — Large Payload Stress Testing"
category: performance
tags: [raft, snapshot, wal, litedb, large-payloads, election-timeout, kestrel, oom, performance]
module: Confman.Api
date: 2026-02-08
---

# Tier 3 Performance Insights

Insights gathered during Tier 3 write-batching implementation and 1MB payload stress testing.

| # | Area | Insight | Impact | Fix Applied |
|---|------|---------|--------|-------------|
| 1 | **Snapshot OOM** | `SerializeToUtf8Bytes` allocates the entire JSON as a single contiguous `byte[]`. With 500 x 1MB configs, the JSON exceeds 1GB -- impossible to allocate. | `OutOfMemoryException` during snapshot on leader election or WAL compaction | Stream through temp file: `SerializeAsync(fileStream)` then `writer.CopyFromAsync(readStream)`. Peak memory drops from ~1.5GB to ~500MB |
| 2 | **Snapshot Restore** | `File.ReadAllBytesAsync` has the same problem -- loads entire snapshot into a single `byte[]` before deserializing | OOM on follower when restoring large snapshots | `JsonSerializer.DeserializeAsync(stream)` reads incrementally from disk |
| 3 | **Kestrel Body Limit** | Raft's AppendEntries RPC bundles multiple log entries into one HTTP request. With large values, the payload easily exceeds Kestrel's default 30MB `MaxRequestBodySize` | `BadHttpRequestException: Request body too large` during replication | `options.Limits.MaxRequestBodySize = null` -- safe for internal cluster service |
| 4 | **WAL Bloat** | `SnapshotInterval: 1000` means the WAL never compacts during a 1000-entry benchmark. With 1MB entries, the WAL grows to ~1.3GB across ~167 chunk files (8MB each). Operations degrade as chunk count increases | Progressive latency growth: 22ms, 128ms, 611ms, 2175ms | Reduced `SnapshotInterval` from 1000 to 100 -- compacts every ~130MB instead of ~1.3GB |
| 5 | **Flush Interval** | WAL batches fsyncs every `FlushIntervalMs`. Great for concurrent writes (amortizes I/O), but sequential writes must wait for the next cycle. With 500ms interval, average wait is ~250ms | 268ms average latency for sequential 1MB writes -- mostly waiting for fsync, not doing useful work | Reduced `FlushIntervalMs` from 500 to 100 |
| 6 | **Snapshot Starvation** | `PersistAsync` runs on the state machine thread. While creating a 500MB+ snapshot (~700ms), the node can't respond to Raft heartbeats. Election timeout (150-300ms) < snapshot time (700ms) means follower starts election | Spurious elections, cluster flapping, "No leader in cluster" during heavy writes | Increased election timeout to 1000-3000ms, request timeout to 10s |
| 7 | **Snapshot Scaling** | Snapshots are O(total data), not O(delta). Every snapshot serializes the entire dataset. With N x 1MB configs, each snapshot writes ~N MB to disk | Each snapshot gets more expensive as data grows. Snapshot at 700 entries = 733MB to serialize and write | Architectural limit of full-state snapshot model -- no code fix, just awareness |
| 8 | **Raft Timeout Invariant** | The relationship `snapshot_time < election_timeout < request_timeout` must hold. Snapshot time is roughly 1ms per config entry, which gives a planning formula | Violating this invariant cascades: missed heartbeats, election, disrupted replication, larger AppendEntries, request timeout, follower marked unavailable | Documented tuning guidelines by payload size in `docs/solutions/` |
| 9 | **Shutdown Race** | ASP.NET DI disposes `LiteDbConfigStore` (and its `SemaphoreSlim`) while the Raft state machine is still applying pending log entries | `ObjectDisposedException` on `SemaphoreSlim.Release()` during Ctrl+C | `_dbSemaphore.Wait()` in `Dispose()` drains in-flight operations before disposing |

## Tuning Guidelines by Payload Size

| Payload Size | SnapshotInterval | Election Timeout | Request Timeout | Notes |
|-------------|-----------------|-----------------|----------------|-------|
| 1-10 KB     | 1000            | 150-300 ms      | 2s             | Default -- works well |
| 10-100 KB   | 500             | 500-1500 ms     | 5s             | Moderate payloads |
| 100 KB-1 MB | 100             | 1000-3000 ms    | 10s            | Large payloads, stress test territory |
| > 1 MB      | 50              | 2000-5000 ms    | 30s            | Extreme -- consider external blob storage |

## Files Changed

| File | Change |
|------|--------|
| `src/Confman.Api/Cluster/ConfigStateMachine.cs` | Streaming snapshot serialization/restore |
| `src/Confman.Api/Program.cs` | Kestrel `MaxRequestBodySize = null` |
| `src/Confman.Api/Storage/LiteDbConfigStore.cs` | Graceful shutdown via semaphore drain |
| `src/Confman.Api/appsettings.json` | Tuned `SnapshotInterval`, `FlushIntervalMs`, election/request timeouts |
