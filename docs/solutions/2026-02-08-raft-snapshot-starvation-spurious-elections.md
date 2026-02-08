---
title: "Raft Snapshot Creation Starves Heartbeats, Causing Spurious Elections"
category: distributed-systems
tags: [dotnext, raft, snapshot, election-timeout, heartbeat, large-payloads, performance]
module: Confman.Api
symptoms:
  - "Transition to Candidate state" logged repeatedly on followers during heavy writes
  - "Cluster member http://... is unavailable" on leader
  - "No leader in cluster" followed by leader recovery (flapping)
  - Write latency grows progressively with large payloads (1MB+)
  - TaskCanceledException / SocketException during AppendEntries replication
date: 2026-02-08
---

# Raft Snapshot Creation Starves Heartbeats, Causing Spurious Elections

## Problem

When writing large config values (1MB+) at high volume (500+ entries), follower nodes repeatedly trigger spurious elections. The cluster flaps between having a leader and "No leader in cluster" states. Writes eventually complete but with degraded throughput and high tail latency.

### Observed Behavior

```
# Node 2 (follower) — snapshots block heartbeat responses
22:57:46.974  Creating snapshot (499 configs, 523MB)
22:57:47.687  Snapshot created (713ms)
22:57:52.086  Transition to Candidate state has started with term 1
22:58:07.140  No leader in cluster
22:58:07.245  Leader changed to: http://127.0.0.1:6100/   ← recovered

# Node 1 (leader) — followers timing out during replication
22:59:17.630  Cluster member http://127.0.0.1:6200/ is unavailable
             TaskCanceledException: Error while copying content to a stream
```

## Root Cause

**Three factors compound to destabilize the cluster:**

### 1. Snapshots block the Raft state machine thread

DotNext's `SimpleStateMachine.PersistAsync` runs on the state machine thread. While creating a snapshot, the node cannot:
- Apply new log entries
- Respond to AppendEntries RPCs (heartbeats)
- Participate in elections

With large datasets, snapshot duration scales linearly:

| Configs | Snapshot Size | Duration |
|---------|--------------|----------|
| 99      | 103 MB       | ~324 ms  |
| 199     | 208 MB       | ~564 ms  |
| 299     | 313 MB       | ~530 ms  |
| 399     | 418 MB       | ~637 ms  |
| 499     | 523 MB       | ~713 ms  |

### 2. Election timeout was shorter than snapshot duration

The default election timeout (150-300ms) is designed for small config values where snapshots take <50ms. With 1MB values, snapshots take 500-700ms — exceeding the election timeout by 2-3x. The follower assumes the leader is dead and starts an election.

### 3. Request timeout was too short for large AppendEntries RPCs

The default `requestTimeout` of 2 seconds is insufficient when the leader sends hundreds of 1MB entries in a single AppendEntries RPC. The HTTP request times out, and the leader marks the follower as unavailable.

### The cascade

```
Large snapshot starts (700ms)
  → Follower misses heartbeat (election timeout 150-300ms)
    → Follower starts election
      → Election disrupts replication
        → More entries pile up on leader
          → Next AppendEntries is even larger
            → Request timeout exceeded
              → Leader marks follower unavailable
```

## Solution

### Increase Raft timeouts for large-payload tolerance

```json
{
  "lowerElectionTimeout": 1000,
  "upperElectionTimeout": 3000,
  "requestTimeout": "00:00:10"
}
```

**Previous values:** 150ms / 300ms / 2s

### Reduce snapshot interval to prevent WAL bloat

```json
{
  "Raft": {
    "SnapshotInterval": 100
  }
}
```

**Previous value:** 1000. With 1MB entries, 1000 entries = ~1.3GB WAL before first compaction. Reducing to 100 compacts every ~130MB.

### Reduce flush interval for sequential write latency

```json
{
  "Raft": {
    "FlushIntervalMs": 100
  }
}
```

**Previous value:** 500ms. Each sequential write waits up to `FlushIntervalMs` for the next fsync cycle. Reducing to 100ms cuts max per-write latency from 500ms to 100ms.

### Remove Kestrel body size limit

```csharp
// Program.cs
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
```

The default 30MB limit is too small for Raft AppendEntries RPCs that bundle many large log entries.

## Key Insights

1. **Election timeout must exceed max snapshot duration.** The Raft invariant is: `snapshot_time < election_timeout < request_timeout`. If snapshots can take 700ms, election timeout must be >700ms. Rule of thumb: snapshot time ≈ 1ms per config entry, so plan accordingly.

2. **Snapshots are O(total data), not O(delta).** Every snapshot serializes the entire dataset (all configs + namespaces + audit events). With N × 1MB configs, each snapshot writes ~N MB to disk. This is the fundamental limit of the full-state snapshot model.

3. **WAL bloat causes progressive latency degradation.** Without snapshots, the WAL accumulates all entries since startup. With 1MB entries and `SnapshotInterval: 1000`, the WAL grows to ~1.3GB across ~167 chunk files (8MB each). DotNext operations degrade as chunk count increases.

4. **`FlushIntervalMs` adds latency to sequential writes.** The WAL batches fsyncs for group commit. This helps concurrent writes (many writers amortize one fsync) but hurts sequential writes (each write waits for the next flush cycle). With 500ms interval, average wait is ~250ms per sequential write.

5. **Kestrel's 30MB body limit applies to Raft RPCs.** DotNext uses HTTP for inter-node communication. AppendEntries RPCs bundle multiple log entries into one HTTP request. With large values, this easily exceeds Kestrel's default `MaxRequestBodySize`.

6. **These issues only manifest with large payloads.** Typical config values (1-10KB) produce snapshots that complete in <50ms, well within the default 150-300ms election timeout. The 1MB benchmark is a deliberate stress test of the system's limits.

## Tuning Guidelines

| Payload Size | SnapshotInterval | Election Timeout | Request Timeout | Notes |
|-------------|-----------------|-----------------|----------------|-------|
| 1-10 KB     | 1000            | 150-300 ms      | 2s             | Default — works well |
| 10-100 KB   | 500             | 500-1500 ms     | 5s             | Moderate payloads |
| 100 KB-1 MB | 100             | 1000-3000 ms    | 10s            | Large payloads, stress test territory |
| > 1 MB      | 50              | 2000-5000 ms    | 30s            | Extreme — consider external blob storage |

## References

- `src/Confman.Api/appsettings.json` — Raft timeout and snapshot configuration
- `src/Confman.Api/Program.cs` — Kestrel body size limit and WAL options
- `src/Confman.Api/Cluster/ConfigStateMachine.cs` — Snapshot creation in `PersistAsync`
- [DotNext SimpleStateMachine](https://dotnet.github.io/dotNext/features/cluster/raft.html) — Snapshot lifecycle
- `docs/plans/2026-02-07-perf-write-throughput-tier3-plan.md` — Tier 3 performance plan
