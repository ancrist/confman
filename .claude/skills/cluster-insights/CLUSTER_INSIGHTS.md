# Cluster Performance Insights

Operational insights from Raft cluster benchmarking and stress testing.

## Insights

| # | Area | Insight | Impact | Fix/Note |
|---|------|---------|--------|----------|
| 1 | **Snapshot OOM** | `SerializeToUtf8Bytes` allocates the entire JSON as a single contiguous `byte[]`. With 500 x 1MB configs, the JSON exceeds 1GB — impossible to allocate | `OutOfMemoryException` during snapshot on leader election or WAL compaction | Stream through temp file: `SerializeAsync(fileStream)` then `writer.CopyFromAsync(readStream)`. Peak memory drops from ~1.5GB to ~500MB |
| 2 | **Snapshot Restore** | `File.ReadAllBytesAsync` loads entire snapshot into a single `byte[]` before deserializing | OOM on follower when restoring large snapshots | `JsonSerializer.DeserializeAsync(stream)` reads incrementally from disk |
| 3 | **Kestrel Body Limit** | Raft AppendEntries RPC bundles multiple log entries into one HTTP request. With large values, the payload exceeds Kestrel's default 30MB `MaxRequestBodySize` | `BadHttpRequestException: Request body too large` during replication | `options.Limits.MaxRequestBodySize = null` — safe for internal cluster service |
| 4 | **WAL Bloat** | `SnapshotInterval: 1000` means the WAL never compacts during a 1000-entry benchmark. With 1MB entries, the WAL grows to ~1.3GB across ~167 chunk files (8MB each). Operations degrade as chunk count increases | Progressive latency growth: 22ms → 128ms → 611ms → 2175ms | Reduced `SnapshotInterval` from 1000 to 100 — compacts every ~130MB instead of ~1.3GB |
| 5 | **Flush Interval** | WAL batches fsyncs every `FlushIntervalMs`. Sequential writes must wait for the next cycle. With 500ms interval, average wait is ~250ms | 268ms average latency for sequential 1MB writes — mostly waiting for fsync | Reduced `FlushIntervalMs` from 500 to 100 |
| 6 | **Snapshot Starvation** | `PersistAsync` runs on the state machine thread. While creating a 500MB+ snapshot (~700ms), the node can't respond to Raft heartbeats. Election timeout (150-300ms) < snapshot time (700ms) | Spurious elections, cluster flapping, "No leader in cluster" during heavy writes | Increased election timeout to 1000-3000ms, request timeout to 10s |
| 7 | **Snapshot Scaling** | Snapshots are O(total data), not O(delta). Every snapshot serializes the entire dataset. With N x 1MB configs, each snapshot writes ~N MB to disk | Each snapshot gets more expensive as data grows. Snapshot at 700 entries = 733MB to serialize | Architectural limit of full-state snapshot model — awareness only |
| 8 | **Raft Timeout Invariant** | The relationship `snapshot_time < election_timeout < request_timeout` must hold. Snapshot time ≈ 1ms per config entry | Violating cascades: missed heartbeats → election → disrupted replication → larger AppendEntries → request timeout → follower marked unavailable | Documented tuning guidelines by payload size (see below) |
| 9 | **Shutdown Race** | ASP.NET DI disposes `LiteDbConfigStore` (and its `SemaphoreSlim`) while Raft state machine is still applying pending log entries | `ObjectDisposedException` on `SemaphoreSlim.Release()` during Ctrl+C | `_dbSemaphore.Wait()` in `Dispose()` drains in-flight operations before disposing |
| 10 | **Write Amplification** | 1000 x 1MB configs produces ~10 GB disk I/O per node (~10x amplification). Three layers stack: Raft WAL (~1.5 GB), LiteDB + journal (~2 GB), full-state snapshots (~5.5 GB cumulative). Snapshot I/O dominates: total bytes = sum(100+200+...+1000) MB | Sustained high disk I/O during bulk writes. SSD wear concern for large-value production clusters | Architectural limit — snapshot I/O grows O(n²) with entry count. Incremental snapshots would reduce to O(n) but add complexity. Acceptable for config-sized workloads |
| 11 | **Flush Interval vs Throughput** | `FlushIntervalMs` controls the throughput/latency tradeoff for batched writes. A longer interval collects more commands per fsync cycle: at 100ms ~2 commands batch per flush (~17 req/s with 1MB payloads), at 500ms ~25 commands batch per flush (~50 req/s). Formula: throughput ≈ batch_size / flush_interval. Concurrent writers are required to see the benefit | Higher flush interval = higher throughput but worse tail latency. First command in a batch waits nearly the full interval; last command barely waits. All commands in a batch commit together | Tunable per deployment: 100ms for low-latency interactive use, 500ms+ for bulk imports/migrations. Not a code change — purely a config knob (`Raft:FlushIntervalMs` in appsettings.json) |
| 12 | **MessagePack + LZ4** | Replacing JSON with MessagePack-CSharp (`Lz4BlockArray`) for Raft command serialization and snapshots. LZ4 compresses repetitive payloads aggressively — WAL stays 8-12 MB for 1 GB of raw data (low-entropy test payloads). Benchmarks: 10KB→353 req/s, 100KB→46.9 req/s, 1MB→4.9 req/s (10 concurrent writers). Disk per node: 10KB/1000=27 MB, 100KB/1000=128 MB, 1MB/1000=942 MB. WAL is negligible; LiteDB (uncompressed BSON) dominates disk usage | WAL I/O reduced by orders of magnitude for compressible payloads. Write amplification drops from ~10x (JSON) to ~1x at 1MB scale since WAL shrinks to <12 MB. At 1MB/1000 entries, 91/1000 failures from snapshot starvation (same as insight #6 — full-state snapshot of 800+ MB exceeds 10s replication timeout) | MessagePack 3.1.4 NuGet. `Serialize<ICommand>` (not concrete type) to preserve Union discriminator. `Lz4BlockArray` (not `Lz4Block`) for >1.7 GB payloads. Skip leading `0x00` bytes in WAL deserialization (DotNext padding). Real JSON configs will see 3-5x compression (not 85x like test patterns) |
| 13 | **LOH & GC Compaction** | Processing thousands of 1MB+ entries fragments the .NET Large Object Heap (objects >85KB). Three hotspots: `payload.ToArray()` in `DeserializeCommand`, `MessagePackSerializer.Serialize()` returning `byte[]`, and snapshot creation loading all configs into memory. Server GC never runs Gen 2 collection during idle periods — without allocation pressure, dead LOH objects stay resident indefinitely. Measured: 68 MB of actual data consumed 17-29 GB RSS per node (250-425x bloat) | Memory grows unbounded during bursty writes and never reclaims. Nodes that were idle for hours still held 17+ GB. In a 3-node cluster with 1000 x 1MB entries, aggregate RSS exceeded 60 GB for data that should fit in ~500 MB | Five-layer fix: (1) `SequenceReader<byte>` + `ReadOnlySequence.Slice()` eliminates `payload.ToArray()`, (2) pooled `ArrayBufferWriter<byte>` reused across batches in `BatchingRaftService`, (3) scoped `CreateAndStreamSnapshotAsync` ensures snapshot data leaves scope before GC, (4) `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` + `GC.Collect(Aggressive)` after each snapshot in `PersistAsync`, (5) 60-second periodic GC timer triggers blocking Gen 2 + LOH compaction when heap > 500 MB. Result: peak 3-6 GB during writes, reclaims to 450-600 MB within 90 seconds of idle. DATAS GC mode (`GarbageCollectionAdaptationMode=1`) and `RetainVMGarbageCollection=false` further help the runtime release empty heap segments to the OS |

## Tuning Guidelines by Payload Size

| Payload Size | SnapshotInterval | Election Timeout | Request Timeout | Notes |
|-------------|-----------------|-----------------|----------------|-------|
| 1-10 KB     | 1000            | 150-300 ms      | 2s             | Default — works well |
| 10-100 KB   | 500             | 500-1500 ms     | 5s             | Moderate payloads |
| 100 KB-1 MB | 100             | 1000-3000 ms    | 10s            | Large payloads, stress test territory |
| > 1 MB      | 50              | 2000-5000 ms    | 30s            | Extreme — consider external blob storage |

## Write Amplification Breakdown (1000 x 1MB benchmark)

### Before (JSON)

| Layer | Estimated I/O | Why |
|-------|--------------|-----|
| Raft WAL | ~1.5 GB | Each 1MB entry written to WAL with framing. ChunkSize=8MB, ~130 chunks |
| LiteDB + journal | ~2 GB | State machine applies each entry. `Connection=direct` WAL journal doubles writes |
| Snapshots | ~5.5 GB | Full-state every 100 entries: 100+200+...+1000 MB = 5500 MB |
| **Total per node** | **~10 GB** | **~10x write amplification** |

### After (MessagePack + LZ4)

| Layer | Measured | Why |
|-------|---------|-----|
| Raft WAL | ~12 MB | LZ4 compresses repetitive test payloads ~85x. Real JSON configs expect 3-5x |
| LiteDB | ~919 MB | Uncompressed BSON — dominates disk usage |
| **Total per node** | **~942 MB** | **~0.94x write amplification** (test payloads) |

Note: LZ4 compression ratio depends heavily on payload entropy. Test payloads (repeating `ABCDEFG...`) compress ~85x. Real JSON configs with structural repetition expect ~3-5x. LiteDB stores values uncompressed regardless.

The leader node shows slightly higher read I/O (~32 MB vs ~11 MB) because it reads LiteDB pages to build snapshots and serves AppendEntries to followers.
