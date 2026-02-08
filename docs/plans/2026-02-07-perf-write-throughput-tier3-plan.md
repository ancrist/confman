---
title: "perf: Write throughput Tier 3 — write batching + MessagePack"
type: perf
date: 2026-02-07
---

# perf: Write Throughput Tier 3 — Write Batching + MessagePack

## Overview

Two changes targeting the concurrent write throughput ceiling. Write batching amortizes Raft consensus cost across N operations. MessagePack reduces serialization overhead and payload size.

Implemented in two phases: write batching first (high impact), MessagePack second (incremental).

## Current Baseline (post-Tier 2)

| Scenario | Throughput | p50 | Notes |
|----------|-----------|-----|-------|
| Sequential writes | 578.5 req/s | 1.5ms | `benchmark_configs.py` direct to leader |
| write-1u (Locust) | 12.4 req/s | 2ms | Sequential to leader |
| write-10u (Locust) | 128.4 req/s | 2ms | Raft-consensus-bound |
| read-10u (Locust) | ~3,000 req/s | 2ms | With read barrier |

**Bottleneck:** Each concurrent write independently waits for Raft quorum (~2ms). 10 users × 2ms = serialized consensus, not parallel throughput. Write batching groups N pending writes into one Raft log entry so one quorum round-trip commits all of them.

## Goals

- **(A) Concurrent write throughput** — break past the ~128 req/s ceiling toward 500-800+ req/s
- **(D) Operational readiness** — optimizations that benefit real networks (10-50ms RTT), not just localhost

## Non-goals

- Rolling-upgrade compatibility (wire format can change freely)
- Storage engine replacement (LiteDB is fast enough at current scale)
- TCP transport (marginal gain over HTTP/2 at current scale)

---

## Phase 1: Write Batching

### Mechanism

Instead of immediately replicating each command, a `BatchingRaftService` buffers pending writes in a `Channel<PendingCommand>`. A background flush loop seals and replicates batches:

```
PUT 1 ──→ enqueue ──┐
PUT 2 ──→ enqueue ──┤  batch window (≤1ms)
PUT 3 ──→ enqueue ──┤
PUT 4 ──→ enqueue ──┘
                    └──→ serialize [1,2,3,4] as BatchCommand
                         ──→ Raft quorum (2ms)
                         ──→ complete all 4 TaskCompletionSources
```

**Amortized cost:** ~2ms quorum / N commands per batch. Under 10 concurrent users with 1ms batch window, batches of 5-10 are typical → 5-10x throughput improvement.

### BatchingRaftService

Implements `IRaftService`. Replaces `RaftService` in DI registration. Controllers don't change.

```csharp
public class BatchingRaftService : IRaftService
{
    private readonly Channel<PendingCommand> _queue;
    private readonly Task _flushLoop;

    // Controllers call this exactly as before
    public async Task<bool> ReplicateAsync(ICommand command, CancellationToken ct)
    {
        var pending = new PendingCommand(command, new TaskCompletionSource<bool>());
        await _queue.Writer.WriteAsync(pending, ct);
        return await pending.Completion.Task;
    }

    // Background loop: collect → seal → replicate → notify
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            var batch = CollectBatch();     // drain up to limits
            var success = await ReplicateBatch(batch);
            foreach (var p in batch)
                p.Completion.TrySetResult(success);
        }
    }
}

private record PendingCommand(ICommand Command, TaskCompletionSource<bool> Completion);
```

### Batch Limits

| Parameter | Default | Rationale |
|-----------|---------|-----------|
| `Raft:BatchMaxSize` | 50 | Cap commands per batch |
| `Raft:BatchMaxWaitMs` | 1 | Fixed window — no adaptive complexity |
| `Raft:BatchMaxBytes` | 4194304 (4MB) | Stay under 8MB WAL ChunkSize |

The flush loop collects commands for up to `BatchMaxWaitMs` or until `BatchMaxSize` or `BatchMaxBytes` is reached — whichever comes first.

**Payload size estimation:** Each `PendingCommand` carries an `EstimatedSize` (approximated from `Value.Length` for set commands, ~0 for deletes). When cumulative bytes exceed `BatchMaxBytes`, the batch flushes early.

**Single-command behavior:** Under low load, the batch window expires after 1ms with one command. Cost: +1ms latency. Acceptable for a config service (p50 goes from 1.5ms to ~2.5ms).

### BatchCommand

New `ICommand` variant that wraps multiple commands:

```csharp
public class BatchCommand : ICommand
{
    public List<ICommand> Commands { get; init; }

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled, CancellationToken ct)
    {
        foreach (var command in Commands)
        {
            try
            {
                await command.ApplyAsync(store, auditEnabled, ct);
            }
            catch (Exception ex)
            {
                // Log but don't throw — entry is committed, all nodes must apply
                // Consistent across nodes: same bytes → same failure
            }
        }
    }
}
```

Wire format:

```json
{
  "$type": "batch",
  "commands": [
    { "$type": "set_config", "namespace": "prod", "key": "timeout", ... },
    { "$type": "delete_config", "namespace": "staging", "key": "old", ... }
  ]
}
```

**Sequential apply:** Inner commands execute in order. No parallel apply — commands in the same batch could touch the same key, and sequential execution preserves version ordering.

**Per-command error isolation:** A failed inner command is caught and logged. Other commands proceed. This is safe because all nodes see the same bytes and produce the same failure — consistency is preserved.

### State Machine Changes

`ConfigStateMachine.ApplyAsync` requires one change: snapshot interval counting.

A `BatchCommand` of 50 inner commands counts as 50 toward the snapshot threshold (not 1), because WAL size and LiteDB state grow proportionally to inner command count.

```csharp
// In ApplyAsync, after command.ApplyAsync:
var commandCount = command is BatchCommand batch ? batch.Commands.Count : 1;
_entriesSinceSnapshot += commandCount;
```

No other state machine changes. `ApplyAsync` already deserializes `ICommand` and calls `command.ApplyAsync()`. The `BatchCommand` handles the inner loop.

### Concurrency Safety

The batch design is safe under DotNext's sequential apply guarantee:

| Concern | Status | Reason |
|---------|--------|--------|
| `_entriesSinceSnapshot` race | Safe | DotNext applies entries sequentially — no concurrent `ApplyAsync` |
| SemaphoreSlim reentrancy | Safe | Each inner command independently acquires/releases — no nesting |
| Thread switch mid-transaction | Safe | No `await` between `BeginTrans()` and `Commit()` in LiteDB store |
| Batch atomicity | By design | Each inner command is its own LiteDB transaction. Not atomic as a batch, but consistent across nodes. |

### Error Handling

**Raft replication fails (quorum not reached):**

All `TaskCompletionSource`s in the batch receive `false`. Controllers return 503 as they do today.

**Inner command fails during apply:**

Caught per-command, logged, other commands proceed. The commit already succeeded in the Raft log — the entry exists on all nodes. A failing apply is an operational issue (e.g., LiteDB corruption), not a client error.

**Channel back-pressure:**

The `Channel` is bounded. If the flush loop can't keep up (unlikely — it's bottlenecked on Raft, not channel throughput), writes block at `WriteAsync` until capacity frees up. This provides natural back-pressure to HTTP clients via request latency.

### Configuration

```json
{
  "Raft": {
    "BatchMaxSize": 50,
    "BatchMaxWaitMs": 1,
    "BatchMaxBytes": 4194304
  }
}
```

Batching can be disabled by setting `BatchMaxSize: 1` and `BatchMaxWaitMs: 0` — every command flushes immediately, equivalent to current behavior.

### Files Changed

| File | Change |
|------|--------|
| `Cluster/Commands/BatchCommand.cs` | **New** — batch wrapper command |
| `Cluster/Commands/ICommand.cs` | Add `[JsonDerivedType(typeof(BatchCommand), "batch")]` |
| `Cluster/BatchingRaftService.cs` | **New** — Channel + flush loop + TCS management |
| `Cluster/ConfigStateMachine.cs` | Count inner commands for snapshot interval |
| `Program.cs` | Register `BatchingRaftService` as `IRaftService` |
| `appsettings.json` | Add `Raft:BatchMaxSize`, `Raft:BatchMaxWaitMs`, `Raft:BatchMaxBytes` |

### Tests

- **Unit: BatchCommand.ApplyAsync** — verify sequential apply, per-command error isolation, audit events created
- **Unit: BatchingRaftService** — verify batching behavior (flush on size, flush on time, flush on bytes)
- **Unit: ConfigStateMachine** — verify snapshot counter increments by inner command count
- **Integration: round-trip** — batch serialize → deserialize → apply → verify store state
- **Existing 57 tests** — must pass (controllers unchanged, IRaftService interface unchanged)
- **Benchmark: Locust write-10u** — target: 500+ req/s (from ~128 req/s)
- **Benchmark: sequential writes** — verify no regression (should be ~same as current 578 req/s)

---

## Phase 2: MessagePack Serialization

### Motivation

After Phase 1, batches of 50 commands produce ~65KB JSON payloads per Raft log entry. MessagePack reduces this by 30-50% and serializes 5-10x faster than `System.Text.Json`.

### What Changes

| Serialization point | Current | Proposed |
|---------------------|---------|----------|
| Raft replication (serialize) | `JsonSerializer.SerializeToUtf8Bytes` | `MessagePackSerializer.Serialize` |
| State machine apply (deserialize) | `JsonSerializer.Deserialize<ICommand>` | `MessagePackSerializer.Deserialize<ICommand>` |
| Snapshots (persist/restore) | JSON | **Keep JSON** — infrequent, human-readable for debugging |

### Polymorphic Dispatch

JSON uses `[JsonDerivedType]` for command polymorphism. MessagePack uses `[Union]`:

```csharp
// On ICommand interface
[Union(0, typeof(SetConfigCommand))]
[Union(1, typeof(DeleteConfigCommand))]
[Union(2, typeof(SetNamespaceCommand))]
[Union(3, typeof(DeleteNamespaceCommand))]
[Union(4, typeof(BatchCommand))]
public interface ICommand { ... }
```

Each command class gets `[MessagePackObject]` and `[Key(N)]` attributes on properties:

```csharp
[MessagePackObject]
public class SetConfigCommand : ICommand
{
    [Key(0)] public string Namespace { get; init; }
    [Key(1)] public string Key { get; init; }
    [Key(2)] public string Value { get; init; }
    [Key(3)] public string Type { get; init; }
    [Key(4)] public string Author { get; init; }
    [Key(5)] public DateTimeOffset Timestamp { get; init; }
}
```

`[JsonDerivedType]` attributes remain on `ICommand` for snapshot JSON serialization.

### State Machine Deserialization

`ConfigStateMachine.DeserializeCommand` currently checks for `{` as first byte to identify JSON. With MessagePack, the discriminator changes:

```csharp
private ICommand? DeserializeCommand(in ReadOnlySequence<byte> payload)
{
    if (payload.IsEmpty) return null;
    var bytes = payload.ToArray();
    if (bytes.Length == 0) return null;

    // MessagePack arrays start with 0x90-0x9f or 0xdc/0xdd
    // Skip Raft internal entries that don't match
    try
    {
        return MessagePackSerializer.Deserialize<ICommand>(bytes);
    }
    catch (MessagePackSerializationException ex)
    {
        _logger.LogWarning(ex, "MessagePack deserialization failed ({Length} bytes)", bytes.Length);
        return null;
    }
}
```

### Files Changed

| File | Change |
|------|--------|
| `Confman.Api.csproj` | Add `MessagePack` NuGet package |
| `Commands/ICommand.cs` | Add `[Union]` attributes |
| `Commands/SetConfigCommand.cs` | Add `[MessagePackObject]` + `[Key]` attributes |
| `Commands/DeleteConfigCommand.cs` | Same |
| `Commands/SetNamespaceCommand.cs` | Same |
| `Commands/DeleteNamespaceCommand.cs` | Same |
| `Commands/BatchCommand.cs` | Same |
| `Cluster/BatchingRaftService.cs` | `MessagePackSerializer.Serialize` |
| `Cluster/ConfigStateMachine.cs` | `MessagePackSerializer.Deserialize` in `DeserializeCommand` |

### Tests

- **Unit: round-trip** — serialize each command type with MessagePack, deserialize, verify equality
- **Unit: BatchCommand round-trip** — serialize batch with mixed inner commands, verify all deserialize correctly
- **Unit: snapshot** — verify snapshots still use JSON (not affected by MessagePack change)
- **Existing 57 tests** — must pass
- **Benchmark** — measure payload size reduction and serialization time delta

---

## Implementation Order

```
Phase 1: Write Batching
  1. BatchCommand + ICommand update         (command layer)
  2. BatchingRaftService                     (replication layer)
  3. ConfigStateMachine snapshot counting    (state machine)
  4. DI registration + config               (wiring)
  5. Tests + benchmark

Phase 2: MessagePack
  6. Add NuGet package + attributes          (all command classes)
  7. Update serialization points             (BatchingRaftService + ConfigStateMachine)
  8. Tests + benchmark
```

Phase 1 and Phase 2 are independent — MessagePack works with or without batching. But batching first maximizes the throughput gain, and MessagePack compounds on top of it.

## Acceptance Criteria

### Phase 1: Write Batching
- [ ] `BatchCommand` implements `ICommand`, applies inner commands sequentially
- [ ] `BatchCommand.ApplyAsync` catches per-command exceptions without failing the batch
- [ ] `BatchingRaftService` implements `IRaftService` with Channel-based batching
- [ ] Batch flushes on max size (50), max wait (1ms), or max bytes (4MB)
- [ ] `ConfigStateMachine` counts inner commands toward snapshot interval
- [ ] All 57 existing tests pass
- [ ] Locust write-10u throughput ≥ 500 req/s (from ~128 req/s)
- [ ] Sequential write throughput: no regression from 578 req/s
- [ ] Single-write latency increase ≤ 2ms (batch window overhead)

### Phase 2: MessagePack
- [ ] All command types annotated with `[MessagePackObject]` + `[Key]`
- [ ] `ICommand` has `[Union]` attributes for MessagePack polymorphism
- [ ] Raft replication uses `MessagePackSerializer.Serialize`
- [ ] State machine uses `MessagePackSerializer.Deserialize`
- [ ] Snapshots remain JSON
- [ ] All existing tests pass
- [ ] Raft log entry size reduced ≥ 30% vs JSON baseline

## Data Directory Warning

After Phase 2, existing Raft WAL entries (JSON format) will be incompatible with the new MessagePack deserializer. **Wipe `data-*` directories before starting the cluster with MessagePack enabled.** This is acceptable per the "no rolling upgrade" decision.

## References

- Tier 1 plan: `docs/plans/2026-02-05-perf-write-throughput-tier1-quick-wins-plan.md`
- Tier 2 plan: `docs/plans/2026-02-05-perf-write-throughput-tier2-optimizations-plan.md`
- Tier 1 PR: #12
- Tier 2 PR: #13
- etcd batching: raft.go `Ready()` batches pending proposals
- MessagePack-CSharp: https://github.com/MessagePack-CSharp/MessagePack-CSharp
- DotNext sequential apply: `SimpleStateMachine` base class contract
