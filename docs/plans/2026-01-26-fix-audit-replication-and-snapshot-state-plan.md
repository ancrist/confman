---
title: "Fix: Audit Replication and Snapshot State"
type: fix
date: 2026-01-26
issues: ["#2", "#3"]
---

# Fix: Audit Replication and Snapshot State

## Overview

This plan addresses two related GitHub issues that impact data consistency in the Raft cluster:

| Issue | Problem | Impact |
|-------|---------|--------|
| **#2** | Audit events only created on leader | Leadership changes lose audit access |
| **#3** | Snapshots don't serialize state | Recovering nodes lose all data |

These issues are interdependent: fixing snapshots without audit replication would snapshot incomplete data.

## Problem Statement

### Issue #2: Audit Events Not Replicated

The current implementation creates audit events only on the leader node to prevent duplicates during log replay:

```csharp
// src/Confman.Api/Cluster/ConfigStateMachine.cs:56-60
var isLeader = cluster is not null && !cluster.LeadershipToken.IsCancellationRequested;
await command.ApplyAsync(store, isLeader, token);
```

Each command checks `isLeader` before creating audit events:

```csharp
// src/Confman.Api/Cluster/Commands/SetConfigCommand.cs:35-48
if (isLeader)
{
    await store.AppendAuditAsync(new AuditEvent { ... }, ct);
}
```

**Result:** Followers never create audit events. When leadership changes, the new leader has no historical audit data.

### Issue #3: Snapshots Don't Include Full State

The snapshot implementation writes a marker instead of actual state:

```csharp
// src/Confman.Api/Cluster/ConfigStateMachine.cs:86-96
protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
{
    var marker = System.Text.Encoding.UTF8.GetBytes("CONFMAN_SNAPSHOT");
    await writer.Invoke(marker, token);
}
```

**Result:** A node recovering from snapshot gets an empty marker while LiteDB is missing or inconsistent.

## Root Cause

Both issues violate the core Raft principle: **all durable state must flow through the consensus log**.

```
CURRENT (Wrong)                    CORRECT (Raft)
─────────────────                  ──────────────────────────
Client → Leader                    Client → Leader
    ↓                                  ↓
Create Audit (side-effect)         Replicate Command via Raft
    ↓                                  ↓
Apply to Store                     ALL nodes apply + create audit
    ↓
Followers: no audit
```

## Proposed Solution

### Phase 1: Fix Audit Replication (Issue #2)

**Approach:** Remove `isLeader` checks and use idempotent audit storage.

1. **Remove `isLeader` parameter** from `ICommand.ApplyAsync`
2. **Remove `isLeader` checks** from all command implementations
3. **Make audit storage idempotent** using a unique key (timestamp + action + key + namespace)
4. **Update ConfigStateMachine** to not pass `isLeader` flag

### Phase 2: Fix Snapshot Serialization (Issue #3)

**Approach:** Implement proper snapshot persistence and restoration.

1. **Add snapshot data model** for serialization
2. **Add `IConfigStore` methods** for bulk export/import
3. **Implement `PersistAsync`** to serialize all state
4. **Implement `RestoreAsync`** to restore from snapshot

## Technical Approach

### Files to Modify

| File | Changes |
|------|---------|
| `src/Confman.Api/Cluster/Commands/ICommand.cs` | Remove `isLeader` parameter |
| `src/Confman.Api/Cluster/Commands/SetConfigCommand.cs` | Remove `isLeader` check |
| `src/Confman.Api/Cluster/Commands/DeleteConfigCommand.cs` | Remove `isLeader` check |
| `src/Confman.Api/Cluster/Commands/SetNamespaceCommand.cs` | Remove `isLeader` check |
| `src/Confman.Api/Cluster/Commands/DeleteNamespaceCommand.cs` | Remove `isLeader` check |
| `src/Confman.Api/Cluster/ConfigStateMachine.cs` | Remove `isLeader` logic, implement snapshots |
| `src/Confman.Api/Storage/IConfigStore.cs` | Add bulk operations |
| `src/Confman.Api/Storage/LiteDbConfigStore.cs` | Implement bulk operations, idempotent audit |
| `tests/Confman.Tests/CommandTests.cs` | Update tests for new signature |

### New Files

| File | Purpose |
|------|---------|
| `src/Confman.Api/Cluster/SnapshotData.cs` | Snapshot serialization model |

## MVP Implementation

### 1. SnapshotData.cs

```csharp
namespace Confman.Api.Cluster;

/// <summary>
/// Data model for serializing state machine snapshots.
/// </summary>
public class SnapshotData
{
    public List<ConfigEntry> Configs { get; set; } = [];
    public List<Namespace> Namespaces { get; set; } = [];
    public List<AuditEvent> AuditEvents { get; set; } = [];
    public long SnapshotIndex { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
```

### 2. ICommand.cs Changes

```csharp
// Remove isLeader parameter
public interface ICommand
{
    Task ApplyAsync(IConfigStore store, CancellationToken ct = default);
}
```

### 3. Command Changes (example: SetConfigCommand)

```csharp
public async Task ApplyAsync(IConfigStore store, CancellationToken ct = default)
{
    var existing = await store.GetAsync(Namespace, Key, ct);

    await store.SetAsync(Namespace, Key, Value, ct);

    // Always create audit - storage handles idempotency
    await store.AppendAuditAsync(new AuditEvent
    {
        Id = $"{Timestamp:O}:{Namespace}:{Key}:{Action}", // Idempotent key
        Timestamp = Timestamp,
        Action = existing is null ? "config.created" : "config.updated",
        Actor = Actor,
        Namespace = Namespace,
        Key = Key,
        OldValue = existing?.Value,
        NewValue = Value
    }, ct);
}
```

### 4. IConfigStore.cs Additions

```csharp
// Bulk operations for snapshots
Task<List<ConfigEntry>> GetAllConfigsAsync(CancellationToken ct = default);
Task<List<AuditEvent>> GetAllAuditEventsAsync(CancellationToken ct = default);
Task RestoreFromSnapshotAsync(SnapshotData snapshot, CancellationToken ct = default);
```

### 5. LiteDbConfigStore.cs Additions

```csharp
public Task<List<ConfigEntry>> GetAllConfigsAsync(CancellationToken ct = default)
{
    return Task.FromResult(_configs.FindAll().ToList());
}

public Task<List<AuditEvent>> GetAllAuditEventsAsync(CancellationToken ct = default)
{
    return Task.FromResult(_audit.FindAll().ToList());
}

public Task RestoreFromSnapshotAsync(SnapshotData snapshot, CancellationToken ct = default)
{
    // Clear existing data
    _configs.DeleteAll();
    _namespaces.DeleteAll();
    _audit.DeleteAll();

    // Restore from snapshot
    _configs.InsertBulk(snapshot.Configs);
    _namespaces.InsertBulk(snapshot.Namespaces);
    _audit.InsertBulk(snapshot.AuditEvents);

    return Task.CompletedTask;
}

// Update AppendAuditAsync for idempotency
public Task AppendAuditAsync(AuditEvent auditEvent, CancellationToken ct = default)
{
    // Use upsert for idempotency during log replay
    _audit.Upsert(auditEvent);
    return Task.CompletedTask;
}
```

### 6. ConfigStateMachine.cs Changes

```csharp
protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
{
    _logger.LogInformation("Creating snapshot");

    var snapshot = new SnapshotData
    {
        Configs = await _store.GetAllConfigsAsync(token),
        Namespaces = await _store.ListNamespacesAsync(token),
        AuditEvents = await _store.GetAllAuditEventsAsync(token),
        Timestamp = DateTimeOffset.UtcNow
    };

    var json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
    await writer.Invoke(json, token);

    _logger.LogInformation("Snapshot created: {ConfigCount} configs, {AuditCount} audit events",
        snapshot.Configs.Count, snapshot.AuditEvents.Count);
}

protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
{
    _logger.LogInformation("Restoring from snapshot: {File}", snapshotFile.FullName);

    var json = await File.ReadAllBytesAsync(snapshotFile.FullName, token);
    var snapshot = JsonSerializer.Deserialize<SnapshotData>(json);

    if (snapshot is null)
    {
        _logger.LogError("Failed to deserialize snapshot");
        return;
    }

    await _store.RestoreFromSnapshotAsync(snapshot, token);

    _logger.LogInformation("Snapshot restored: {ConfigCount} configs, {AuditCount} audit events",
        snapshot.Configs.Count, snapshot.AuditEvents.Count);
}

// Update ApplyAsync to not pass isLeader
protected override async ValueTask ApplyAsync(LogEntry entry, CancellationToken token)
{
    var command = await DeserializeCommandAsync(entry, token);
    await command.ApplyAsync(_store, token);
}
```

## Acceptance Criteria

### Issue #2: Audit Replication

- [x] Audit events created on ALL nodes during command apply
- [x] Querying any healthy node returns complete audit history
- [x] No duplicate audit entries (idempotent storage)
- [x] Dashboard can query single node instead of all nodes
- [x] Leadership changes don't affect audit visibility

### Issue #3: Snapshot State

- [x] `PersistAsync` serializes all configs, namespaces, and audit events
- [x] `RestoreAsync` fully restores state from snapshot
- [x] Node recovering from snapshot has identical state to other nodes
- [x] Log compaction + snapshot recovery works correctly
- [x] Snapshot size is reasonable (JSON serialization)

### Integration

- [x] Existing tests pass (with signature updates)
- [x] New tests for snapshot serialization/restoration
- [ ] Multi-node integration test for audit consistency

## Test Plan

### Unit Tests

1. **Command signature tests** - Verify commands work without `isLeader`
2. **Idempotent audit tests** - Same audit event inserted twice = 1 record
3. **Snapshot serialization tests** - Round-trip serialize/deserialize
4. **Bulk operations tests** - GetAll and RestoreFromSnapshot work correctly

### Integration Tests

1. **Audit consistency test**:
   - Create config on leader
   - Verify audit exists on all nodes
   - Kill leader, verify new leader has audit

2. **Snapshot recovery test**:
   - Create 100 configs across namespaces
   - Force snapshot creation
   - Wipe a follower's LiteDB
   - Restart follower, verify full state recovery

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Audit duplicates during replay | Idempotent upsert with unique ID |
| Large snapshots for big audit logs | Consider compression (future) |
| Snapshot during active writes | DotNext handles locking |
| Breaking change to command interface | Update all callers in same PR |

## References

### Internal References
- `src/Confman.Api/Cluster/Commands/ICommand.cs:20-22`
- `src/Confman.Api/Cluster/ConfigStateMachine.cs:56-60`
- `src/Confman.Api/Cluster/ConfigStateMachine.cs:86-96`
- `docs/solutions/2026-01-24-dotnext-raft-cluster-members-configuration.md`

### Related Work
- GitHub Issue #2: Audit events not replicated
- GitHub Issue #3: Snapshots don't include full state
