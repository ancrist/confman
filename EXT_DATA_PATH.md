# External Data Path Plan for Confman

## Scope

Apply "separate data path from consensus" to this repo so 1MB values do not flow through Raft/WAL/snapshots, while preserving Confman's current write semantics (leader-only writes, linearizable reads).

## Current State (Repo Analysis)

Confman currently replicates full values through Raft:

1. `ConfigController.Set` builds `SetConfigCommand` with full `request.Value`.
2. `IRaftService` serializes command bytes and replicates them (`RaftService` / `BatchingRaftService`).
3. `ConfigStateMachine.ApplyAsync` deserializes and applies to `LiteDbConfigStore`.
4. `ConfigEntry.Value` is compressed at rest in LiteDB (`CompressedValue`) but still full logical value.
5. Snapshots include full `SnapshotData.Configs` and full audit payloads (`AuditEvent.OldValue/NewValue`).

Impact for 1MB-heavy workloads in this codebase:

- Raft WAL carries large payloads.
- Batch payload limits (`Raft:BatchMaxBytes`) are quickly consumed.
- Snapshot size and runtime scale with total value bytes.
- Audit duplicates large before/after values.

## Recommended Design for This Repo

Use **Model A** + **Pattern 1** as default:

- Model A: durable blob first, then Raft pointer commit.
- Pattern 1: local blob store on each node (`data-{port}/blobs`) with out-of-band leader->follower blob replication and durability quorum before Raft commit.

Why this fits Confman now:

- Current deployment is symmetric 3-node cluster with per-node local disk.
- No new external infra required.
- Keeps leader/follower and redirect behavior unchanged.
- Can still add Pattern 2 later via an `IBlobStore` abstraction.

## A-H Mapping to Confman

## A) What changes

Current Raft command:

- `SetConfigCommand { Namespace, Key, Value, Type, Author, Timestamp }`

Target Raft command:

- `SetConfigBlobRefCommand { Namespace, Key, BlobId, Length, Checksum, Compression, Type, Author, Timestamp }`

Storage changes:

- `ConfigEntry` becomes metadata-first (`BlobId`, `Length`, `Checksum`, optional `InlineValue` for small items).
- Blob bytes live outside LiteDB in blob store.
- `SnapshotData` contains metadata refs, not blob bytes.

## B) Ack semantics (critical)

Confman should preserve source-of-truth semantics:

- If `PUT` returns success, a linearizable read can fetch the value immediately.

Write flow in this repo:

1. Client sends write to leader (existing redirect behavior kept).
2. Leader streams value to local blob store, computes `BlobId = sha256(value)`.
3. Leader replicates blob out-of-band to followers and waits for blob durability quorum (for 3 nodes: leader + at least 1 follower).
4. Leader replicates `SetConfigBlobRefCommand` via Raft.
5. After Raft quorum commit, return success.

Never commit pointer before blob durability quorum.

## C) Blob replication pattern for 3-node cluster

Implement local blob replication endpoints:

- `PUT /internal/blobs/{blobId}` (leader push)
- `HEAD /internal/blobs/{blobId}` (existence check)
- `GET /internal/blobs/{blobId}` (repair/catch-up fetch)

Durability rule:

- `requiredBlobAcks = raftWriteQuorum` by default.
- For 3 nodes this is 2 durable copies before pointer commit.

Durable write definition:

- fsync completed (or write-through policy) and atomic rename from temp file.
- checksum and length validated before ACK.

## D) Reads and caching

Read flow becomes:

1. Existing read barrier logic for metadata linearizability.
2. Lookup `ConfigEntry` metadata in LiteDB.
3. Resolve blob by `BlobId` from local blob store.
4. If missing locally, trigger repair fetch from leader/peer; return value if fetch succeeds.

Caching strategy:

- Pattern 1 already gives local copies on all healthy nodes.
- Optional: memory LRU for hot blobs to reduce disk reads.

## E) Garbage collection

Add periodic mark-and-sweep GC per node:

1. Mark live `BlobId`s from authoritative current state (`configs` table; optionally current audit refs if retained).
2. Sweep blobs not live and older than safety window (for example 1 hour).
3. Skip blobs currently in pending replication or recent writes.

Because blobs are content-addressed (`sha256`), duplicates are naturally deduplicated.

## F) Snapshots and catch-up

Snapshot changes:

- `SnapshotData` stores config metadata/pointers only.
- Blob bytes are excluded from snapshot payload.
- Snapshot version bump (for example `2 -> 3`).

Node recovery changes:

1. Restore metadata snapshot quickly.
2. Run blob reconciler to fetch missing blobs by `BlobId`.
3. Support lazy fetch-on-first-read plus background eager repair.

## G) Failure modes and mitigations

- Blob uploaded but Raft commit fails: orphan blob, GC removes.
- Raft pointer committed but blob not durable: prevented by Model A gating.
- Leader crash mid-upload: retry is idempotent because `BlobId` is hash-based.
- Follower offline during write: metadata may catch up first; missing blob is repaired via internal fetch.
- Corrupt blob on disk: checksum mismatch triggers refetch and alert.

## H) Minimal Raft entry model in this codebase

`SetConfigBlobRefCommand` fields:

- `Namespace`
- `Key`
- `BlobId`
- `Length`
- `Checksum` (may equal `BlobId` digest)
- `Compression` (none/lz4/zstd/etc)
- `Type`
- `Author`
- `Timestamp`

Keep command payload small and deterministic for MessagePack union serialization.

## Implementation Plan (Phased)

## Phase 0: Feature Flags and Compatibility

Add config flags so rollout is safe:

- `BlobStore:Enabled` (default false)
- `BlobStore:InlineThresholdBytes` (for example 64KB)
- `BlobStore:RequiredAcksMode` (`raft-quorum`)
- `BlobStore:GcRetentionMinutes`

Behavior:

- If disabled: existing inline path unchanged.
- If enabled and value > threshold: blob path.
- If enabled and value <= threshold: optionally keep inline for simplicity/perf.

## Phase 1: Blob Subsystem Abstractions

Add new components:

- `Storage/Blobs/IBlobStore.cs`
- `Storage/Blobs/LocalBlobStore.cs`
- `Storage/Blobs/IBlobReplicator.cs`
- `Storage/Blobs/PeerBlobReplicator.cs`
- `Storage/Blobs/BlobIntegrity.cs` (hash/checksum helpers)

Program wiring:

- Register blob services in `Program.cs`.
- Create `Storage:DataPath/blobs` alongside current `raft-log` and LiteDB files.

## Phase 2: API and Write Path

Update `ConfigController` write path:

- Keep current endpoint contract for now (`SetConfigRequest.Value`).
- On leader, branch to blob flow when size threshold exceeded.
- Execute Model A sequence before `IRaftService.ReplicateAsync`.

Optional phase 2b (recommended for very large values):

- Add streaming upload endpoint (octet-stream) to avoid JSON string overhead.
- Keep existing JSON endpoint for small values/backward compatibility.

## Phase 3: Command and State Model Changes

Raft commands:

- Add `SetConfigBlobRefCommand` and MessagePack union entry in `ICommand`.
- Keep `SetConfigCommand` for inline/small values during migration.

Data model:

- Extend `ConfigEntry` with blob metadata fields.
- Preserve existing keys or bump snapshot/version carefully.

Storage logic:

- Update `LiteDbConfigStore.SetWithAuditAsync` path to store metadata refs.
- Avoid writing full value to LiteDB for blob-backed entries.

Audit logic:

- Avoid reintroducing large payloads through `AuditEvent`.
- For blob-backed values, store refs/checksums and optional truncated preview instead of full old/new payload.

## Phase 4: Internal Blob Endpoints and Security

Add internal controller/endpoints for blob push/fetch.

Security requirements:

- Restrict to cluster members (token and/or mTLS).
- Validate checksum, length, and content-addressed ID before accept.

Operational protections:

- Rate limits / body size protections for internal endpoints.
- Structured logs include `blobId`, `len`, `checksum`, source node.

## Phase 5: Read Path and Reconciliation

Read handling changes:

- `Get`/`List`/`/api/v1/configs` resolve value from blob store when entry is blob-backed.
- If blob missing locally, run peer fetch fallback.

Add background reconciliation service:

- Periodically scan metadata for missing local blobs.
- Fetch missing blobs from leader/peers.

## Phase 6: Snapshot/Restore Adaptation

Update snapshot format:

- Bump `SnapshotData.Version`.
- Persist metadata-only config entries.
- Restore metadata and start blob reconciliation.

Guarantee:

- Snapshot install remains fast even with many large values.

## Phase 7: GC + Observability

GC service:

- `BlobGcHostedService` with mark-and-sweep.
- Safety window and in-flight exclusions.

Metrics:

- Blob put latency, replication latency, ack quorum wait.
- Missing blob read count, repair success/failure.
- Orphan blob count and GC deletions.

## Phase 8: Migration and Rollout

Rollout order:

1. Deploy code with feature disabled.
2. Upgrade all nodes.
3. Enable feature flag cluster-wide.
4. Start with canary namespace.
5. Run reconciliation and GC in observe-only mode first.
6. Enable deletion once metrics are stable.

Legacy data handling:

- Existing inline entries remain valid.
- New writes use blob path by threshold.
- Optional background backfill migrates old large inline values to blobs.

## File-Level Change Map

Primary files likely touched:

- `src/Confman.Api/Controllers/ConfigController.cs`
- `src/Confman.Api/Cluster/Commands/ICommand.cs`
- `src/Confman.Api/Cluster/Commands/SetConfigCommand.cs`
- `src/Confman.Api/Cluster/Commands/SetConfigBlobRefCommand.cs` (new)
- `src/Confman.Api/Models/ConfigEntry.cs`
- `src/Confman.Api/Models/AuditEvent.cs`
- `src/Confman.Api/Storage/IConfigStore.cs`
- `src/Confman.Api/Storage/LiteDbConfigStore.cs`
- `src/Confman.Api/Cluster/ConfigStateMachine.cs`
- `src/Confman.Api/Cluster/SnapshotData.cs`
- `src/Confman.Api/Program.cs`
- `src/Confman.Api/appsettings.json`
- `tests/Confman.Tests/*` (new blob-path, snapshot, failure-mode tests)

New area to add:

- `src/Confman.Api/Storage/Blobs/*`
- `src/Confman.Api/Controllers/InternalBlobController.cs`
- `src/Confman.Api/Services/BlobReconcilerHostedService.cs`
- `src/Confman.Api/Services/BlobGcHostedService.cs`

## Test Plan

Unit tests:

- Content hash determinism and checksum validation.
- Idempotent blob put (same bytes -> same `BlobId`).
- GC mark/sweep safety window behavior.

Integration tests:

- 3-node write: success only after blob durability + Raft commit.
- Read-after-write linearizability with blob-backed values.
- Follower restart catch-up with missing blob repair.

Failure injection:

- Blob quorum not reached -> no pointer commit.
- Pointer commit failure -> orphan appears and GC removes.
- Corrupt local blob -> checksum detection and repair.

Performance validation:

- 1MB writes: Raft entry size drops to metadata-only.
- WAL growth and snapshot size reduced vs baseline.
- Read p95 impact measured with and without cache.

## Key Decisions to Confirm Before Build

- Keep single `PUT` API only, or add streaming blob upload endpoint now.
- Inline threshold for dual-mode storage.
- Audit payload policy for large values (full vs refs+preview).
- Internal endpoint security mechanism (token-only vs mTLS).

