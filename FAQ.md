# Frequently Asked Questions

---

## Q1: Why use a Raft cluster instead of a centralized database?

With a centralized DB, you'd have 3 stateless services behind a load balancer, all reading/writing to one database that guarantees atomicity. Here's how the approaches compare:

| Aspect | Centralized DB + Services | Raft Cluster |
|--------|---------------------------|--------------|
| **Single Point of Failure** | ❌ DB is SPOF — if DB dies, all services fail | ✅ No SPOF — any node can serve reads, majority needed for writes |
| **Network Latency** | ❌ Every request hits DB over network | ✅ Reads served locally from embedded storage |
| **Operational Complexity** | ✅ Simpler — one DB to manage, backup, monitor | ❌ More complex — N nodes to manage, consensus to understand |
| **Scaling Reads** | ❌ DB becomes bottleneck (or need read replicas) | ✅ Any node can serve reads locally |
| **Scaling Writes** | ❌ Limited by single DB throughput | ❌ Limited by consensus (leader bottleneck) |
| **Consistency Model** | ✅ Strong — DB handles ACID | ✅ Strong — Raft provides linearizability |
| **Data Locality** | ❌ Data on separate server | ✅ Data co-located with service |
| **Deployment** | ✅ Services are stateless, easy to deploy | ❌ Stateful nodes, need persistent storage |
| **Cost** | ❌ DB infrastructure (often expensive for HA) | ✅ Embedded storage, no separate DB cost |
| **Failover Time** | ❌ DB failover can be slow (30s+) | ✅ Fast leader election (150-300ms) |
| **Split-Brain Risk** | ✅ DB prevents it | ✅ Raft quorum prevents it |
| **Debugging** | ✅ Easier — single source of truth | ❌ Harder — distributed state across nodes |

### When to Use Each

| Use Centralized DB When | Use Raft Cluster When |
|-------------------------|----------------------|
| You already have DB infrastructure | You need minimal external dependencies |
| Team is familiar with SQL/DB ops | Config data should be co-located with services |
| Complex queries needed | Simple key-value access patterns |
| Write-heavy workload | Read-heavy workload |
| Single region deployment | Need fast local reads at edge/multi-region |
| Stateless services preferred | Tight latency requirements for reads |

### Confman's Use Case Fits Raft Well Because:
- Configuration data is read-heavy, write-light
- Fast local reads matter (services checking config frequently)
- Small data size (fits in memory/embedded DB)
- Self-contained deployment (no external DB dependency)

---

## Q2: Is the whole point of a Raft cluster that each node has local storage?

**No.** Local storage is a *consequence* of the architecture, not the primary goal.

### The Real Purpose of Raft

| Goal | Description |
|------|-------------|
| **Consensus** | All nodes agree on the same sequence of operations |
| **Fault Tolerance** | Survive node failures without data loss |
| **Consistency** | Strong guarantee that all nodes see the same state |
| **Availability** | Continue operating despite minority failures |

### What Raft Actually Guarantees

```
Client writes "X=5" to leader

     Leader              Follower 1           Follower 2
        │                    │                    │
   1. Write to WAL           │                    │
        │                    │                    │
   2. ──────── Replicate ────┼────────────────────┤
        │                    │                    │
        │               Write to WAL         Write to WAL
        │                    │                    │
   3. ◄─────── ACK ──────────┼────────────────────┤
        │                    │                    │
   4. Commit (majority)      │                    │
        │                    │                    │
   5. Apply to state         Apply               Apply
        │                    │                    │
   6. Reply to client
```

The guarantee: **Once committed, the write survives any minority failure.**

### Local Storage is a Bonus

| Benefit | From Raft Consensus | From Local Storage |
|---------|--------------------|--------------------|
| Fault tolerance | ✅ | |
| Strong consistency | ✅ | |
| No single point of failure | ✅ | |
| Fast local reads | | ✅ |
| No network hop for reads | | ✅ |
| Works offline (for reads) | | ✅ |

**Raft's core insight** is solving the consensus problem: "How do N nodes agree on a sequence of operations even when some fail?" Local storage is how we *apply* that agreed sequence, but the algorithm itself is about **log replication and leader election**, not storage.

---

## Q3: Is Confman's persistence model in-memory? What happens if a node fails?

**No, Confman persists to disk.** Both storage layers are durable:

| Component | Storage | Location | Purpose |
|-----------|---------|----------|---------|
| **Raft WAL** | Disk | `./data-{port}/raft-log/` | Consensus log, crash recovery |
| **LiteDB** | Disk | `./data-{port}/confman.db` | Queryable state (configs, namespaces, audit) |

### What Happens When a Node Fails

```
Node crashes
     │
     ▼
┌─────────────────────────────────────┐
│  On Disk (survives crash):          │
│  ├── data-6100/confman.db     ✅    │
│  └── data-6100/raft-log/      ✅    │
└─────────────────────────────────────┘
     │
     ▼
Node restarts
     │
     ▼
┌─────────────────────────────────────┐
│  Recovery:                          │
│  1. LiteDB opens confman.db         │
│  2. WAL replayed (RestoreStateAsync)│
│  3. State fully restored            │
└─────────────────────────────────────┘
```

**No data loss** — both persistence layers are durable.

### Comparison: In-Memory vs Disk Persistence

| Scenario | If In-Memory | Disk (Current) |
|----------|--------------|----------------|
| Single node crash | ❌ Data lost on that node | ✅ Data survives restart |
| Majority crash simultaneously | ❌ Committed data lost | ✅ Data survives |
| Power outage (all nodes) | ❌ Total data loss | ✅ Full recovery |

### Defense in Depth

Even though Raft guarantees committed data exists on a majority, Confman persists to disk on *every* node. This means:
- Single node restart → local recovery (fast)
- Full cluster restart → all nodes recover independently
- No need to transfer state from peers unless WAL is corrupted/deleted

---

For more details see [CLUSTER_NOTES.md](CLUSTER_NOTES.md).
