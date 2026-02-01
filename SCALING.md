# Scaling Confman

## Target Workload

Confman is designed to replace a 15GB git repository of JSON configuration files. The workload characteristics:

| Metric | Value |
|--------|-------|
| Total state size | ~15 GB |
| Largest single file | ~100 MB JSON |
| Data composition | Many small KV pairs + large JSON files |
| Write frequency | ~50 changes/day (~1 write per 30 minutes) |
| Write pattern | Burst (PR merge), then idle |
| Read pattern | Frequent, from many consumers |
| Consistency requirement | Linearizable reads for all data |

## Single Cluster Viability

A single 3-node Raft cluster can handle this workload. The write rate is so low that even replicating a 100MB log entry through Raft is acceptable — there are ~30 minutes of idle time between writes.

For reference, where other Raft-based systems draw the line:

| System | Recommended max state size |
|--------|---------------------------|
| etcd | 2–8 GB |
| Consul KV | ~512 MB |
| ZooKeeper | ~hundreds of MB |

These limits exist because those systems are designed for high-throughput coordination (leader elections, service discovery, distributed locks). Configuration management at 50 writes/day is a fundamentally different workload — the bottleneck is state size and snapshot management, not write throughput.

## What Needs to Change for 15GB

### 1. Compress Values at Rest

JSON configuration files compress extremely well (repetitive keys, whitespace, predictable structure). Typical compression ratios:

| Format | Raw 100MB JSON | Compressed |
|--------|----------------|------------|
| gzip | 100 MB | ~8–12 MB |
| brotli | 100 MB | ~5–8 MB |
| zstd | 100 MB | ~6–10 MB |

Compress before writing to the state machine, decompress on read. This reduces:
- Raft log entry size (100MB → ~10MB per replication)
- Snapshot size on disk (15GB → ~1.5–2GB)
- Snapshot transfer time to recovering nodes
- Total storage per node

The API should handle compression transparently — clients send and receive uncompressed JSON.

### 2. Binary Snapshot Serialization

The current `ConfigStateMachine.PersistAsync` serializes the entire state to JSON. This won't work at 15GB:
- JSON serialization is slow for large datasets
- Requires holding the full serialized state in memory
- Produces unnecessarily large snapshots

Replace with a streaming binary format:
- **MessagePack** or **protobuf** for serialization (5–10x faster than JSON, 2–3x smaller)
- **Stream directly to disk** instead of building the full state in memory
- Write entries sequentially: header → namespaces → configs → audit events
- Each entry prefixed with length for efficient reading

Estimated snapshot sizes with compression + binary serialization:

| Raw state | Binary | Binary + compressed |
|-----------|--------|---------------------|
| 15 GB | ~5–7 GB | ~1–2 GB |

### 3. Memory Allocation

Nodes need enough memory to handle:
- The LiteDB working set (indexes + hot data)
- Snapshot creation (streaming reduces peak memory vs. full materialization)
- Raft log buffer for in-flight entries
- Decompression buffers for reads

Recommended: **4–8 GB per node** for a 15GB state size, depending on access patterns.

### 4. Incremental Snapshots (If Needed)

If full snapshots become too slow (minutes), implement incremental snapshots:
- Track which keys changed since the last snapshot
- Write only deltas
- Periodically compact deltas into a full snapshot

This is an optimization — start with full snapshots and measure before adding this complexity.

## What Doesn't Need to Change

Given the low write throughput, several complex scaling patterns are unnecessary:

| Pattern | Why it's not needed |
|---------|---------------------|
| **Partitioning / sharding** | Single cluster handles the state size with compression |
| **External blob storage** | Raft log entries are large but infrequent — one every 30 min |
| **Two-tier consistency** | All data fits through Raft; no need for eventual-consistent blob tier |
| **Write batching** | 50 writes/day doesn't need batching |
| **Read replicas** | 3-node cluster with read barrier handles the read load |
| **Multi-cluster federation** | Single cluster is sufficient |

## Delta Storage for Large Files

For large JSON files where updates typically change only a few fields, storing deltas instead of full values dramatically reduces Raft log entry sizes.

### How It Works

Instead of replicating the entire 50MB file on every update, the leader computes a JSON Patch ([RFC 6902](https://datatracker.ietf.org/doc/html/rfc6902)) diff between the old and new values. Only the patch goes through Raft:

```json
// Full value replication (current): 50 MB through Raft log
{ "op": "replace", "value": "<entire 50MB JSON>" }

// Delta replication: ~200 bytes through Raft log
[
  { "op": "replace", "path": "/settings/timeout", "value": 5000 },
  { "op": "replace", "path": "/settings/retries", "value": 3 },
  { "op": "add", "path": "/features/newFlag", "value": true }
]
```

The state machine applies the patch to the stored document, materializing the full current value. Reads return the materialized document — no reconstruction needed.

### Impact

| Metric | Full value | Delta |
|--------|-----------|-------|
| Raft log entry (50MB file, 3 fields changed) | ~5 MB compressed | ~200 bytes |
| Replication time | Noticeable | Instant |
| Daily log growth (50 writes of large files) | 250 MB–2.5 GB | A few MB |
| Snapshot frequency needed | Often (log grows fast) | Rarely (log stays small) |

### Write Path with Deltas

1. Client sends full updated JSON to the API (unchanged from client perspective)
2. Leader loads the current value from the state machine
3. Leader computes JSON Patch: `diff(current, new)` → delta
4. If delta is smaller than a threshold (e.g., 50% of full value), replicate the delta
5. If delta is larger (major rewrite), replicate the full value (delta overhead not worth it)
6. State machine applies the patch to produce the new materialized value
7. Readers always get the full materialized document

### When Full Value Is Still Used

| Scenario | Strategy |
|----------|----------|
| New file (no previous version) | Full value |
| Delta > 50% of full value | Full value (major rewrite) |
| Small KV pairs (< 1 KB) | Full value (delta overhead not worth it) |
| File deletion | Delete command (no value) |
| Typical field update on large file | Delta |

### Tradeoffs

**Benefits:**
- Raft log entries shrink from megabytes to bytes for typical config updates
- Replication bandwidth drops by orders of magnitude
- Log compaction is needed less frequently
- Snapshot-to-log-size ratio improves (less log growth between snapshots)

**Costs:**
- **Delta computation on the leader** — diffing a 50MB JSON file takes CPU time. For 50 writes/day this is negligible, but the leader must hold both old and new values in memory during the diff.
- **Conflict semantics change** — two concurrent updates to different fields of the same file could theoretically be merged (JSON Patch supports this). With full-value replacement, last write wins. The publishing workflow (draft → review → publish) mitigates this since changes are serialized through approvals.
- **State machine complexity** — the apply logic must handle both full values and deltas, applying JSON Patch when the log entry is a delta.
- **First write is always full** — initial import of a 50MB file goes through Raft at full size. Only subsequent updates benefit.

### .NET Libraries

| Library | Purpose |
|---------|---------|
| `SystemTextJson.JsonDiffPatch` | Compute JSON diffs using System.Text.Json |
| `JsonPatch.Net` | JSON Patch (RFC 6902) implementation |
| `Json.More.Net` | JSON Merge Patch (RFC 7396) support |

### Interaction with Compression

Delta storage and compression are complementary:
- Small deltas don't benefit much from compression (already tiny)
- Full-value writes still benefit from compression
- The log entry header should indicate the encoding: `full`, `full+compressed`, `delta`, `delta+compressed`

Combined effect on a typical 50MB JSON file update (3 fields changed):

| Strategy | Log entry size |
|----------|---------------|
| Full value, no compression | 50 MB |
| Full value, compressed | ~5 MB |
| Delta, no compression | ~200 bytes |
| Delta, compressed | ~200 bytes |

The delta alone provides a ~250,000x reduction for this case. Compression on top is unnecessary for small deltas but should remain available as a fallback for large deltas or full-value writes.

## Consistency Considerations

All data — small KV pairs and large JSON files — goes through Raft consensus. This preserves:

- **Linearizable reads** via `ApplyReadBarrierAsync` on followers
- **Consistent snapshots** — the full state is a single Raft state machine
- **Atomic versioning** — each write gets a Raft log index and term
- **Audit trail** — every change is an immutable log entry

An alternative architecture (Raft for metadata, async replication for blobs) was considered and rejected because:
1. It introduces split consistency — metadata is linearizable but blob availability is eventual
2. A follower could have valid metadata pointing to a blob that hasn't arrived yet
3. The write rate doesn't justify the complexity — Raft handles 10MB compressed log entries fine at this frequency
4. The publishing workflow already provides a natural write gate (draft → review → publish)

## Raft Log Entry Sizes

With compression enabled, the Raft log entry sizes for typical operations:

| Operation | Raw size | Compressed |
|-----------|----------|------------|
| Small KV write | ~200 bytes | ~200 bytes (not worth compressing) |
| 1 MB JSON file | 1 MB | ~100–150 KB |
| 10 MB JSON file | 10 MB | ~1–1.5 MB |
| 100 MB JSON file | 100 MB | ~8–12 MB |

At 50 writes/day, the daily Raft log growth depends on the mix. Worst case (all 100MB files): ~500–600 MB/day compressed. Typical case (mostly small KV + occasional large files): ~50–100 MB/day.

## Snapshot Strategy

| Trigger | Action |
|---------|--------|
| Every N committed entries (e.g., 1000) | Create full snapshot |
| Manual trigger | Admin endpoint to force snapshot |
| Node recovery | Transfer latest snapshot + replay log tail |

Snapshot creation should be done on a background thread to avoid blocking the Raft state machine. The state machine should support consistent reads during snapshot creation (LiteDB handles this with its copy-on-write pages).

## Network Bandwidth

Replication bandwidth per write (3-node cluster, quorum = 2):

| File size | Compressed | Network I/O (leader → 2 followers) |
|-----------|------------|-------------------------------------|
| Small KV | ~200 bytes | ~400 bytes |
| 10 MB JSON | ~1.5 MB | ~3 MB |
| 100 MB JSON | ~12 MB | ~24 MB |

At 50 writes/day, peak daily bandwidth is negligible. Even the worst case (50 × 100MB files) is ~1.2 GB/day across the cluster.

## Git Repo Migration Path

The git repository maps naturally to Confman's data model:

| Git concept | Confman equivalent |
|-------------|-------------------|
| Repository | Root namespace `/` |
| Directory structure | Namespace hierarchy (`/environments/prod/us-east/`) |
| JSON file | Config entry (key = filename, value = file content) |
| Commit | Raft log entry + audit event |
| PR review | Publishing workflow (draft → review → publish) |
| Git blame | Audit log per namespace/key |
| Branch | Draft configs (not yet published) |
| Git tag | Config version number |

Migration strategy:
1. Walk the git tree, create namespaces for each directory
2. Import each JSON file as a config entry with compression
3. Preserve the latest author and timestamp from git log
4. Historical versions can be bulk-imported into the audit log

## Alternative Solutions

If the custom Raft implementation becomes a limiting factor, these are production-grade systems that could serve as the storage/consensus backend for a configuration service at this scale.

### Distributed KV Stores and Databases

| System | Consensus | Max Value Size | Recommended State Size | License | Language |
|--------|-----------|----------------|------------------------|---------|----------|
| [etcd](https://etcd.io/) | Raft | 1.5 MB default | 2–8 GB | Apache 2.0 | Go |
| [Consul KV](https://www.consul.io/) | Raft | 512 KB default | ~512 MB | BSL 1.1 | Go |
| [ZooKeeper](https://zookeeper.apache.org/) | ZAB (Raft-like) | 1 MB default | Hundreds of MB | Apache 2.0 | Java |
| [TiKV](https://tikv.org/) | Raft (multi-group) | No hard limit | Terabytes | Apache 2.0 | Rust |
| [FoundationDB](https://www.foundationdb.org/) | Paxos variant | 100 KB per KV | Terabytes | Apache 2.0 | C++ |
| [CockroachDB](https://www.cockroachlabs.com/) | Raft (multi-range) | No hard limit | Terabytes | BSL 1.1 / CCL | Go |
| [YugabyteDB](https://www.yugabyte.com/) | Raft (per-tablet) | No hard limit | Terabytes | Apache 2.0 (core) | C++ |
| [Riak KV](https://riak.com/) | Dynamo (gossip) | No hard limit | Terabytes | Apache 2.0 | Erlang |
| [DragonflyDB](https://www.dragonflydb.io/) | None (single-node) | 512 MB | Memory-bound | BSL 1.1 | C++ |
| [Redis + Sentinel](https://redis.io/) | Async replication | 512 MB | Memory-bound | RSALv2 / SSPLv1 | C |

### Configuration-Specific Systems

| System | Model | Versioning | Audit | Approval Workflow | Scale |
|--------|-------|------------|-------|-------------------|-------|
| [Spring Cloud Config](https://spring.io/projects/spring-cloud-config) | Git-backed | Git history | Git log | Via git/PR | Git repo size |
| [AWS AppConfig](https://docs.aws.amazon.com/appconfig/) | Managed service | Built-in | CloudTrail | Validators + rollback | Unlimited (managed) |
| [Azure App Configuration](https://azure.microsoft.com/en-us/products/app-configuration) | Managed service | Labels + snapshots | Activity log | Feature flags | Unlimited (managed) |
| [GCP Runtime Configurator](https://cloud.google.com/deployment-manager/runtime-configurator) | Managed service | Basic | Cloud Audit | None | Limited |
| [HashiCorp Vault](https://www.vaultproject.io/) | Raft / Consul | Versioned KV v2 | Audit log | Policies + Sentinel | Depends on backend |
| [LaunchDarkly](https://launchdarkly.com/) | Managed service | Flag history | Audit log | Approvals + scheduling | Unlimited (managed) |
| [ConfigCat](https://configcat.com/) | Managed service | Version history | Audit log | Environments | Unlimited (managed) |

### Feature Comparison for Confman's Use Case

The key requirements: hierarchical namespaces, large value support (50–100MB JSON), linearizable reads, publishing workflow with approvals, and audit trail.

| Feature | etcd | Consul | TiKV | FoundationDB | CockroachDB | Confman |
|---------|------|--------|------|--------------|-------------|---------|
| Linearizable reads | Yes | Yes (default) | Yes | Yes (SSI) | Yes (serializable) | Yes (read barrier) |
| Large values (50–100 MB) | No (1.5 MB limit) | No (512 KB limit) | Yes | No (100 KB limit) | Yes | Yes (with compression) |
| Hierarchical keys | Prefix-based | Prefix-based | Prefix-based | Prefix-based (layers) | SQL schemas | Native namespaces |
| Built-in versioning | Revision-based | No | MVCC | MVCC | MVCC | Per-key version |
| Audit trail | Watch + revision | Event stream | Change data capture | Watch | Changefeeds | Built-in per-entry |
| Approval workflow | No | No | No | No | No | Built-in (draft → review → publish) |
| Schema validation | No | No | No | No | SQL constraints | JSON Schema with extensions |
| RBAC | Basic (user/role) | ACLs + policies | No | No | SQL grants | Per-namespace roles |
| Encryption at rest | Yes | Yes (Enterprise) | Yes | Yes | Yes (Enterprise) | Planned (x-confman-encrypted) |
| .NET native | Client only | Client only | Client only | Client only | Npgsql (PostgreSQL wire) | Full stack |

### Suitability by Scale

| State size | Recommended approach |
|------------|---------------------|
| < 1 GB | Confman single cluster (current), etcd, Consul |
| 1–15 GB | Confman single cluster (with compression + delta storage) |
| 15–100 GB | TiKV or CockroachDB as storage backend, Confman as API layer |
| 100 GB+ | CockroachDB or YugabyteDB with Confman as a thin API gateway |

### Hybrid Architecture

For the 15GB target, the most pragmatic scaling path if the custom Raft implementation hits limits:

1. **Keep Confman as the API layer** — namespaces, RBAC, publishing workflow, schema validation, audit
2. **Replace the Raft state machine with TiKV or CockroachDB** — handles storage, replication, and consensus
3. **Confman becomes a stateless API** — all state lives in the external store, Confman handles business logic

This preserves the governance model (the actual value of Confman) while offloading the distributed storage problem to systems designed for it.

### Licensing Notes

| License | Commercial use | Modification | Distribution |
|---------|---------------|--------------|--------------|
| Apache 2.0 | Unrestricted | Unrestricted | Unrestricted |
| BSL 1.1 (CockroachDB, Consul, Dragonfly) | Free for non-competing use; paid for offering as a service | Allowed | Must not compete with vendor's offering |
| RSALv2 / SSPLv1 (Redis) | Free for internal use; restricted for offering as a service | Allowed | Cannot offer as managed service |
| CCL (CockroachDB Enterprise) | Paid license for enterprise features | Not allowed | Not allowed |

For an internal configuration service (not offered as SaaS), all licenses above permit free use. The BSL and RSAL restrictions only apply if you'd offer the database itself as a managed service to third parties.
