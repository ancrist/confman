---
title: "feat: External data path — content-addressed blob store for large config values"
type: feat
date: 2026-02-12
brainstorm: docs/brainstorms/2026-02-12-external-data-path-brainstorm.md
deepened: 2026-02-12
---

# feat: External Data Path — Blob Store

## Enhancement Summary

**Deepened on:** 2026-02-12
**Research agents used:** architecture-strategist, performance-oracle, security-sentinel, code-simplicity-reviewer, data-integrity-guardian, deployment-verification-agent, dotnet-concurrency-specialist, dotnet-performance-analyst, backend-architect, pattern-recognition-specialist
**Web research:** .NET FileStream fsync behavior, Ollama blob storage patterns, Google renameio atomic writes, K4os.Compression.LZ4, BLAKE3.NET

### Critical Fixes Required Before Implementation

| # | Finding | Source | Fix |
|---|---------|--------|-----|
| C1 | `ConfigEntry.Value` is `required string` — blob-backed entries have no inline value | Architecture, Concurrency, Pattern, Data Integrity | Make `Value` nullable: `string?`. Add `resolvedValue` parameter to `ConfigEntryDto.FromModel` |
| C2 | Missing `ApplyAsync` implementation on `SetConfigBlobRefCommand` | Architecture, Pattern | Every `ICommand` must implement `ApplyAsync`. Add method calling `store.SetWithAuditAsync` |
| C3 | Missing `[JsonDerivedType]` attribute alongside `[Union(5)]` | Pattern Recognition | Add `[JsonDerivedType(typeof(SetConfigBlobRefCommand), "set_config_blob_ref")]` — JSON polymorphism tests will fail without it |
| C4 | Path traversal via blobId parameter (`../../etc/passwd`) | Security Sentinel | Validate with regex: `^[0-9a-f]{64}$`. Reject before any filesystem operation |
| C5 | Double compression: MessagePack LZ4 + blob LZ4 | Performance Oracle, .NET Perf | Use direct K4os.Compression.LZ4 for blobs. Keep MessagePack LZ4 only for Raft commands |
| C6 | Hash over compressed data couples BlobId to LZ4 version | Architecture, .NET Perf | Hash raw value: `SHA256(value)` not `SHA256(LZ4(value))`. Hash is stable across compression changes |
| C7 | `byte[]` returns force LOH allocations for large blobs | Performance Oracle, .NET Perf | Use `Stream`-based APIs: `OpenReadAsync` returns `Stream?`, `PutAsync` accepts `Stream` |

### High-Priority Improvements

| # | Finding | Source | Fix |
|---|---------|--------|-----|
| H1 | Thundering herd on missing blob reads — N readers trigger N peer fetches | Concurrency | Per-BlobId `SemaphoreSlim` coalescer with double-check pattern |
| H2 | Controller bloat — blob orchestration logic in ConfigController | Backend Arch, Pattern, Simplicity | Extract `IConfigWriteService` + `IBlobValueResolver` services |
| H3 | Quorum counting uses `Task.WhenAll` (waits for all, not quorum) | Concurrency | `Interlocked` counter + `TaskCompletionSource` for quorum-first signaling |
| H4 | Cluster token comparison is not constant-time | Security | `CryptographicOperations.FixedTimeEquals()` |
| H5 | No max blob size limit | Security | Add `BlobStore:MaxBlobSizeBytes` (default: 50MB) |
| H6 | `SetBlobRefWithAuditAsync` has 10 parameters — anti-pattern | Pattern, Simplicity | Reuse existing `SetWithAuditAsync` — detect `IsBlobBacked` inside the method |

### Simplification Opportunities

- Drop `Checksum` field (redundant with content-addressed BlobId) — saves 1 field per command + model
- Drop `Length` field (no consumer in v1) — or derive from blob file on demand
- Drop HEAD endpoint (check existence via GET + 404)
- Hardcode threshold as `const int InlineThreshold = 65536` (YAGNI: no runtime tuning needed)
- Inline `BlobIntegrity` methods into `LocalBlobStore` (single consumer)
- Use `ClusterTokenAuthenticationHandler` as ASP.NET auth scheme (not custom middleware)
- Estimated savings: ~2 fewer files, ~200 LOC reduction

---

## Overview

Separate large config values (≥64KB) from the Raft consensus path by storing blob bytes in a local content-addressed blob store on each node. Raft replicates only lightweight metadata pointers (~200 bytes) instead of full values. This reduces WAL entry size by 2500× and snapshot size by 2500× for 1MB payloads.

## Problem Statement

Current system replicates full config values through Raft:

- **WAL bloat**: 1MB config = ~500KB WAL entry (LZ4). 1000 entries = ~500MB WAL before compaction.
- **Snapshot size**: Full-state snapshots serialize all values. 1000 × 1MB = ~500MB snapshot, taking ~30s to create.
- **Snapshot starvation**: Large snapshots block the state machine thread, starving heartbeats and causing spurious elections (documented in `docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md`).
- **Batch inefficiency**: 4MB batch limit fits only ~8 × 1MB commands per Raft round-trip.
- **LOH pressure**: Serializing/deserializing large payloads fragments the Large Object Heap.

## Proposed Solution

**Model A + Pattern 1** (from `EXT_DATA_PATH.md`):

- **Model A**: Durable blob first, then Raft pointer commit. Never commit a pointer before blob durability quorum.
- **Pattern 1**: Local blob store on each node (`data-{port}/blobs/`) with out-of-band leader→follower blob replication.

**Write flow (large value):**
1. Client sends value to leader
2. Leader streams request body → single-pass `IncrementalHash(SHA256)` + `LZ4Stream.Encode` → temp file → fsync → rename
3. Leader pushes compressed blob to all followers in parallel via `PUT /internal/blobs/{blobId}`
4. Wait for quorum ACKs (2 of 3 for 3-node cluster) — each ACK means fsync'd + hash validated
5. Build `SetConfigBlobRefCommand { BlobId, ... }` (~200 bytes, metadata only)
6. Replicate pointer via Raft (tiny entry, batches efficiently)
7. Return success after Raft commit

**Read flow (blob-backed):**
1. ReadBarrierMiddleware ensures linearizability (unchanged)
2. Fetch `ConfigEntry` metadata from LiteDB — contains `BlobId`, no value
3. `IBlobValueResolver.ResolveAsync(entry)` resolves blob from local disk
4. If missing locally: sync fetch from peer via `GET /internal/blobs/{blobId}` (deduplicated per-BlobId)
5. Decompress LZ4 → return as string

> **Key design insight (from concurrency review):** Content-addressed storage is inherently safe for concurrent writes. Two concurrent `PutAsync` for the same BlobId both write identical content to unique temp files, then atomically rename. The last rename wins, and since content is identical, the outcome is correct regardless of ordering. This eliminates the need for per-blob write locks.

## Technical Approach

### Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    ConfigController                      │
│  PUT /config/{key}                                      │
│  ┌──────────────┐                                       │
│  │ Delegates to  │──► IConfigWriteService                │
│  └──────────────┘                                       │
└─────────────────────┬───────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────┐
│              ConfigWriteService                          │
│  ┌──────────────┐    ┌──────────────────────────────┐   │
│  │ value < 64KB │───►│ SetConfigCommand (inline)     │   │
│  └──────────────┘    └──────────────────────────────┘   │
│  ┌──────────────┐    ┌──────────────────────────────┐   │
│  │ value ≥ 64KB │───►│ Blob Path:                    │   │
│  └──────────────┘    │  1. LocalBlobStore.PutAsync   │   │
│                      │  2. BlobReplicator.Quorum     │   │
│                      │  3. SetConfigBlobRefCommand   │   │
│                      └──────────────────────────────┘   │
└─────────────────────┬───────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────┐
│              BatchingRaftService                         │
│  Serializes pointer command (~200 bytes)                 │
│  Batches up to 20,000 pointers per 4MB round-trip       │
└─────────────────────┬───────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────┐
│              ConfigStateMachine.ApplyAsync               │
│  Deserializes SetConfigBlobRefCommand                    │
│  Stores metadata-only ConfigEntry in LiteDB              │
└─────────────────────────────────────────────────────────┘
```

### Implementation Phases

#### Phase 1: Blob Subsystem Abstractions

**Goal:** Build the core blob storage, compression, and integrity primitives. No behavior changes yet.

**New files:**

`src/Confman.Api/Storage/Blobs/IBlobStore.cs`:
```csharp
public interface IBlobStore
{
    Task<bool> ExistsAsync(string blobId, CancellationToken ct = default);

    /// <summary>
    /// Stores compressed blob bytes from a stream. Computes hash + LZ4 in a single pass.
    /// Returns the computed BlobId (SHA256 of uncompressed source).
    /// </summary>
    Task<string> PutFromStreamAsync(Stream source, long contentLength, CancellationToken ct = default);

    /// <summary>
    /// Stores pre-compressed blob bytes (received from leader during replication).
    /// Validates that SHA256 of uncompressed content matches blobId.
    /// </summary>
    Task PutCompressedAsync(string blobId, Stream source, long contentLength, CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream over the compressed blob file. Caller owns the stream.
    /// Returns null if blob does not exist.
    /// </summary>
    Task<Stream?> OpenReadAsync(string blobId, CancellationToken ct = default);

    Task DeleteAsync(string blobId, CancellationToken ct = default);
    IAsyncEnumerable<string> ListBlobIdsAsync(CancellationToken ct = default);
}
```

`src/Confman.Api/Storage/Blobs/LocalBlobStore.cs`:
- Content-addressed file storage under `data-{port}/blobs/`
- Directory structure: `{blobId[0..2]}/{blobId}` (2-char prefix subdirectory)
- Atomic writes: temp file (unique Guid suffix) → `RandomAccess.FlushToDisk(handle)` → `File.Move(overwrite: false)`
- Idempotent: PutAsync is a no-op if blob already exists (same hash = same content)
- Thread-safe: concurrent PutAsync for same BlobId handled by atomic rename + `IOException` catch
- **BlobId validation**: Reject any blobId not matching `^[0-9a-f]{64}$` (path traversal protection)
- `FileStreamOptions`: `BufferSize = 81920` (under LOH threshold), `FileOptions.Asynchronous | SequentialScan`, `PreallocationSize = contentLength * 0.6`
- Single-pass hash+compress: `IncrementalHash(SHA256)` + `LZ4Stream.Encode` in one pipeline
- Temp file cleanup on startup (orphans from crash)

`src/Confman.Api/Storage/Blobs/BlobCompression.cs`:
```csharp
public static class BlobCompression
{
    /// <summary>
    /// Single-pass: stream source → IncrementalHash(SHA256) + LZ4Stream.Encode → destination.
    /// Returns hex-encoded SHA256 hash of the uncompressed data.
    /// Uses 80KB ArrayPool buffer — zero LOH allocations.
    /// </summary>
    public static Task<string> HashAndCompressAsync(Stream source, Stream destination, CancellationToken ct);

    /// <summary>Decompresses LZ4 stream to destination. Streaming, no full materialization.</summary>
    public static Task DecompressAsync(Stream source, Stream destination, CancellationToken ct);

    /// <summary>Validates SHA256 of uncompressed stream matches expected blobId.</summary>
    public static Task<bool> ValidateAsync(string blobId, Stream compressedSource, CancellationToken ct);
}
```

`src/Confman.Api/Storage/Blobs/BlobStoreOptions.cs`:
```csharp
public sealed class BlobStoreOptions
{
    public const string SectionName = "BlobStore";
    public const int DefaultThreshold = 65_536;

    public bool Enabled { get; set; } = true;
    public int InlineThresholdBytes { get; set; } = DefaultThreshold;
    public string ClusterToken { get; set; } = string.Empty;
    public int MaxBlobSizeBytes { get; set; } = 50 * 1024 * 1024; // 50MB
}
```

**Config additions to `appsettings.json`:**
```json
{
  "BlobStore": {
    "Enabled": true,
    "InlineThresholdBytes": 65536,
    "ClusterToken": "confman_cluster_secret",
    "MaxBlobSizeBytes": 52428800
  }
}
```

**DI registration in `Program.cs`:**
```csharp
// IOptions<BlobStoreOptions> with startup validation
builder.Services.AddOptions<BlobStoreOptions>()
    .Bind(builder.Configuration.GetSection(BlobStoreOptions.SectionName))
    .Validate(o => !o.Enabled || !string.IsNullOrEmpty(o.ClusterToken),
        "BlobStore:ClusterToken required when enabled")
    .Validate(o => o.InlineThresholdBytes >= 1024,
        "InlineThresholdBytes must be >= 1024");

// Type mapping — matches LiteDbConfigStore pattern
builder.Services.AddSingleton<IBlobStore, LocalBlobStore>();
```

**NuGet dependency:**
```xml
<PackageReference Include="K4os.Compression.LZ4.Streams" Version="1.3.8" />
```

**Tests:**
- `tests/Confman.Tests/LocalBlobStoreTests.cs`
  - Put/Get round-trip (stream-based)
  - Idempotent put (same BlobId twice)
  - Exists check
  - Delete
  - List all blob IDs
  - Concurrent put for same BlobId (thread safety via atomic rename)
  - Hash validation (corrupt data detection)
  - BlobId validation (path traversal rejected)
  - Temp file cleanup on startup

**Acceptance criteria:**
- [ ] `IBlobStore` interface defined with streaming APIs
- [ ] `LocalBlobStore` passes all unit tests
- [ ] `BlobCompression.HashAndCompressAsync` single-pass pipeline verified
- [ ] Blob directory created on startup
- [ ] BlobId validation rejects non-hex-64 inputs
- [ ] No behavior changes to existing system

<details>
<summary>Research Insights — Phase 1</summary>

**Streaming single-pass pipeline** (.NET Performance Analyst):
Instead of: (1) read body into `byte[]`, (2) hash `byte[]`, (3) compress `byte[]`, (4) write `byte[]` to file — do a single-pass stream: `request.Body → IncrementalHash + LZ4Stream.Encode → FileStream`. Zero LOH allocations. 80KB `ArrayPool` rented buffer stays on SOH.

**Hash stability** (Architecture Strategist):
`SHA256(raw_value)` ensures BlobId is deterministic regardless of LZ4 compression level or library version changes. If you hash compressed data, changing LZ4 levels in the future produces different BlobIds for identical logical values.

**FileStream configuration** (.NET Performance Analyst):
- `PreallocationSize = (long)(contentLength * 0.6)` — pre-allocates contiguous disk blocks, avoids fragmentation
- `RandomAccess.FlushToDisk(SafeFileHandle)` — explicit fsync, more precise than `Flush(true)`
- `BufferSize = 81920` (80KB) — stays under 85,000-byte LOH threshold; default 4KB causes excessive syscalls
- `FileOptions.Asynchronous` — uses thread pool async I/O on macOS/Linux (no IOCP)

**Atomic write race** (Concurrency Specialist):
Two concurrent `PutAsync` for same BlobId: each writes to unique temp file (Guid suffix), both `File.Move(overwrite: false)`. If one fails with `IOException` because the other completed first, catch and delete temp — content is identical, outcome is correct.

**Direct LZ4** (.NET Performance Analyst):
Use `K4os.Compression.LZ4.Streams` directly, NOT MessagePack LZ4. MessagePack `Lz4BlockArray` adds framing overhead and requires deserialization through `MessagePackSerializer.Deserialize<string>` which allocates the full string on the managed heap. Direct LZ4 enables streaming decompress to HTTP response.

</details>

---

#### Phase 2: Internal Blob Endpoints

**Goal:** Add HTTP endpoints for inter-node blob transfer, secured with cluster token auth scheme.

**New files:**

`src/Confman.Api/Controllers/InternalBlobController.cs`:
```csharp
[ApiController]
[Route("internal/blobs")]
[Authorize(Policy = "ClusterInternal")]
[ApiExplorerSettings(IgnoreApi = true)]  // Hide from Swagger
public class InternalBlobController : ControllerBase
{
    // PUT /internal/blobs/{blobId} — receive compressed blob from leader
    //   - Validate blobId matches ^[0-9a-f]{64}$ (path traversal protection)
    //   - Stream Request.Body directly to temp file (no in-memory buffering)
    //   - Validate SHA256(decompressed) matches blobId
    //   - Atomic rename into place
    //   - Return 200 OK (or 204 if already exists)

    // GET /internal/blobs/{blobId} — fetch blob (for missing blob repair)
    //   - Return File(stream, "application/octet-stream") — zero-copy via sendfile(2) on Linux
    //   - Return 404 if not found
}
```

**Auth scheme for internal endpoints:**

`src/Confman.Api/Auth/ClusterTokenAuthenticationHandler.cs`:
- ASP.NET Core `AuthenticationHandler<ClusterTokenAuthOptions>` — integrates with `[Authorize]`
- Validates `X-Cluster-Token` header against `BlobStoreOptions.ClusterToken`
- Uses `CryptographicOperations.FixedTimeEquals()` for constant-time comparison
- Returns 401 if missing/invalid
- Register as secondary auth scheme alongside existing `ApiKey` scheme
- Add `"ClusterInternal"` authorization policy

**Acceptance criteria:**
- [ ] PUT streams request body to disk (no LOH allocation)
- [ ] PUT validates blobId format before any filesystem operation
- [ ] GET returns `FileStreamResult` (zero-copy on Linux via sendfile)
- [ ] All endpoints reject requests without valid cluster token (constant-time comparison)
- [ ] PUT is idempotent (uploading same blob twice succeeds)
- [ ] BlobId path traversal attempts return 400
- [ ] Max blob size enforced (rejects bodies > `MaxBlobSizeBytes`)

<details>
<summary>Research Insights — Phase 2</summary>

**Zero-copy serving** (.NET Performance Analyst):
Kestrel's `File(stream, ...)` for `FileStream` instances triggers `IHttpResponseBodyFeature.SendFileAsync`, which on Linux uses `sendfile(2)`. Data moves from filesystem page cache directly to socket buffer without entering managed memory.

**Path traversal protection** (Security Sentinel):
BlobId is user-influenced (derived from content, but the blobId parameter in the URL is client-provided on GET). Validate with `Regex.IsMatch(blobId, "^[0-9a-f]{64}$")` before any `Path.Combine` or filesystem call. Without this, `GET /internal/blobs/../../etc/passwd` could read arbitrary files.

**Constant-time comparison** (Security Sentinel):
`string.Equals()` short-circuits on first mismatched character → timing side-channel. Use:
```csharp
var expected = Encoding.UTF8.GetBytes(options.ClusterToken);
var provided = Encoding.UTF8.GetBytes(tokenHeader);
CryptographicOperations.FixedTimeEquals(expected, provided);
```

**Auth scheme pattern** (Backend Architect):
Use a proper ASP.NET Core `AuthenticationHandler<T>` registered alongside the existing `ApiKey` scheme. This integrates with `[Authorize(Policy = "ClusterInternal")]` attributes, keeps the auth pipeline consistent, and doesn't interfere with existing API key auth on `/api/*` routes. Internal endpoints at `/internal/*` are already excluded from `ReadBarrierMiddleware` (which only applies to `/api`).

**Drop HEAD endpoint** (Code Simplicity):
HEAD adds a method that saves one HTTP body transfer. In practice, the GET path with 404 serves the same purpose. If you need existence checks, the GET response with 404 is sufficient for v1.

</details>

---

#### Phase 3: Blob Replication

**Goal:** Implement leader→follower blob push with quorum-first signaling.

**New files:**

`src/Confman.Api/Storage/Blobs/IBlobReplicator.cs`:
```csharp
public interface IBlobReplicator
{
    /// <summary>
    /// Pushes compressed blob to cluster peers and waits for durability quorum.
    /// Returns when (clusterSize/2+1)-1 peers have confirmed durable storage.
    /// Throws BlobReplicationException if quorum cannot be achieved within timeout.
    /// Does NOT await slow followers after quorum — remaining pushes continue in background.
    /// </summary>
    Task ReplicateAsync(string blobId, CancellationToken ct = default);
}
```

`src/Confman.Api/Storage/Blobs/PeerBlobReplicator.cs`:
- Discovers cluster members from DotNext `IRaftCluster` (existing DI service)
- Excludes self from replication targets
- Streams blob from local file to each follower via `StreamContent(FileStream)` — zero LOH allocations
- **Quorum-first signaling**: `Interlocked.Increment(ref ackCount)` + `TaskCompletionSource` — signals as soon as quorum reached
- Fire-and-forget: remaining background pushes continue after quorum (best-effort, not cancelled)
- Fail-fast: if `totalFollowers - failCount < requiredAcks - ackCount`, signal failure immediately
- Timeout: 10 seconds per attempt (matches Raft request timeout)
- If quorum unreachable: throws `BlobReplicationException` → controller returns 503
- Sets `X-Cluster-Token` header per-request (not baked into HttpClient defaults)
- HTTP/2 for multiplexed pushes: `DefaultRequestVersion = HttpVersion.Version20`

**DI registration:**
```csharp
builder.Services.AddSingleton<IBlobReplicator, PeerBlobReplicator>();
builder.Services.AddHttpClient("BlobReplication", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestVersion = HttpVersion.Version20;
    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    MaxConnectionsPerServer = 2,
    EnableMultipleHttp2Connections = true,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    ConnectTimeout = TimeSpan.FromSeconds(5),
});
```

**Tests:**
- `tests/Confman.Tests/PeerBlobReplicatorTests.cs`
  - Quorum achieved with 1 of 2 followers responding → success
  - Both followers respond → success
  - No followers respond → throws BlobReplicationException
  - 1 follower times out, 1 responds → success (quorum met)
  - Blob already exists on follower (idempotent) → counts as ACK
  - Single-node cluster (no followers) → immediate success

**Acceptance criteria:**
- [ ] Streams blob from local file (not from memory buffer)
- [ ] Returns after quorum ACKs (not all nodes) — uses Interlocked + TCS
- [ ] Remaining pushes continue in background after quorum
- [ ] Handles follower timeout gracefully (fail-fast when quorum impossible)
- [ ] Throws clear exception when quorum unreachable
- [ ] Single-node cluster works (no replication needed)

<details>
<summary>Research Insights — Phase 3</summary>

**Quorum-first vs Task.WhenAll** (Concurrency Specialist):
`Task.WhenAll` waits for ALL followers, degrading latency to the slowest node. Instead, use:
```csharp
var quorumTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
// Per-peer: on success → Interlocked.Increment(ref ackCount) >= required → TrySetResult
// Per-peer: on failure → if remaining can't reach quorum → TrySetException
await Task.WhenAny(quorumTcs.Task, timeoutTask);
```
After quorum, `_ = Task.WhenAll(pushTasks).ContinueWith(...)` — fire-and-forget remaining pushes.

**CancellationToken scope** (Concurrency Specialist):
Background pushes after quorum should NOT use the HTTP request's `CancellationToken`. Use a separate CTS that's only cancelled on application shutdown. Otherwise, the caller's request completing cancels the best-effort push to the remaining follower.

**StreamContent from FileStream** (.NET Performance Analyst):
`new StreamContent(fileStream, bufferSize: 81920)` does NOT buffer the body — it streams on-demand from disk to socket. Zero LOH allocations. Open one `FileStream` per peer (each `StreamContent` consumes the stream independently).

**Per-request token header** (Backend Architect):
Don't bake `X-Cluster-Token` into `DefaultRequestHeaders` — it's resolved at DI time and won't rotate. Set it per-request: `request.Headers.Add("X-Cluster-Token", _options.Value.ClusterToken)`.

</details>

---

#### Phase 4: Raft Command and State Model Changes

**Goal:** Add `SetConfigBlobRefCommand`, update `ConfigEntry` model, modify state machine.

**Modified files:**

`src/Confman.Api/Cluster/Commands/ICommand.cs` — Add union case + JSON discriminator:
```csharp
[Union(5, typeof(SetConfigBlobRefCommand))]  // Next index after BatchCommand(4)
[JsonDerivedType(typeof(SetConfigBlobRefCommand), "set_config_blob_ref")]
```

**New file:**

`src/Confman.Api/Cluster/Commands/SetConfigBlobRefCommand.cs`:
```csharp
[MessagePackObject]
public sealed record SetConfigBlobRefCommand : ICommand
{
    [Key(0)] public required string Namespace { get; init; }
    [Key(1)] public required string Key { get; init; }
    [Key(2)] public required string BlobId { get; init; }
    [Key(3)] public string Type { get; init; } = "string";
    [Key(4)] public required string Author { get; init; }
    [Key(5)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [IgnoreMember] public int EstimatedBytes => 64 + (BlobId?.Length ?? 0);

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default)
    {
        var entry = new ConfigEntry
        {
            Namespace = Namespace,
            Key = Key,
            Value = null,   // Blob-backed: no inline value
            BlobId = BlobId,
            Type = Type,
            UpdatedBy = Author,
            UpdatedAt = Timestamp,
        };
        await store.SetWithAuditAsync(entry, Author, Timestamp, auditEnabled, ct);
    }
}
```

> **Note:** `Checksum` and `Length` fields removed — BlobId (SHA256) IS the checksum, and length can be derived from the blob file on demand. This follows the simplicity reviewer's recommendation to eliminate redundant metadata.

**Modified: `src/Confman.Api/Models/ConfigEntry.cs`** — Make Value nullable + add blob fields:
```csharp
// CRITICAL CHANGE: required string → string?
[Key(2)]
[BsonIgnore]
public string? Value { get; set; }  // null for blob-backed entries

[Key(7)] public string? BlobId { get; set; }

[IgnoreMember] public bool IsBlobBacked => BlobId is not null;
```

> **Why nullable Value?** `ConfigEntry.Value` was `required string`. For blob-backed entries there is no inline value. The null-forgiving operator in the `Value` getter's lazy decompression path (`return _value!`) would NPE when both `_value` and `_compressedValue` are null. Making it `string?` is the cleanest fix — callers must now handle the null case, which the `IBlobValueResolver` does.

**Modified: `src/Confman.Api/Storage/IConfigStore.cs`** — No new method needed:
- Existing `SetWithAuditAsync(ConfigEntry entry, ...)` handles both inline and blob-backed entries
- Inside `LiteDbConfigStore`, detect `entry.IsBlobBacked` to decide audit value representation

**Modified: `src/Confman.Api/Controllers/ConfigController.cs`** — Update `ConfigEntryDto.FromModel`:
```csharp
public static ConfigEntryDto FromModel(ConfigEntry entry, string? resolvedValue = null) => new()
{
    Value = resolvedValue ?? entry.Value ?? "",
    // ... rest of fields
};
```

**Modified: `src/Confman.Api/Cluster/ConfigStateMachine.cs`** — `DeserializeCommand` already handles any `ICommand` via MessagePack Union. No changes needed.

**Tests:**
- Add to `tests/Confman.Tests/CommandTests.cs` (same file as existing command tests):
  - `SetConfigBlobRefCommand_CreatesBlobBackedEntry`
  - `SetConfigBlobRefCommand_UpdatesExistingEntry`
  - Round-trip serialization via MessagePack (Union discriminator preserved)
  - Round-trip serialization via JSON (`JsonDerivedType` preserved)
  - `ConfigEntry_IsBlobBacked_TrueWhenBlobIdSet`
  - `ConfigEntry_Value_NullForBlobBacked`

**Acceptance criteria:**
- [ ] `SetConfigBlobRefCommand` serializes/deserializes correctly via MessagePack Union AND JSON
- [ ] `SetConfigBlobRefCommand.ApplyAsync` calls existing `SetWithAuditAsync`
- [ ] `ConfigEntry.IsBlobBacked` returns true when `BlobId` is set
- [ ] `ConfigEntry.Value` is nullable — blob-backed entries have `Value = null`
- [ ] `ConfigEntryDto.FromModel` accepts optional `resolvedValue` parameter
- [ ] Existing `SetConfigCommand` path unchanged
- [ ] All existing 66 tests still pass (after fixing nullable Value references)

<details>
<summary>Research Insights — Phase 4</summary>

**Reuse `SetWithAuditAsync`** (Pattern Recognition, Code Simplicity):
The original plan proposed a separate `SetBlobRefWithAuditAsync` with 10 parameters. The existing `SetWithAuditAsync(ConfigEntry, author, timestamp, ct)` already handles the full workflow (upsert + audit). Just pass a `ConfigEntry` with `BlobId` set and `Value = null`. Inside the store, detect `IsBlobBacked` to format audit values as `[blob:{blobId}]`.

**`EstimatedBytes` should be computed** (Pattern Recognition):
Existing commands compute from actual content size: `64 + (Value?.Length ?? 0)`. Use `64 + (BlobId?.Length ?? 0)` for consistency, not a fixed `200`.

**JSON polymorphism required** (Pattern Recognition):
`ICommand` has both `[Union]` (MessagePack) and `[JsonDerivedType]` (System.Text.Json) attributes. Tests `Commands_SerializeWithPolymorphism` and `AllCommandTypes_SerializeCorrectly` in `CommandTests.cs` verify JSON round-trips. Without `[JsonDerivedType]`, these tests fail silently or throw.

**Backward-compatible snapshot option** (Data Integrity Guardian):
The model changes (adding nullable `BlobId` field) are additive — Version 2 snapshots can still deserialize with `BlobId = null`. Consider supporting BOTH Version 2 and 3 snapshots in `RestoreAsync` instead of hard cutover. This avoids requiring a full cluster wipe.

</details>

---

#### Phase 5: Write Path & Read Path Integration

**Goal:** Extract service layer and wire blob path into config operations.

**New files:**

`src/Confman.Api/Services/IConfigWriteService.cs`:
```csharp
public interface IConfigWriteService
{
    /// <summary>
    /// Writes a config entry through the appropriate path (inline or blob).
    /// For blob path: stores locally, replicates to quorum, then Raft commits pointer.
    /// For inline path: Raft commits full value directly.
    /// </summary>
    Task<ConfigWriteResult> WriteAsync(
        string ns, string key, string value, string type,
        string author, CancellationToken ct = default);
}

public sealed record ConfigWriteResult(
    bool Success, string Namespace, string Key,
    string Value, string Type, DateTimeOffset Timestamp,
    string Author, string? ErrorDetail = null);
```

`src/Confman.Api/Services/ConfigWriteService.cs`:
- Encapsulates threshold check, blob storage, quorum replication, Raft commit
- Injects `IBlobStore`, `IBlobReplicator`, `IRaftService`, `IOptions<BlobStoreOptions>`
- Blob path: `PutFromStreamAsync` → `ReplicateAsync` → `SetConfigBlobRefCommand` → Raft
- Inline path: `SetConfigCommand` → Raft (unchanged)
- Logs warning on Raft failure after blob quorum (orphan blob for future GC)

`src/Confman.Api/Services/IBlobValueResolver.cs`:
```csharp
public interface IBlobValueResolver
{
    /// <summary>
    /// Resolves the string value for a ConfigEntry.
    /// Inline entries: returns entry.Value directly.
    /// Blob-backed entries: reads from local store or fetches from peer (deduplicated).
    /// </summary>
    Task<string?> ResolveAsync(ConfigEntry entry, CancellationToken ct = default);
}
```

`src/Confman.Api/Services/BlobValueResolver.cs`:
- Per-BlobId deduplication gate: `ConcurrentDictionary<string, SemaphoreSlim>` — prevents thundering herd
- Double-check pattern: re-check local store after acquiring gate
- Fetches from peers sequentially (try each until one succeeds)
- Validates hash after peer fetch
- Caches locally after successful fetch
- Removes gate from dictionary after fetch completes (content-addressed → fetched once)

**Modified: `src/Confman.Api/Controllers/ConfigController.cs`**:

Write path (simplified controller):
```csharp
[HttpPut("{key}")]
public async Task<ActionResult<ConfigEntryDto>> Set(
    string @namespace, string key, SetConfigRequest request, CancellationToken ct)
{
    if (!_raft.IsLeader) return await ForwardToLeaderAsync(ct);

    var author = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
    var result = await _writeService.WriteAsync(
        @namespace, key, request.Value, request.Type ?? "string", author, ct);

    if (!result.Success)
        return Problem(title: "Replication failed", detail: result.ErrorDetail, statusCode: 503);

    return Ok(ConfigEntryDto.FromModel(...));
}
```

Read path (simplified controller):
```csharp
[HttpGet("{key}")]
public async Task<ActionResult<ConfigEntryDto>> Get(
    string @namespace, string key, CancellationToken ct)
{
    var entry = await _store.GetAsync(@namespace, key, ct);
    if (entry is null) return NotFound();

    var value = await _blobResolver.ResolveAsync(entry, ct);
    if (value is null) return StatusCode(503, "Blob temporarily unavailable");

    return Ok(ConfigEntryDto.FromModel(entry, value));
}
```

**Modified: dashboard endpoint in `Program.cs`** (`/api/v1/configs`):
- Blob-backed entries: return placeholder value `"[blob:{blobId}, {fileSize}]"` instead of resolving
- Add `isBlobBacked` and `blobId` fields to dashboard DTO

**Tests:**
- `tests/Confman.Tests/ConfigWriteServiceTests.cs`
  - Large value → blob path triggered (store + replicate + Raft)
  - Small value → inline path triggered
  - Blob replication failure → returns error result
  - Raft failure after blob quorum → returns error, logs orphan warning
  - BlobStore:Enabled=false → always inline path
  - Threshold boundary: exactly 64KB → blob path; 64KB - 1 → inline
- `tests/Confman.Tests/BlobValueResolverTests.cs`
  - Inline entry → returns entry.Value directly
  - Blob-backed, local hit → returns decompressed value
  - Blob-backed, local miss → fetches from peer, caches locally
  - Thundering herd: 10 concurrent resolves for same missing blob → only 1 peer fetch
  - All peers fail → returns null

**Acceptance criteria:**
- [ ] ConfigController delegates to `IConfigWriteService` and `IBlobValueResolver`
- [ ] Controller has no blob orchestration logic (thin controller)
- [ ] Thundering herd deduplication verified (concurrent resolve test)
- [ ] Dashboard shows blob metadata placeholder (not null value)
- [ ] Ghost blob logged when Raft fails after blob quorum
- [ ] All existing API tests pass (backward compatible)

<details>
<summary>Research Insights — Phase 5</summary>

**Service extraction** (Backend Architect):
The controller would otherwise accumulate 7 responsibilities: threshold check, compression, hashing, local storage, quorum replication, command construction, and Raft replication. `ConfigWriteService` encapsulates all blob orchestration. `BlobValueResolver` encapsulates read-path resolution + peer fetch. Controller stays at 3 dependencies.

**Thundering herd** (Concurrency Specialist):
100 concurrent readers for a missing blob would trigger 100 HTTP GETs to the same peer. Fix with per-BlobId `SemaphoreSlim(1)` and double-check pattern:
```csharp
// Fast path: already cached
if (local hit) return;
// Per-blobId gate: only one network fetch
var gate = _locks.GetOrAdd(blobId, _ => new SemaphoreSlim(1, 1));
await gate.WaitAsync(ct);
try { /* double-check, fetch, cache */ }
finally { gate.Release(); _locks.TryRemove(blobId, out _); }
```

**Ghost blob handling** (Concurrency Specialist):
Leader step-down between blob quorum (step 4) and Raft commit (step 6) creates ghost blobs — durable on quorum but never referenced by any Raft entry. These are harmless (content-addressed, immutable) but waste disk. Log a warning; future GC compares blob IDs on disk vs LiteDB references.

**Read-path LOH allocation** (.NET Performance Analyst):
The read path must decompress LZ4 and return a JSON string — this unavoidably materializes the full value in managed memory. For v1 this is acceptable. Future optimization: `/api/v1/.../config/{key}/raw` endpoint that streams decompressed blob as `application/octet-stream`.

</details>

---

#### Phase 6: Snapshot Adaptation

**Goal:** Update snapshot format to store metadata-only for blob-backed entries.

**Modified: `src/Confman.Api/Cluster/SnapshotData.cs`**:
```csharp
[Key(0)] public int Version { get; set; } = 3; // Bumped from 2

// New: blob manifest for post-restore diagnostics
[Key(6)] public List<string> BlobManifest { get; set; } = [];
```

**Modified: `src/Confman.Api/Cluster/ConfigStateMachine.cs`**:
- `RestoreAsync`:
  - Accept Version 3 (new format with blob-backed entries)
  - **Also accept Version 2** (backward compatible — additive model changes mean V2 entries have null BlobId, which is correct for inline entries)
  - After restore, log missing blob count: `"Snapshot restored with {Count} missing blobs — will fetch on first access"`
- `PersistAsync`:
  - Blob-backed entries: `BlobId` populated, `CompressedValue` null (metadata only)
  - Inline entries: `CompressedValue` populated as before
  - Populate `BlobManifest` with all BlobIds from blob-backed entries

**Modified: `src/Confman.Api/Storage/LiteDbConfigStore.cs`**:
- `GetAllConfigsAsync`: Return entries as-is (metadata-only for blob-backed)
- `RestoreFromSnapshotAsync`: Restore metadata entries. Blob bytes already on disk (survived restart) or fetched lazily on first read

**Deployment procedure:**
1. Stop all nodes
2. Build with new code
3. **Optional**: wipe data dirs if you want a clean start — but NOT required (backward compatible)
4. Start all nodes

**Tests:**
- Add to `tests/Confman.Tests/SnapshotTests.cs`:
  - Snapshot persist with mix of inline and blob-backed entries
  - Snapshot restore creates correct metadata entries
  - Version 2 snapshot loads correctly (backward compatible)
  - Snapshot size proportional to metadata count, not value sizes
  - BlobManifest populated with all blob IDs

**Acceptance criteria:**
- [ ] Snapshot Version bumped to 3
- [ ] Blob-backed entries serialized as metadata only (no value bytes)
- [ ] Inline entries serialized as before
- [ ] Version 2 snapshots still loadable (backward compatible)
- [ ] BlobManifest populated for post-restore diagnostics
- [ ] Post-snapshot GC still runs (LOH compaction)

<details>
<summary>Research Insights — Phase 6</summary>

**Backward-compatible snapshots** (Data Integrity Guardian):
The model changes are additive — new nullable fields `BlobId` etc. Version 2 snapshots deserialize with `BlobId = null`, which is correct for inline entries. Supporting both versions eliminates the need for a full cluster wipe on upgrade. This matches the brainstorm's "hard cutover" decision but improves it with zero downtime.

**Blob manifest** (Concurrency Specialist):
After restore, the node knows which blobs it should have locally. Without a manifest, it can only discover missing blobs reactively on first read. The manifest enables proactive logging (and future: background reconciliation):
```csharp
var missing = snapshot.BlobManifest
    .Where(id => !await _blobStore.ExistsAsync(id))
    .ToList();
_logger.LogWarning("Restored with {Count} missing blobs", missing.Count);
```

**Full cluster wipe loses all blobs** (Data Integrity Guardian):
If all nodes wipe data dirs simultaneously, all blobs are permanently lost. The metadata pointers in the new snapshot reference blobs that no node has. Lazy fetch has no peer to fetch from. Mitigation: document that full cluster wipe requires re-ingesting data (same as today — wiping all data dirs loses everything).

</details>

---

### Phase Summary

| Phase | Deliverable | New Files | Modified Files | Tests |
|-------|-------------|-----------|----------------|-------|
| 1 | Blob store abstractions + streaming | 4 | 1 (Program.cs) | ~10 |
| 2 | Internal blob endpoints + auth scheme | 2 | 1 (Program.cs auth) | ~6 |
| 3 | Blob replication + quorum signaling | 2 | 0 | ~6 |
| 4 | Command + model changes | 1 | 4 | ~6 |
| 5 | Services + controller integration | 4 | 2 (Controller + Program.cs) | ~12 |
| 6 | Snapshot adaptation | 0 | 3 | ~5 |
| **Total** | | **13 new** | **11 modified** | **~45 new tests** |

## Alternative Approaches Considered

### Shared External Storage (Redis / S3)

Rejected: Adds external dependency, breaks Confman's self-contained 3-node architecture. Redis has weak durability guarantees for blob storage. S3 adds latency. Local filesystem is simplest and aligns with existing per-node data isolation.

### Raft-Based Blob Replication

Rejected: Flowing blobs through Raft is exactly the problem we're solving. Raft entries should be small and fast to replicate.

### Compression-Only (No Blob Store)

Already implemented: MessagePack + LZ4 compression (ConfmanSerializerOptions) achieves 60-80% reduction. But 1MB compressed to 500KB is still 500KB in every WAL entry. The blob store achieves 2500× reduction by removing value bytes entirely from Raft.

## Acceptance Criteria

### Functional Requirements

- [x] Values ≥ 64KB stored in blob store, pointer replicated via Raft
- [x] Values < 64KB use existing inline path (no behavior change)
- [x] Reads resolve blob-backed values transparently (via `IBlobValueResolver`)
- [x] Missing blobs fetched synchronously from peers (deduplicated per-BlobId)
- [x] Internal blob endpoints secured with ASP.NET Core auth scheme + constant-time comparison
- [x] BlobId validated against `^[0-9a-f]{64}$` on all endpoints
- [x] Blob replication waits for durability quorum (Interlocked + TCS) before Raft commit
- [x] Snapshots contain metadata only for blob-backed entries
- [x] Backward-compatible snapshot restore (Version 2 and 3 both supported)

### Non-Functional Requirements

- [x] WAL entry size < 1KB for blob-backed configs (vs ~500KB today)
- [x] Snapshot size proportional to entry count, not value sizes
- [x] No regression in read latency for inline configs
- [x] Blob-backed read latency < 100ms (local disk)
- [x] Write latency increase < 50ms for blob path (one extra round-trip)
- [x] Zero LOH allocations on write path (streaming pipeline)
- [x] One LOH allocation on read path (unavoidable: LZ4 decompress → string)

### Quality Gates

- [x] All existing 66 tests pass (no regressions)
- [x] ~45 new tests covering blob store, replication, command, services, read path, snapshot
- [ ] 3-node cluster smoke test: write 100 × 1MB configs, read from all nodes
- [x] `dotnet build` clean (no warnings)
- [x] BlobId path traversal test (security)

## Dependencies & Prerequisites

- MessagePack + LZ4 serialization already in place (ConfmanSerializerOptions)
- Streaming snapshot persist/restore already implemented
- DotNext cluster member discovery available via DI
- **New NuGet dependency**: `K4os.Compression.LZ4.Streams` (direct LZ4, not via MessagePack wrapper)

## Risk Analysis & Mitigation

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Blob quorum timeout under load | Medium | Write fails (503) | 10s timeout, quorum-first signaling, parallel push |
| Orphan blob accumulation | Low | Disk usage grows | Log orphans, GC in v2. Manual cleanup script if needed |
| Missing blob on read | Low | Latency spike | Sync fetch from peer, deduplicated per-BlobId, cache locally |
| Snapshot restore without blobs | Guaranteed | Cold reads slow | Lazy fetch + BlobManifest for diagnostics |
| Internal endpoint path traversal | Medium | Security breach | BlobId regex validation `^[0-9a-f]{64}$` |
| Thundering herd on missing blob | Medium | Peer overload | Per-BlobId SemaphoreSlim coalescer |
| ConfigEntry.Value NPE | High | Crash | Make Value `string?`, update all callers |
| Ghost blobs after leader step-down | Low | Disk waste | Content-addressed = harmless. Log + future GC |

## Deployment Verification Checklist

<details>
<summary>Pre-deployment checks</summary>

**Build verification:**
```bash
dotnet build -q && dotnet test
```

**Configuration verification:**
```bash
# Check all nodes have BlobStore config
for port in 6100 6200 6300; do
  curl -s "http://127.0.0.1:$port/health" | jq '.status'
done
```

**Data invariants (must hold at all times):**
1. Every committed Raft pointer references a BlobId that exists on at least quorum nodes
2. Every local blob file has content matching its filename (SHA256)
3. `ConfigEntry.IsBlobBacked == true` ↔ `ConfigEntry.BlobId != null`
4. `ConfigEntry.IsBlobBacked == true` → `ConfigEntry.Value == null`
5. Inline entries (`IsBlobBacked == false`) have `Value != null`
6. No blob file path contains `..` or characters outside `[0-9a-f/]`

**Rollback procedure:**
1. Stop all nodes
2. Revert to previous code version
3. Wipe data dirs (blob-backed entries incompatible with old code without backward-compat snapshots)
4. Start all nodes, re-ingest data

</details>

## Future Considerations (Not in Scope)

- Per-namespace GC retention policies (designed in brainstorm, build in v2)
- Background blob reconciliation (`BlobReconcilerHostedService`)
- Blob-aware audit events (deferred — audit currently disabled)
- Memory LRU cache for hot blobs
- Streaming upload endpoint for >30MB values
- mTLS for internal endpoints
- `/api/v1/.../config/{key}/raw` endpoint for zero-copy blob reads
- MVCC read optimization (relax SemaphoreSlim after blob store reduces LiteDB doc sizes)

## References

### Internal References

- Brainstorm: `docs/brainstorms/2026-02-12-external-data-path-brainstorm.md`
- Detailed design: `EXT_DATA_PATH.md`
- Snapshot starvation: `docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md`
- Large payload insights: `docs/solutions/2026-02-08-large-payload-performance-insights.md`
- WAL null bytes: `docs/solutions/dotnext-wal-null-bytes-log-entry-deserialization.md`
- MessagePack plan: `docs/plans/2026-02-09-feat-messagepack-lz4-serialization-plan.md`

### Key Source Files

- `src/Confman.Api/Controllers/ConfigController.cs` — write/read path
- `src/Confman.Api/Cluster/Commands/ICommand.cs:12-36` — MessagePack Union + JsonDerivedType
- `src/Confman.Api/Cluster/ConfigStateMachine.cs:164-223` — snapshot persist/restore
- `src/Confman.Api/Storage/LiteDbConfigStore.cs` — storage layer
- `src/Confman.Api/Models/ConfigEntry.cs:33` — `required string Value` → `string?`
- `src/Confman.Api/Cluster/ConfmanSerializerOptions.cs` — LZ4 compression options (Raft only)
- `src/Confman.Api/Program.cs:56-103` — DI registration

### External References

- K4os.Compression.LZ4: Direct LZ4 streaming compression for .NET
- .NET `RandomAccess.FlushToDisk`: Explicit fsync via SafeFileHandle
- .NET `IncrementalHash`: Streaming hash computation without materialization
- .NET `CryptographicOperations.FixedTimeEquals`: Constant-time byte comparison
- Ollama blob storage: Real-world content-addressed storage pattern (SHA256, dedup via digest)
