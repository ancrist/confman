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

## References

- [Raft Paper](https://raft.github.io/raft.pdf) — Original consensus algorithm
- [DotNext Documentation](https://dotnet.github.io/dotNext/) — .NET Raft implementation
- [FEATURES.md](FEATURES.md) — Full feature status
- [README.md](README.md) — API documentation
