# Cluster Behavior Notes

> Observations from testing the Confman Raft cluster.

---

## Cluster Configuration

| Setting | Value | Description |
|---------|-------|-------------|
| Nodes | 3 | Standard Raft cluster size |
| Quorum | 2 | Majority required for consensus (n/2 + 1) |
| Election Timeout | 150-300ms | Randomized to prevent split votes |
| Heartbeat Threshold | 0.5 | Leader heartbeats at half election timeout |
| Replication Timeout | 10s | Max wait for write replication |

---

## Leader Election

### Normal Election
When the cluster starts, nodes begin an election:
1. Each node starts as a **follower**
2. After election timeout with no heartbeat, a node becomes a **candidate**
3. Candidate requests votes from other nodes
4. First candidate to receive majority votes becomes **leader**
5. Leader sends heartbeats to maintain authority

### Election After Leader Failure
When the leader dies:
1. Followers stop receiving heartbeats
2. After election timeout (150-300ms), a follower becomes candidate
3. New election proceeds as normal
4. **Term number increments** (e.g., term 13 → term 14)

```
Before: Node 2 = Leader (term 13)
        Node 1 = Follower
        Node 3 = Follower

[Node 2 killed]

After:  Node 1 = Leader (term 14)  ← Won election
        Node 2 = Dead
        Node 3 = Follower
```

---

## Replication Behavior

### Write Path
1. Client sends write to any node
2. If node is **not leader**, returns `307 Redirect` to leader
3. Leader appends entry to its WAL
4. Leader replicates entry to followers
5. Once **majority confirms**, entry is committed
6. Leader applies to state machine (LiteDB)
7. Leader responds to client

### Follower Catch-Up
When a follower rejoins after being down:
1. Connects to current leader
2. Leader detects follower's log is behind
3. Leader sends missing log entries
4. Follower applies entries to catch up
5. Follower is now consistent with cluster

**Tested:** Node 2 was killed, `staging` namespace created on new leader, node 2 restarted → node 2 received `staging` through replication.

---

## Quorum Scenarios

### 3-Node Cluster Truth Table

| Alive | Dead | Quorum | Reads | Writes | Leader Election |
|-------|------|--------|-------|--------|-----------------|
| 3 | 0 | ✅ 3/3 | ✅ | ✅ | ✅ |
| 2 | 1 | ✅ 2/3 | ✅ | ✅ | ✅ (if leader died) |
| 1 | 2 | ❌ 1/3 | ✅ | ❌ | ❌ |
| 0 | 3 | ❌ 0/3 | ❌ | ❌ | ❌ |

### Scenario: Two Followers Killed

```
Node 1 (was Leader) → Demotes to Follower
Node 2 (Follower)   → Dead
Node 3 (Follower)   → Dead
```

**Observed behavior:**
- Health endpoint returns `status: not_ready`
- Leader **demotes itself to follower** (can't reach majority)
- `leaderKnown: false` — no leader in cluster
- **Reads still work** — local LiteDB data accessible
- **Writes fail** — `503: No leader available`

### Scenario: Leader + One Follower Killed

```
Node 1 (Leader)   → Dead
Node 2 (Follower) → Dead
Node 3 (Follower) → Running alone
```

**Expected behavior:**
- Remaining node cannot elect itself (needs majority vote)
- No leader = no writes
- Reads work but may be stale
- Cluster unavailable for writes

---

## Key Insights

### Leader Self-Demotion
A leader that cannot communicate with a majority **must step down**. This prevents split-brain scenarios where two leaders could accept conflicting writes during a network partition.

The leader continuously verifies quorum via heartbeats. When it can't reach majority, it demotes to follower to maintain consistency guarantees.

### Reads vs Writes Availability
- **Reads** are served locally from LiteDB — work even without quorum
- **Writes** require Raft consensus — need quorum to commit

This means a degraded cluster can still serve read traffic, making it partially available during failures.

### Term Numbers
Each election increments the term number. This helps:
- Identify stale leaders (leader with lower term steps down)
- Order log entries chronologically
- Detect and resolve conflicts

### Write-Ahead Log (WAL) + LiteDB Hybrid
The architecture uses two persistence layers:
- **WAL**: Append-only Raft log for consensus and crash recovery
- **LiteDB**: Queryable state for serving reads

Benefits:
- WAL is optimized for sequential writes (fast replication)
- LiteDB is optimized for random reads (fast queries)
- On recovery, WAL replays to rebuild consistent state

---

## Failure Recovery Checklist

### Single Node Failure
1. If leader died → automatic election (150-300ms)
2. New leader accepts writes immediately
3. Restart failed node → catches up via log replication
4. **No data loss** — committed entries are on majority

### Quorum Loss (2+ nodes down)
1. Cluster becomes read-only
2. Remaining node(s) demote to follower
3. **Bring back at least one node** to restore quorum
4. Leader election proceeds
5. Writes resume

### Full Cluster Restart
1. Start nodes in any order
2. Election timeout triggers leader election
3. Leader identified, followers connect
4. WAL replayed on each node → state restored
5. **All committed data preserved**

---

## API Behavior During Failures

| Cluster State | GET (reads) | PUT/DELETE (writes) | Health Check |
|---------------|-------------|---------------------|--------------|
| Healthy | `200 OK` | `200/201 OK` | `ready` |
| Leader failover (in progress) | `200 OK` | `503` briefly | `not_ready` |
| No quorum | `200 OK` | `503 No leader` | `not_ready` |
| Node is follower (write) | `307 Redirect` | Redirects to leader | `ready` |

---

## Monitoring Recommendations

### Key Metrics to Watch
- **Term number** — frequent changes indicate instability
- **Leader changes** — too many suggests network issues
- **Replication lag** — followers behind leader
- **Commit latency** — time for writes to be confirmed

### Health Check Usage
```bash
# Liveness (is the process running?)
GET /health → Always 200 if process is up

# Readiness (can it serve traffic?)
GET /health/ready → 200 only if quorum + leader known
```

Use `/health/ready` for load balancer health checks to route traffic away from nodes that can't serve writes.

---

## Scaling Beyond a Single Raft Cluster

Confman uses a **single Raft cluster** where every node has a complete copy of all data. This works well for configuration data (small, read-heavy). When data grows too large or write throughput becomes a bottleneck, you need **partitioned Raft** — multiple independent Raft groups, each owning a subset of data.

### When Partitioning Becomes Necessary

| Problem | Symptom | Solution |
|---------|---------|----------|
| Data too large | Won't fit on single node | Partition data across Raft groups |
| Write throughput | Single leader bottleneck | Multiple Raft groups = multiple leaders |
| Geographic distribution | Latency across regions | Regional Raft groups |

### Partitioned Raft Architecture

```
                    ┌─────────────────┐
                    │  Router/Proxy   │
                    │  (knows which   │
                    │   group owns    │
                    │   which keys)   │
                    └────────┬────────┘
                             │
        ┌────────────────────┼────────────────────┐
        ▼                    ▼                    ▼
┌───────────────┐    ┌───────────────┐    ┌───────────────┐
│ Raft Group A  │    │ Raft Group B  │    │ Raft Group C  │
│ Keys: 0-999   │    │ Keys: 1000-1999│   │ Keys: 2000-2999│
│ ┌───┐┌───┐┌───┐│   │ ┌───┐┌───┐┌───┐│   │ ┌───┐┌───┐┌───┐│
│ │ L ││ F ││ F ││   │ │ F ││ L ││ F ││   │ │ F ││ F ││ L ││
│ └───┘└───┘└───┘│   │ └───┘└───┘└───┘│   │ └───┘└───┘└───┘│
└───────────────┘    └───────────────┘    └───────────────┘
```

Each Raft group is independent — has its own leader, term, and log.

### Partitioning Strategies

#### 1. Consistent Hashing

```
Hash Ring:
         0
         │
    ┌────┴────┐
   /           \
  ╱  Group A    ╲
 │   (0-120)     │
270───────────────90
 │               │
  ╲  Group C    ╱
   \  Group B  /
    └────┬────┘
         │
        180

key = "prod/timeout"
hash("prod/timeout") = 87  →  Group A
```

| Aspect | How It Works |
|--------|--------------|
| **Routing** | `hash(key) mod ring_size` → find owning group |
| **Rebalance** | Add node → only neighboring range moves (k/n keys) |
| **Virtual nodes** | Each physical node owns multiple ranges for balance |
| **Used by** | Amazon DynamoDB, Apache Cassandra, Riak |

#### 2. Range Partitioning

```
Key Space:
├── a-m     → Raft Group 1
├── n-z     → Raft Group 2
└── 0-9     → Raft Group 3

"prod/api-gateway" → starts with 'p' → Group 2
"dev/settings"     → starts with 'd' → Group 1
```

| Aspect | How It Works |
|--------|--------------|
| **Routing** | Key prefix/range lookup |
| **Range queries** | ✅ Efficient — co-located data |
| **Hot spots** | Risk if one range is heavily accessed |
| **Mitigation** | Auto-split busy ranges |
| **Used by** | Google Spanner, CockroachDB, TiKV |

#### 3. Directory-Based

```
┌─────────────────────────────────┐
│   Directory Service             │
│   (itself Raft-replicated)      │
│                                 │
│   "prod/*"     → Group 2        │
│   "staging/*"  → Group 3        │
│   "user:1-1000"→ Group 1        │
│   "user:1001-*"→ Group 4        │
└─────────────────────────────────┘
           │
           ▼
     Route request to
     correct Raft group
```

| Aspect | How It Works |
|--------|--------------|
| **Routing** | Lookup in directory service |
| **Flexibility** | Maximum — arbitrary mappings |
| **Trade-off** | Directory is SPOF |
| **Mitigation** | Replicate directory (often with Raft!) |
| **Used by** | Vitess (MySQL), custom sharding solutions |

### Strategy Comparison

| Strategy | Routing | Range Queries | Rebalance Cost | Hot Spot Risk |
|----------|---------|---------------|----------------|---------------|
| **Consistent Hash** | O(log n) | ❌ Poor | Low (k/n keys) | Medium |
| **Range Partition** | O(log n) | ✅ Excellent | Medium | High |
| **Directory-Based** | O(1) lookup | ✅ If designed | Flexible | Depends |

### Real-World Examples

| System | Strategy | Notes |
|--------|----------|-------|
| **CockroachDB** | Range + Raft | Auto-splits ranges, each range is a Raft group |
| **TiKV** | Range + Raft | Raft groups called "Regions", 96MB default |
| **etcd** | Single Raft | No partitioning, full replication (like Confman) |
| **Consul** | Single Raft | No partitioning for KV store |
| **Kafka** | Partition + Raft | Each partition is a Raft group (KRaft mode) |

### If Confman Needed Partitioning

```
Option A: Hash by namespace
┌─────────────────────────────────────┐
│ hash("prod") mod 3 = 0 → Group A    │
│ hash("staging") mod 3 = 1 → Group B │
│ hash("dev") mod 3 = 2 → Group C     │
└─────────────────────────────────────┘

Option B: Range by namespace prefix
┌─────────────────────────────────────┐
│ a-m/* → Group A                     │
│ n-z/* → Group B                     │
└─────────────────────────────────────┘
```

For configuration data, **single Raft cluster is usually sufficient** — the data is small and read-heavy.

---

## References

- [Raft Paper](https://raft.github.io/raft.pdf) — Original consensus algorithm
- [DotNext Documentation](https://dotnet.github.io/dotNext/) — .NET Raft implementation
- [FEATURES.md](FEATURES.md) — Full feature status
- [README.md](README.md) — API documentation
