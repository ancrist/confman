---
title: "perf: Write throughput Tier 1 quick wins"
type: perf
date: 2026-02-05
---

# perf: Write Throughput Tier 1 Quick Wins

## Overview

Implement four low-complexity, high-impact write performance optimizations identified from research across DotNext documentation, distributed KV store literature (etcd, TiKV, CockroachDB), and LiteDB internals. These changes require minimal code modifications (mostly 1-line config changes) but are projected to reduce write latency by 40-80% under concurrent load.

## Current Baseline (from benchmark suite)

| Scenario | Users | Median | Avg | p99 | Throughput |
|----------|-------|--------|-----|-----|------------|
| Small write | 1 | 13ms | 14.6ms | 36ms | 10.7 req/s |
| Small write | 10 | 71ms | 71.7ms | 130ms | 67.7 req/s |
| XLarge write | 1 | 21ms | 32.3ms | 200ms | 8.9 req/s |
| XLarge write | 10 | 88ms | 89.9ms | 220ms | 60.0 req/s |

## Problem Statement

The current write path has three configuration-level bottlenecks:

1. **LiteDB `Connection=shared`** — uses cross-process mutex locking and reopens the database file on each operation. Confman is single-process per node, so this overhead is unnecessary.
2. **WAL `FlushInterval=Zero`** — triggers `fsync` on every single commit. Under concurrent load, each write pays the full disk flush penalty independently instead of batching flushes.
3. **Redundant pre-write read** — `ConfigController.Set` reads the existing entry before replication solely to choose HTTP 200 vs 201 status code. This is a LiteDB read on the critical path for cosmetic value.
4. **HTTP/1.1 inter-node transport** — no multiplexing for Raft AppendEntries RPCs between nodes.

## Proposed Solution

Four independent changes, each touching 1-3 lines:

### Change 1: LiteDB Direct Connection Mode

**File:** `src/Confman.Api/Storage/LiteDbConfigStore.cs:43`

**Current:**
```csharp
_db = new LiteDatabase($"Filename={dbPath};Connection=shared");
```

**Proposed:**
```csharp
_db = new LiteDatabase($"Filename={dbPath};Connection=direct");
```

**Rationale:** `shared` mode uses OS-level mutex locking for cross-process safety and reopens the datafile repeatedly. `direct` mode keeps the file handle open and uses lightweight in-process locking. Since each Confman node is a single process with its own database file, cross-process coordination is unnecessary. LiteDB community benchmarks report 2-5x write improvement.

**Risk:** If any external process (backup tool, debug inspector) opens the same `.db` file while Confman is running, it could corrupt data. Mitigated by per-node data isolation (`./data-{port}/confman.db`).

### Change 2: WAL Periodic Flush Interval

**File:** `src/Confman.Api/Program.cs:59-65`

**Current:**
```csharp
builder.Services.AddSingleton(new WriteAheadLog.Options
{
    Location = walPath,
    MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
    ChunkSize = 512 * 1024,
});
```

**Proposed:**
```csharp
builder.Services.AddSingleton(new WriteAheadLog.Options
{
    Location = walPath,
    MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
    ChunkSize = 512 * 1024,
    FlushInterval = TimeSpan.FromMilliseconds(100),
});
```

**Rationale:** The default `FlushInterval` of `TimeSpan.Zero` triggers a background flush (fsync) on every commit. Setting it to 100ms batches multiple commits into a single fsync, amortizing the disk I/O cost across concurrent writes. This is the same technique etcd uses ("group commit") and is the single largest latency contributor in WAL-based systems.

**Trade-off:** A 100ms window of committed-but-not-flushed entries could be lost if the process crashes. However, since Raft replicates to a majority before acknowledging, data loss only occurs if a majority of nodes crash simultaneously within the same 100ms window — which is an acceptable risk for a config store.

**Tuning:** Start at 100ms. If benchmarks show room, try 50ms (tighter durability) or 200ms (more batching). Make configurable via `appsettings.json`.

### Change 3: Remove Redundant Pre-Write Existence Check

**File:** `src/Confman.Api/Controllers/ConfigController.cs:110`

**Current:**
```csharp
// Check if entry exists for response code
var existing = await _store.GetAsync(ns, key, ct);
// ... replication ...
if (existing is null)
    return CreatedAtAction(...);
return Ok(dto);
```

**Proposed:**
```csharp
// ... replication ...
// Always return 200 OK for PUT (idempotent operation)
return Ok(dto);
```

**Rationale:** The sole purpose of this read is to decide between HTTP 200 (update) and 201 (create). For an idempotent PUT endpoint, returning 200 is semantically correct regardless. This removes one LiteDB `FindOne` per write from the leader's hot path.

**Risk:** Clients that relied on 201 to detect "first creation" will now always get 200. This is a minor API behavior change — document in changelog.

### Change 4: Enable HTTP/2 for Inter-Node Communication

**File:** `src/Confman.Api/appsettings.json`

**Current:** No `protocolVersion` configured (defaults to HTTP/1.1).

**Proposed:** Add to root config:
```json
{
  "protocolVersion": "http2"
}
```

**Rationale:** HTTP/2 provides multiplexing (multiple Raft RPCs over a single TCP connection) and header compression. For a 3-node cluster with concurrent AppendEntries, this reduces connection overhead. Kestrel in .NET 10 supports HTTP/2 natively.

**Risk:** Low. All nodes run the same binary on the same network. HTTP/2 is well-supported. If any issues arise, simply remove the config line to revert.

## Acceptance Criteria

- [x] LiteDB connection mode changed to `direct` in `LiteDbConfigStore.cs`
- [x] WAL `FlushInterval` set to configurable value (default 100ms) in `Program.cs`
- [x] `FlushInterval` value exposed in `appsettings.json` as `Raft:FlushIntervalMs` for tuning
- [x] Pre-write existence check removed from `ConfigController.Set`
- [x] HTTP/2 enabled via `protocolVersion` in `appsettings.json`
- [x] All existing tests pass (`dotnet test`)
- [ ] 3-node cluster starts and operates normally (`/run-cluster`)
- [ ] Benchmark re-run shows measurable improvement vs baseline
- [ ] CLAUDE.md / FEATURES.md updated if needed

## Testing Strategy

1. **Unit tests:** Existing tests cover controller and storage behavior — verify they pass after changes.
2. **Cluster smoke test:** Start 3-node cluster, write/read configs, verify replication, kill/restart a node.
3. **Benchmark comparison:** Re-run `python benchmarks/run_benchmark.py --tier small --scenarios write` and compare against baseline. Target: ≥30% improvement in p50 write latency at 10 concurrent users.

## Implementation Order

Each change is independent and can be tested in isolation:

1. **LiteDB Direct mode** — highest expected impact, zero risk to tests
2. **WAL FlushInterval** — high impact, needs cluster test
3. **Remove pre-write read** — small refactor, test behavior change
4. **HTTP/2** — config-only, needs cluster test

## References

- [DotNext WAL Options](https://dotnet.github.io/dotNext/features/cluster/wal.html) — `FlushInterval` documentation
- [LiteDB Connection String](https://www.litedb.org/docs/connection-string/) — `Connection=direct` vs `shared`
- [etcd Performance Guide](https://etcd.io/docs/v3.5/op-guide/performance/) — group commit / batching rationale
- Confman benchmark baseline: `benchmarks/results/small-write-*_stats.csv`
- Prior WAL tuning: commit `8847099` (PrivateMemory for +30-50% write throughput)
