# External Data Path Brainstorm

**Date:** 2026-02-12
**Status:** Decisions captured, ready for planning

## What We're Building

Separate large config values from the Raft consensus path. Instead of replicating full values through WAL/snapshots, store blob bytes in a local content-addressed blob store on each node and replicate only lightweight metadata pointers (~200 bytes) through Raft.

**Primary goal:** WAL and snapshot size reduction for large payloads (1MB+). Not about enabling massive values (>30MB) — that's a future concern.

## Why This Approach

Current system replicates full values through Raft:
- 1MB config = ~500KB WAL entry (LZ4 compressed) on every node
- 1000 x 1MB configs = ~500MB snapshot
- Batch limit (4MB) fits only ~8 large commands per round-trip
- LOH memory pressure during snapshot serialization

With blob store:
- 1MB config = ~200 byte WAL entry (metadata pointer)
- 1000 x 1MB configs = ~200KB snapshot (2500x reduction)
- Batch limit fits ~20,000 pointer commands per round-trip
- Snapshot creation becomes near-instant

Concurrent write throughput improves because blob pushes happen in parallel before Raft, and tiny pointers batch efficiently. Single-write latency adds ~10ms (one extra round-trip) but is negligible in practice.

## Key Decisions

### 1. Blob Replication: Full Quorum

Leader pushes blob to followers and waits for durability quorum (2/3 for 3-node cluster) before committing the Raft pointer. Both the leader's local copy and the follower's copy must be fsync'd before ACK.

**Rationale:** Same bytes cross the network either way (blob push vs Raft AppendEntries). Full quorum prevents "pointer committed but blob missing" failures. Content-addressed BlobId makes retries idempotent.

**Write flow:**
1. Client sends value to leader
2. Leader computes `BlobId = SHA256(value)`, stores locally (fsync)
3. Leader pushes blob to all followers in parallel
4. Wait for quorum ACKs (2 of 3)
5. Build `SetConfigBlobRefCommand { BlobId, Length, Checksum, ... }` (~200 bytes)
6. Replicate pointer via Raft (tiny entry)
7. Return success after Raft commit

### 2. Missing Blob Reads: Block + Fetch From Peer

When a node receives a read for a blob-backed config and the blob isn't on local disk, it synchronously fetches from a peer (leader preferred). Client sees a latency spike (100-500ms) but always gets a response.

No 503 / retry-after — keeps client contract simple.

### 3. Audit: Defer (Currently Disabled)

Audit is disabled in production (`Audit.Enabled: false` in appsettings.json). Defer blob-aware audit design to when audit is re-enabled.

**TODO:** When re-enabling audit, consider blob-aware approach:
- Store `OldValueBlobId` / `NewValueBlobId` instead of full values
- Or truncated preview (first 1KB + BlobId reference)
- Must coordinate with GC — audit-referenced blobs cannot be deleted
- Per-namespace audit retention policy may be needed

### 4. Inline Threshold: Configurable, Default 64KB

Add `BlobStore:InlineThresholdBytes` to appsettings.json (default: 65536). Values below this threshold use the existing inline `SetConfigCommand` path unchanged. Values above trigger the blob path.

**Rationale:** Most configs are <10KB and stay inline. 64KB catches the large payloads that cause WAL/snapshot bloat. Runtime-configurable lets us tune without code changes.

### 5. Internal Endpoint Auth: Shared Secret Token

Blob replication endpoints (`PUT/GET/HEAD /internal/blobs/{id}`) secured with a static `X-Cluster-Token` header read from config (`BlobStore:ClusterToken`).

Fits the existing static topology pattern. mTLS is a future enhancement.

### 6. GC: Defer From v1, Design Retention Now

No blob GC in v1. Orphan blobs accumulate on disk (acceptable for initial validation).

**Future design: Per-namespace retention policy**

```
Namespace: /prod/payments
├── RetentionPolicy:
│   ├── MaxVersions: 30        # keep last 30 versions per key
│   └── MaxAgeDays: 90         # OR delete versions older than 90 days
```

GC will have two concerns:
- **Orphan cleanup:** Blobs with zero metadata references (failed writes, superseded values). Safe to delete after safety window.
- **Version retention:** Per-namespace policy controls how many historical versions to keep. Pruning deletes both LiteDB ConfigEntry records and associated blobs.

Content-addressing (SHA256) provides natural deduplication — shared blobs are only deleted when the last reference is removed.

GC runs independently on each node (no coordination needed). Mark phase queries LiteDB for live BlobIds, sweep phase deletes unreferenced blobs older than safety window.

### 7. Blob Reconciliation: Lazy Only (v1)

No background reconciler in v1. Missing blobs are fetched on first read (sync fetch from peer, per decision #2).

**Rationale:**
- Hot configs populate within seconds of restart via normal client traffic
- Blobs survive restarts (plain files in `data-{port}/blobs/`)
- Only truly lost blobs (wiped data dir) need fetching
- Avoids downloading all blobs eagerly (1000 x 1MB = 1GB) on startup

**Future:** Add `BlobReconcilerHostedService` with priority ordering (recent writes first) if lazy fetch causes unacceptable read latency spikes post-restart.

### 8. Snapshot Migration: Hard Cutover With Wipe

Bump `SnapshotData.Version` to 3. No backward compatibility with Version 2 snapshots. Wipe data dirs on all nodes and restart with new code.

Same pattern as the MessagePack serialization migration. Eliminates:
- Dual-format snapshot support
- Version migration logic in RestoreAsync
- Feature flag gating on snapshot format

### 9. Blob Storage Format

- Content-addressed: `BlobId = SHA256(value)`
- Compressed on disk with LZ4 (same as Raft commands via ConfmanSerializerOptions)
- Directory structure: `data-{port}/blobs/{blobId[0..2]}/{blobId}` (2-char prefix subdirectory to avoid filesystem limits)
- Atomic writes: temp file -> fsync -> rename

## What's NOT in Scope (v1)

- Streaming upload endpoint (octet-stream) for >30MB values
- Per-namespace GC / retention policies (design captured above)
- Background blob reconciliation
- Blob-aware audit events
- mTLS for internal endpoints
- Memory LRU cache for hot blobs
- Backward-compatible snapshot migration

## Open Questions (Resolved)

| Question | Decision |
|----------|----------|
| Blob quorum size | Raft quorum (2/3) |
| Missing blob reads | Block + sync fetch |
| Audit handling | Defer (audit disabled) |
| Inline threshold | Configurable, 64KB default |
| Internal auth | Shared secret token |
| GC scope | Defer from v1 |
| Reconciliation | Lazy only |
| Snapshot migration | Hard cutover with wipe |

## Remaining Open Questions (For Planning)

1. **Blob replication timeout/retry policy** — How many retries? Exponential backoff? Timeout per attempt?
2. **Blob storage path config** — Hardcode under `Storage:DataPath/blobs` or separate `BlobStore:DataPath`?
3. **Checksum validation on read** — Validate SHA256 on every blob read (10ms/MB overhead) or trust filesystem integrity?
4. **Prometheus metrics** — Which blob metrics to add? (put latency, replication latency, missing reads, store size)
5. **Feature flag behavior** — Does `BlobStore:Enabled=false` just skip blob path on writes, or also disable internal endpoints?

## Next Steps

Run `/workflows:plan` to create an implementation plan based on these decisions.
