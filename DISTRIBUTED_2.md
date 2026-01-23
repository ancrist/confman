# Designing Distributed Systems (Burns) - Confman Notes

This file extracts relevant parts of "Designing Distributed Systems" for Confman. The book focuses on reusable patterns and infrastructure mechanisms more than formal consensus theory, so some topics (e.g., linearizability, Raft internals) are only referenced indirectly.

## Sharding and Routing (Ambassador Pattern)

Key ideas:
- Sharding splits a storage layer into disjoint pieces, each hosted on a separate machine.
- A routing layer is required to map a request to the correct shard.
- Two approaches: embed sharding logic in the service or use a client-side ambassador to route requests.

ASCII diagram:

```
Client/App -> Ambassador (localhost) -> Shard A/B/C
```

Implementation notes (Confman):
- Use a sharding map for hierarchical keys (prefix-based or hash-based) and route to the shard’s leader.
- A local proxy (sidecar) or SDK can hide shard logic and leader redirects from callers.

## Sharded Services and Stateful Scaling

Key ideas:
- Sharded services are for stateful systems when data size exceeds a single node.
- Shards are often deployed as StatefulSets (stable DNS names) with a proxy for routing.

Implementation notes (Confman):
- Model each shard as its own Raft group; keep a shard registry that maps shards to node endpoints.
- Expose a stable routing layer (e.g., `Confman.Api` gateway) to avoid rewriting clients.

## Replicated Services (Stateless Tier)

Key ideas:
- Stateless services are replicated behind a load balancer for availability and scale.
- Health checks are used to avoid routing traffic to unhealthy nodes.

Implementation notes (Confman):
- `Confman.Api` is replicated and should remain stateless; leader routing happens per request.
- Liveness/readiness endpoints are critical for safe rollout.

## Ownership / Master Election (Leader Pattern)

Key ideas:
- Distributed systems often require a single owner/master for a task.
- Use a consensus-backed key-value store (e.g., etcd/ZooKeeper) for leader election rather than implementing one from scratch.
- Compare-and-swap (CAS) with TTL enables a lease-based lock; renew before TTL expiry.
- Store a resource/version with the lock to prevent stale owners from acting after losing the lock.

ASCII diagram:

```
Replica A (leader) --heartbeat/lease--> KV store
Replica B/C watch lease -> take over on expiry
```

Implementation notes (Confman):
- Raft already provides leader election; still use the “version check” idea to fence stale leaders.
- If an external coordination store is used for cluster membership, treat it as control-plane only.

## Replication and Consistency (What the Book Covers)

Key ideas:
- The book emphasizes replication as a reliability pattern rather than formal consistency models.
- Consensus algorithms (Raft/Paxos) are referenced mainly as implementations of replicated state.

Implementation notes (Confman):
- Use Raft for strong consistency; implement reads via leader or quorum reads for linearizability.
- For follower reads, require a version token so a follower waits until it has caught up.

## Distributed Time (Limited Coverage)

Key ideas:
- The book does not focus on logical clocks or linearization; time is mostly discussed in operational contexts.

Implementation notes (Confman):
- Use Raft log order for authoritative sequencing.
- If cross-shard ordering is required, adopt Hybrid Logical Clocks (HLC) in the audit/event model.
