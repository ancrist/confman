---
title: "feat: MessagePack + LZ4 serialization for Raft commands and snapshots"
type: feat
date: 2026-02-09
---

# feat: MessagePack + LZ4 Serialization

## Overview

Replace JSON serialization with MessagePack-CSharp for both Raft command replication and snapshots. MessagePack is a compact binary format (~30% smaller than JSON) and MessagePack-CSharp has **built-in LZ4 compression** (`Lz4BlockArray`), making this a single-library solution for serialization + compression.

For config values with repetitive JSON content, LZ4 compression achieves **60-80% reduction** — which directly shrinks WAL size, replication payloads, and snapshot files.

## Motivation

| Layer | Current (JSON, 1MB payload) | Expected (MsgPack+LZ4) | Why |
|-------|----------------------------|------------------------|-----|
| Raft WAL per entry | ~1 MB | ~200-400 KB | LZ4 compresses JSON config values well |
| Replication per entry | ~2 MB (leader→2 followers) | ~400-800 KB | Less network I/O |
| Snapshot (500 entries) | ~500 MB | ~100-200 MB | Streaming MsgPack + LZ4 |
| Total write amplification (1000 x 1MB) | ~10 GB/node | ~3-5 GB/node | All 3 layers shrink |

Secondary benefit: binary format eliminates the "binary-in-JSON" problem — storing compressed blobs inside JSON required base64 encoding (+33% overhead), whereas MessagePack handles `byte[]` natively.

## Non-goals

- Rolling-upgrade / backwards compatibility (wire format change, wipe data dirs)
- LiteDB storage format change (LiteDB keeps its own BSON format)
- Changing the REST API format (JSON stays for client-facing API)

---

## Phase 1: MessagePack for Raft Commands

Replace `System.Text.Json` serialization in the Raft command pipeline with MessagePack-CSharp.

### 1.1 Add NuGet package

```xml
<PackageReference Include="MessagePack" Version="3.1.4" />
```

### 1.2 Annotate command types with MessagePack attributes

**`ICommand.cs`** — Add `[Union]` attributes for polymorphic deserialization:

```csharp
[Union(0, typeof(SetConfigCommand))]
[Union(1, typeof(DeleteConfigCommand))]
[Union(2, typeof(SetNamespaceCommand))]
[Union(3, typeof(DeleteNamespaceCommand))]
[Union(4, typeof(BatchCommand))]
public interface ICommand
```

Keep the existing `[JsonDerivedType]` attributes — JSON is still used for the REST API request/response bodies.

**Each command class** — Add `[MessagePackObject]` and `[Key(n)]`:

```csharp
[MessagePackObject]
public sealed record SetConfigCommand : ICommand
{
    [Key(0)] public required string Namespace { get; init; }
    [Key(1)] public required string Key { get; init; }
    [Key(2)] public required string Value { get; init; }
    [Key(3)] public string Type { get; init; } = "string";
    [Key(4)] public required string Author { get; init; }
    [Key(5)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    // EstimatedBytes and ApplyAsync are ignored by MessagePack (no [Key])
}
```

Repeat for all 5 command types:

| Command | Properties to key |
|---------|------------------|
| `SetConfigCommand` | Namespace, Key, Value, Type, Author, Timestamp |
| `DeleteConfigCommand` | Namespace, Key, Author, Timestamp |
| `SetNamespaceCommand` | Path, Description, Owner, Author, Timestamp |
| `DeleteNamespaceCommand` | Path, Author, Timestamp |
| `BatchCommand` | Commands (List\<ICommand\>) |

### 1.3 Replace serialization sites

Two files serialize commands to bytes:

**`RaftService.cs:80`** — single command path:
```csharp
// Before:
var bytes = JsonSerializer.SerializeToUtf8Bytes(command);

// After:
var bytes = MessagePackSerializer.Serialize<ICommand>(command);
```

**`BatchingRaftService.cs:187`** — batched command path:
```csharp
// Before:
var bytes = JsonSerializer.SerializeToUtf8Bytes(command);

// After:
var bytes = MessagePackSerializer.Serialize<ICommand>(command);
```

> **Critical**: Must serialize as `Serialize<ICommand>(command)`, NOT `Serialize(command)`. Without the interface type parameter, MessagePack serializes the concrete type and loses the Union discriminator. Deserialization then fails because it doesn't know which derived type to create.

### 1.4 Replace deserialization site

**`ConfigStateMachine.cs:213`** — `DeserializeCommand` method:

```csharp
private ICommand? DeserializeCommand(in ReadOnlySequence<byte> payload)
{
    if (payload.IsEmpty)
        return null;

    // Skip DotNext WAL null-byte padding.
    // Under concurrent load, the WAL may prepend 0x00 bytes before the actual payload.
    var bytes = payload.ToArray();
    var offset = 0;
    while (offset < bytes.Length && bytes[offset] == 0x00)
        offset++;

    if (offset >= bytes.Length)
        return null;

    try
    {
        var span = new ReadOnlyMemory<byte>(bytes, offset, bytes.Length - offset);
        return MessagePackSerializer.Deserialize<ICommand>(span);
    }
    catch (MessagePackSerializationException ex)
    {
        _logger.LogWarning(ex, "MessagePack deserialization failed ({Length} bytes, offset {Offset})",
            bytes.Length, offset);
        return null;
    }
}
```

> **Critical gotcha — DotNext WAL null-byte padding**: The WAL may prepend `0x00` bytes before the actual command payload. The current JSON path handles this by checking `bytes[0] != '{'`. The MessagePack path must skip leading null bytes before deserializing.

### 1.5 Update tests

Existing tests that verify command serialization round-trips need updating:
- Replace `JsonSerializer.Serialize/Deserialize` calls with `MessagePackSerializer`
- Test that `Serialize<ICommand>` / `Deserialize<ICommand>` preserves all properties through a round-trip
- Test null-byte padding tolerance (prepend `0x00` bytes, verify deserialization succeeds)
- Test `BatchCommand` with nested commands (recursive Union serialization)

---

## Phase 2: MessagePack for Snapshots

Replace JSON serialization in snapshot persist/restore with MessagePack.

### 2.1 Annotate snapshot and model types

**`SnapshotData.cs`**:
```csharp
[MessagePackObject]
public class SnapshotData
{
    [Key(0)] public int Version { get; set; } = 1;
    [Key(1)] public List<ConfigEntry> Configs { get; set; } = [];
    [Key(2)] public List<Namespace> Namespaces { get; set; } = [];
    [Key(3)] public List<AuditEvent> AuditEvents { get; set; } = [];
    [Key(4)] public long SnapshotIndex { get; set; }
    [Key(5)] public DateTimeOffset Timestamp { get; set; }
}
```

**`ConfigEntry.cs`**, **`Namespace.cs`**, **`AuditEvent.cs`** — all need `[MessagePackObject]` + `[Key(n)]`.

> These models also have `LiteDB.ObjectId Id` properties. ObjectId is a LiteDB type that MessagePack doesn't know about. Options:
> - `[IgnoreMember]` on `Id` — snapshots don't need LiteDB IDs (they're regenerated on restore)
> - Custom formatter for `ObjectId` → `string` round-trip

### 2.2 Custom formatter for `AuditAction`

`AuditAction` is a value object with a private constructor. MessagePack can't construct it directly.

```csharp
public class AuditActionFormatter : IMessagePackFormatter<AuditAction>
{
    public void Serialize(ref MessagePackWriter writer, AuditAction value,
        MessagePackSerializerOptions options)
    {
        writer.Write(value.Value); // "config.created"
    }

    public AuditAction Deserialize(ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        var str = reader.ReadString()!;
        return AuditAction.Parse(str);
    }
}
```

Register via a custom resolver or `[MessagePackFormatter(typeof(AuditActionFormatter))]` attribute on `AuditAction`.

### 2.3 Update snapshot persist/restore

**`ConfigStateMachine.PersistAsync`** — replace `JsonSerializer.SerializeAsync` with `MessagePackSerializer.SerializeAsync`:

```csharp
await using (var writeStream = new FileStream(tempFile, ...))
{
    await MessagePackSerializer.SerializeAsync(writeStream, snapshot, cancellationToken: token);
    fileSize = writeStream.Length;
}
```

**`ConfigStateMachine.RestoreAsync`** — replace `JsonSerializer.DeserializeAsync`:

```csharp
var snapshot = await MessagePackSerializer.DeserializeAsync<SnapshotData>(stream, cancellationToken: token);
```

### 2.4 Bump snapshot version

Change `SnapshotData.Version` default from `1` to `2`. The `RestoreAsync` version check ensures nodes can't accidentally load old JSON snapshots after the format change.

---

## Phase 3: Enable LZ4 Compression

MessagePack-CSharp has built-in LZ4 support — no additional NuGet package needed.

### 3.1 Create shared MessagePack options

```csharp
public static class ConfmanSerializerOptions
{
    public static readonly MessagePackSerializerOptions Instance =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray);
}
```

`Lz4BlockArray` splits the payload into blocks before compressing — better for large payloads than `Lz4Block` which has a 2GB limit.

### 3.2 Pass options to all serialization calls

```csharp
// Serialize
var bytes = MessagePackSerializer.Serialize<ICommand>(command, ConfmanSerializerOptions.Instance);

// Deserialize
return MessagePackSerializer.Deserialize<ICommand>(span, ConfmanSerializerOptions.Instance);

// Snapshot serialize
await MessagePackSerializer.SerializeAsync(stream, snapshot, ConfmanSerializerOptions.Instance, token);

// Snapshot deserialize
var snapshot = await MessagePackSerializer.DeserializeAsync<SnapshotData>(stream, ConfmanSerializerOptions.Instance, token);
```

### 3.3 Verify compression ratio

Run `benchmark_configs.py` with 1MB payloads and check:
- WAL directory size (should be ~60-80% smaller)
- Replication latency (should improve from smaller network payloads)
- Snapshot file sizes

---

## Files to Change

| File | Change |
|------|--------|
| `src/Confman.Api/Confman.Api.csproj` | Add `MessagePack` 3.1.4 NuGet reference |
| `src/Confman.Api/Cluster/Commands/ICommand.cs` | Add `[Union]` attributes |
| `src/Confman.Api/Cluster/Commands/SetConfigCommand.cs` | Add `[MessagePackObject]` + `[Key]` |
| `src/Confman.Api/Cluster/Commands/DeleteConfigCommand.cs` | Add `[MessagePackObject]` + `[Key]` |
| `src/Confman.Api/Cluster/Commands/SetNamespaceCommand.cs` | Add `[MessagePackObject]` + `[Key]` |
| `src/Confman.Api/Cluster/Commands/DeleteNamespaceCommand.cs` | Add `[MessagePackObject]` + `[Key]` |
| `src/Confman.Api/Cluster/Commands/BatchCommand.cs` | Add `[MessagePackObject]` + `[Key]` |
| `src/Confman.Api/Cluster/RaftService.cs` | Replace `JsonSerializer.SerializeToUtf8Bytes` → `MessagePackSerializer.Serialize<ICommand>` |
| `src/Confman.Api/Cluster/BatchingRaftService.cs` | Replace `JsonSerializer.SerializeToUtf8Bytes` → `MessagePackSerializer.Serialize<ICommand>` |
| `src/Confman.Api/Cluster/ConfigStateMachine.cs` | Replace `DeserializeCommand` (MsgPack + null-byte skip), update snapshot persist/restore |
| `src/Confman.Api/Cluster/SnapshotData.cs` | Add `[MessagePackObject]` + `[Key]`, bump version to 2 |
| `src/Confman.Api/Models/ConfigEntry.cs` | Add `[MessagePackObject]` + `[Key]`, `[IgnoreMember]` on `Id` |
| `src/Confman.Api/Models/Namespace.cs` | Add `[MessagePackObject]` + `[Key]`, `[IgnoreMember]` on `Id` |
| `src/Confman.Api/Models/AuditEvent.cs` | Add `[MessagePackObject]` + `[Key]`, `[IgnoreMember]` on `Id` |
| `src/Confman.Api/Models/AuditAction.cs` | Add `[MessagePackFormatter]` attribute, create `AuditActionFormatter` |
| `src/Confman.Api/Cluster/ConfmanSerializerOptions.cs` | **New** — shared MessagePack options with LZ4 |
| `tests/Confman.Tests/` | Update serialization round-trip tests |

## Critical Gotchas

| # | Gotcha | Why it matters |
|---|--------|---------------|
| 1 | **`Serialize<ICommand>`** not `Serialize(command)` | Without the interface type parameter, Union discriminator is lost. Deserialization returns null or throws |
| 2 | **DotNext WAL null-byte padding** | WAL may prepend `0x00` bytes before payload under concurrent load. Must skip before deserialization |
| 3 | **`AuditAction` private constructor** | MessagePack can't construct it. Needs custom `IMessagePackFormatter<AuditAction>` |
| 4 | **`LiteDB.ObjectId` in models** | Not a MessagePack-known type. Use `[IgnoreMember]` on `Id` properties for snapshot serialization |
| 5 | **Wipe data dirs after format change** | Old JSON WAL/snapshots are incompatible. Must `cluster.sh wipe` before starting with new binary |
| 6 | **`BatchCommand.Commands` is `List<ICommand>`** | Nested polymorphic — Union must work recursively. MessagePack handles this natively when using `Serialize<ICommand>` |
| 7 | **`EstimatedBytes` after compression** | `EstimatedBytes` is used for batch size limits. After LZ4, actual serialized size < estimated. This is conservative (safe), not a bug |

## Implementation Order

1. **Add `MessagePack` NuGet** — verify build
2. **Annotate commands** (`[Union]`, `[MessagePackObject]`, `[Key]`) — no behavior change yet
3. **Create `ConfmanSerializerOptions`** — shared options with LZ4
4. **Replace command serialization** (RaftService + BatchingRaftService) — commands now binary
5. **Replace command deserialization** (ConfigStateMachine.DeserializeCommand) — handles null-byte padding
6. **Wipe data dirs, run cluster, verify commands replicate**
7. **Annotate model types** + custom formatters (AuditAction, ObjectId)
8. **Replace snapshot serialization** (PersistAsync + RestoreAsync)
9. **Bump snapshot version** to 2
10. **Wipe data dirs, run cluster, verify snapshots work**
11. **Run benchmark** — measure compression ratio and throughput impact
12. **Update tests**
