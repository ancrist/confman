# System Architecture Brainstorm

**Date:** 2026-01-23
**Status:** Complete
**Next Step:** `/workflows:plan` for implementation

---

## What We're Building

A **single Raft cluster architecture** for Confman where all nodes run identical code (API + Raft + KV Store) and form one consensus group.

```
┌─────────────────────────────────────────────────────────┐
│                    Load Balancer                        │
│            (Reads: any, Writes: forward to leader)      │
└─────────────────────┬───────────────────────────────────┘
                      │
        ┌─────────────┼─────────────┐
        ▼             ▼             ▼
   ┌─────────┐   ┌─────────┐   ┌─────────┐
   │ Node 1  │   │ Node 2  │   │ Node 3  │
   │ (Leader)│◄──│(Follower│◄──│(Follower│
   │         │──►│)        │──►│)        │
   ├─────────┤   ├─────────┤   ├─────────┤
   │ REST API│   │ REST API│   │ REST API│
   │ Raft    │   │ Raft    │   │ Raft    │
   │ KV Store│   │ KV Store│   │ KV Store│
   └─────────┘   └─────────┘   └─────────┘
        │             │             │
        └─────────────┴─────────────┘
              gRPC (Raft protocol)
```

---

## Why This Approach

### Constraints We Learned

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| Deployment | VMs/bare metal | No Kubernetes, traditional infrastructure |
| Scale | 100K+ keys, 100-200 req/sec | High data volume, moderate throughput |
| Access pattern | 70-90% reads | Mixed workload, regular updates |
| Consistency | Bounded staleness OK | Reads from any node acceptable |
| Client access | REST API + staged rollouts | On-demand API, targeting logic deferred |
| Auth | Azure AD | Corporate SSO, mTLS as future option |
| Node discovery | Static configuration | Simplest for stable VM clusters |

### Why Single Raft Cluster Wins

1. **Fits the scale** — 200 req/sec is well within single-leader write capacity
2. **Bounded staleness** — Reading from followers is fine, no quorum reads needed
3. **Simplest operational model** — One cluster, one codebase, uniform nodes
4. **DotNext.Net.Cluster** — Handles leader election, log replication, snapshots
5. **Evolution path clear** — Can add sharding or read replicas later if needed

### Alternatives Considered

| Approach | Why Not Now |
|----------|-------------|
| Sharded by namespace | Adds routing complexity for a scale problem we don't have |
| Read replicas (non-voting) | Overkill when followers can serve reads |

---

## Key Decisions

### 1. Node Architecture

Each node is a single process running:
- **ASP.NET Core** — REST API for clients (port 5000)
- **gRPC** — Raft inter-node communication (port 5001)
- **DotNext Raft** — Consensus, log replication, snapshots
- **KV Store** — State machine (RocksDB or LiteDB)

### 2. Request Flow

**Writes:**
```
Client → Any Node → Forward to Leader → Raft Commit → Apply to KV → Response
```

**Reads (bounded staleness):**
```
Client → Any Node → Read from local KV → Response
```

**Reads (strong consistency, optional):**
```
Client → Leader → Read from KV → Response
```

### 3. Cluster Membership

- **Static configuration** — Node addresses in `appsettings.json`
- **3 or 5 nodes** — Standard Raft quorum (tolerate 1 or 2 failures)
- **No dynamic membership initially** — Manual config updates for node changes

```json
{
  "Cluster": {
    "NodeId": "node1",
    "Nodes": [
      { "Id": "node1", "Address": "10.0.1.10", "ApiPort": 5000, "RaftPort": 5001 },
      { "Id": "node2", "Address": "10.0.1.11", "ApiPort": 5000, "RaftPort": 5001 },
      { "Id": "node3", "Address": "10.0.1.12", "ApiPort": 5000, "RaftPort": 5001 }
    ]
  }
}
```

### 4. Authentication

- **Azure AD / OIDC** — For human users (Microsoft.Identity.Web)
- **Service principals** — For service-to-service calls
- **mTLS** — Future consideration for inter-node and client auth

### 5. Load Balancing

- **External load balancer** — Routes client traffic to any healthy node
- **Health endpoint** — `/health/ready` includes quorum status
- **Leader awareness** — Clients can optionally target leader for strong reads

### 6. Data Storage

- **Raft log** — Append-only, managed by DotNext
- **State machine** — Hierarchical KV store (RocksDB or LiteDB)
- **Snapshots** — Periodic compaction to bound log growth

---

## Component Interaction

```
┌──────────────────────────────────────────────────────────────────┐
│                           Node Process                           │
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌─────────────┐     ┌─────────────┐     ┌─────────────────────┐│
│  │  REST API   │────►│  Service    │────►│    Raft Engine      ││
│  │ Controllers │     │  Layer      │     │  (DotNext.Cluster)  ││
│  └─────────────┘     └─────────────┘     └──────────┬──────────┘│
│        │                   │                        │           │
│        │                   │              ┌─────────▼─────────┐ │
│        │                   │              │   Persistent Log  │ │
│        │                   │              │   (Raft entries)  │ │
│        │                   │              └───────────────────┘ │
│        │                   │                        │           │
│        │                   ▼                        ▼           │
│        │            ┌─────────────┐         ┌─────────────┐     │
│        │            │ KV Store    │◄────────│ State       │     │
│        └───────────►│ (RocksDB)   │         │ Machine     │     │
│         (reads)     └─────────────┘         └─────────────┘     │
│                                                                  │
├──────────────────────────────────────────────────────────────────┤
│  gRPC Server (Raft protocol) ◄──────────────► Other Nodes       │
└──────────────────────────────────────────────────────────────────┘
```

---

## Open Questions (For Planning Phase)

### Storage
- [ ] RocksDB vs LiteDB — Performance testing needed
- [ ] Snapshot frequency and retention policy

### Networking
- [ ] gRPC channel configuration (keepalive, retries)
- [ ] TLS for inter-node communication

### Operations
- [ ] Rolling upgrade strategy
- [ ] Backup and restore procedures
- [ ] Monitoring dashboards (Grafana templates)

### Features (Deferred)
- [ ] Staged rollouts with topology-aware targeting
- [ ] mTLS for client authentication
- [ ] Dynamic cluster membership

---

## Success Criteria

1. **Cluster forms and elects leader** within configured timeout
2. **Writes committed** only after quorum acknowledgment
3. **Reads succeed** from any healthy node
4. **Leader failure** triggers election within seconds
5. **Split-brain prevented** — Minority partition rejects writes

---

## Next Steps

1. Run `/workflows:plan` to create implementation plan
2. Scaffold project structure
3. Implement Raft integration with DotNext
4. Build REST API layer
5. Add authentication and RBAC
