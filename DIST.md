# Distributed Systems Reference for Confman

This document consolidates patterns from "Patterns of Distributed Systems" (Fowler) and "Designing Distributed Systems" (Burns), organized into general patterns and Confman-specific implementation details.

---

## Section 1: Distributed Systems Patterns

This section covers technology-agnostic distributed systems concepts that apply to any strongly consistent, replicated system.

### Replicated Log and Quorum Commit

A Write-Ahead Log (WAL) provides a linear history of state changes. Replaying the log reproduces state deterministically. Replication becomes copying log entries to followers; a leader commits only after a majority quorum acknowledges.

**Core principles:**
- Uncommitted entries must never be applied to the state machine until quorum acknowledgment arrives
- Log index + term together form a unique version identifier
- Replication masks failures but requires software to handle edge cases

```
Client -> Leader
             | append log entry (uncommitted)
             | replicate to followers
             v
          [Majority ACK] -> commit index advances
```

### Leader and Followers (Consensus)

Only one leader processes writes; followers replicate and optionally serve reads. This ownership/master pattern ensures coordinated decisions without conflicts.

**Core principles:**
- Followers forward writes to leader rather than processing them locally
- Leader sends periodic heartbeats; absence triggers election
- Term (generation clock) increments each election
- Followers reject stale-term requests and return leader hint

```
Follower --forward--> Leader --replicate--> Follower
    ^                                      |
    +--------------heartbeat---------------+
```

### Majority Quorum and Safety

A majority quorum ensures committed entries survive leader changes. This is the foundation of fault tolerance in consensus systems.

**Core principles:**
- Leaders must be the most up-to-date (highest term, then highest log index)
- A new leader must complete replication of any uncommitted entries before serving new writes
- Quorum size for N nodes is (N/2) + 1

```
3-node cluster quorum = 2
5-node cluster quorum = 3
commit when >= quorum acks received
```

### Follower Reads and Read-Your-Writes

Followers can serve reads to reduce leader load, but may return stale data. Version tokens enable read-your-writes consistency without leader involvement.

**Core principles:**
- Return a version token (term:index) on write
- Followers wait until their commit index reaches the requested version before responding
- Trade-off: leader reads for linearizability vs follower reads for scalability

```
Client write -> leader returns version V
Client read -> follower waits until commit_index >= V -> respond
```

### Consistency and Linearizability

Quorum read/write alone can still expose anomalies. True linearizable reads require coordination with the leader or a proven commit index.

**Linearizable read options:**
- Leader-only reads (simplest, highest load on leader)
- Quorum reads with commit index verification
- Lease-based reads (leader holds lease, serves reads within lease period)

```
Linearizable read path:
Client -> Leader (or quorum) -> commit index verified -> respond
```

### Partitioning (Sharding)

Data is partitioned to scale beyond a single node's capacity. Each partition (shard) owns a key range.

**Core principles:**
- Use logical partitions (many more than physical nodes) so rebalancing moves only some partitions
- Partition by hash (even distribution) or by prefix/range (locality)
- Each shard can be independently replicated

```
Key -> hash/prefix -> logical partition -> physical node
```

### Sharding and Routing (Ambassador Pattern)

A routing layer maps requests to the correct shard. Two approaches exist:
- Embed sharding logic in the service (server-side routing)
- Use a client-side ambassador/sidecar to route requests

```
Client/App -> Ambassador (localhost) -> Shard A/B/C
```

The ambassador pattern hides shard logic and leader redirects from callers.

### Stateful Scaling with Sharded Services

Sharded services handle stateful systems when data size exceeds a single node. Shards are often deployed with stable network identities (e.g., StatefulSets in Kubernetes with stable DNS names).

**Design considerations:**
- Keep a shard registry mapping shards to node endpoints
- Expose a stable routing layer (gateway) to avoid rewriting clients on topology changes

### Replicated Stateless Services

Stateless services are replicated behind a load balancer for availability and horizontal scaling.

**Core principles:**
- Health checks prevent traffic to unhealthy nodes
- Liveness checks (is the process alive?) and readiness checks (can it serve traffic?) serve different purposes
- Stateless tiers can scale independently of stateful tiers

### Replication vs Partitioning Trade-offs

| Concern | Replication | Partitioning |
|---------|-------------|--------------|
| Purpose | Resilience, read scaling | Data size scaling |
| Failure handling | Survives node loss | Isolates failures to affected shards |
| Complexity | Consensus required | Routing required |

Combine both: partition by key range and replicate within each partition.

### Distributed Time and Ordering

System clocks are unreliable for ordering writes across nodes due to clock skew. Logical clocks provide ordering guarantees.

**Clock patterns:**
- **Lamport clocks:** Provide partial order; increment on local events and when receiving messages
- **Hybrid Logical Clocks (HLC):** Combine wall time with logical counter for both ordering and approximate real time
- **Clock-bound wait:** Use a maximum skew bound to ensure ordering visibility

```
Hybrid clock: (wall_time_ms, logical_counter)

On event:
  wall = max(local.wall, remote.wall, now())
  counter = if wall == local.wall then local.counter + 1 else 0
  return (wall, counter)
```

Within a single Raft log, log order is authoritative. HLC is useful for cross-shard ordering or global audit trails.

### Fencing and Stale Leader Protection

When using lease-based ownership, a resource/version token prevents stale owners from acting after losing the lock.

**Core principles:**
- Store a fencing token with each lock acquisition
- Include the token in all operations; reject operations with stale tokens
- Compare-and-swap (CAS) with TTL enables lease-based locks

```
Replica A (leader) --heartbeat/lease--> coordination store
Replica B/C watch lease -> take over on expiry
```

### Leader Election with a Consistent Core (Optional)

Large clusters may offload leader election to a consistent core (etcd, ZooKeeper, Consul).

**Primitives used:**
- Compare-and-swap for atomic lock acquisition
- TTL for lease expiration
- Watch/notify for leader change detection

This pattern is useful for cluster membership services but not required when using Raft directly for small groups (3-5 nodes).

---

## Section 2: Confman Implementation

This section details how the distributed patterns above are implemented in Confman using DotNext.Net.Cluster and the project structure.

### Project Structure Mapping

| Component | Purpose | Patterns Applied |
|-----------|---------|------------------|
| `Confman.Raft` | Log replication, leader election | Replicated log, quorum, consensus |
| `Confman.Store` | State machine, snapshots | Apply committed entries, follower reads |
| `Confman.Api` | REST gateway, health checks | Stateless tier, leader routing |
| `Confman.Core` | Shared models, interfaces | - |
| `Confman.Infrastructure` | gRPC, persistence | Inter-node communication |

### WAL and Version Mapping

The Raft log serves as the WAL. Each entry is identified by term + index, which together form the resource version.

```csharp
// DotNext-style pseudo-code
if (!raft.IsLeader) return Redirect(raft.LeaderHint);

var entry = new LogEntry(term: raft.CurrentTerm, payload: cmd);
await raft.ReplicateAsync(entry); // returns after quorum commit
store.Apply(entry); // apply only after commit
```

`Confman.Store` applies entries only after the commit index advances. The version (term:index) is returned to clients for read-your-writes support.

### Leader Redirect Pattern

Write operations (PUT/DELETE) on followers return HTTP 307 redirect to the leader. Clients must follow redirects.

```csharp
// In Confman.Api controllers
if (req.Term < raft.CurrentTerm) return Results.Conflict();
if (!raft.IsLeader) return Results.Redirect(raft.LeaderHint);
```

Followers reject requests with stale terms and include the leader hint in responses.

### Follower Reads Implementation

Follower reads use commit index verification to provide read-your-writes consistency.

```csharp
// Write returns version
var version = await raft.ReplicateAsync(cmd); // returns (term, index)
return Results.Ok(new { version = version.ToString() });

// Follower read with version
await raft.WaitForCommitAsync(version.Index, timeout);
return Results.Ok(store.Read(key));
```

The `X-Confman-Version` header carries the version token. Followers block reads (within SLA timeout) until their commit index reaches the requested version.

### Consistency Modes

| Mode | Behavior | Use Case |
|------|----------|----------|
| Leader reads (default) | All reads go to leader | Strict linearizability |
| Follower reads + version | Wait for commit index | Read-your-writes |
| Follower reads (eventual) | No waiting | High-throughput reads |

### Quorum Configuration

Configure Raft groups as 3 or 5 nodes. Writes require majority acknowledgment.

```csharp
// Use raft.CommitIndex as high-water mark for follower commits
// 3-node cluster: quorum = 2
// 5-node cluster: quorum = 3
```

### Sharding Strategy

Confman uses prefix-based or hash-based routing for hierarchical keys.

```csharp
// Hash-based routing
int partition = Hash(key) % logicalPartitions;
string node = shardMap[partition];

// Prefix-based routing (e.g., by namespace)
// /teams/payments/* -> shard-1
// /environments/prod/* -> shard-2
```

Each shard is its own Raft group. A shard registry maps logical partitions to Raft group endpoints. `Confman.Api` serves as the stable gateway.

### Hybrid Logical Clock for Audit

For cross-shard ordering in the audit log, use HLC with monotonic IDs.

```csharp
// Hybrid clock implementation
record Hlc(long WallTimeMs, int Counter);

Hlc Next(Hlc local, Hlc remote)
{
    var wall = Math.Max(local.WallTimeMs, remote.WallTimeMs);
    var counter = wall == local.WallTimeMs ? local.Counter + 1 : 0;
    return new Hlc(wall, counter);
}
```

Within a single Raft group, log order is authoritative. HLC is used when aggregating events across shards.

### Health Endpoints

`Confman.Api` exposes health endpoints for load balancers and orchestrators.

| Endpoint | Purpose | Checks |
|----------|---------|--------|
| `GET /health` | Liveness | Process is running |
| `GET /health/ready` | Readiness | Quorum available, can serve traffic |

### Snapshot Strategy

Snapshots capture the full state machine for faster recovery and log compaction.

**Current implementation:**
- `PersistAsync` serializes configs, namespaces, and audit events to JSON
- `RestoreAsync` clears state and restores from snapshot
- Snapshots are triggered by log size or periodic interval

### Audit Event Replication

Audit events are created on ALL nodes during state machine apply (not just the leader).

**Implementation details:**
- Deterministic ID: `timestamp + namespace + key` for idempotency
- Upsert semantics prevent duplicates during log replay
- All nodes have consistent audit data; dashboard can query any node

### DotNext-Specific Notes

| DotNext Feature | Usage |
|-----------------|-------|
| `IRaftCluster` | Cluster membership and leader state |
| `ReplicateAsync` | Quorum-acknowledged write |
| `CommitIndex` | High-water mark for follower reads |
| `LeaderHint` | Redirect target for writes |
| Persistent log | WAL with log compaction |
| Snapshot support | State machine snapshots |

### External Coordination (Optional)

If using external coordination (etcd, Consul) for cluster membership:
- Treat external store as control-plane only
- Raft handles data replication and consensus
- External store handles node discovery and membership changes
