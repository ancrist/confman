# Distributed Systems Patterns (Fowler + Burns) - Confman

This document merges relevant patterns from "Patterns of Distributed Systems" (Fowler) and "Designing Distributed Systems" (Burns) and maps them to Confman’s architecture (Raft-based, strongly consistent, partitioned config store).

## Replicated Log + Quorum Commit

Key ideas (Fowler):
- Write-Ahead Log (WAL) provides a linear history; replay is deterministic.
- Leaders commit only after Majority Quorum acknowledges the log entry.
- Uncommitted entries are never applied to the state machine.

Key ideas (Burns):
- Replication is a core reliability pattern; use replication to mask failures.

ASCII diagram:

```
Client -> Leader
             | append log entry (uncommitted)
             | replicate to followers
             v
          [Majority ACK] -> commit index advances
```

Implementation notes (Confman):
- WAL maps to the Raft log; log index + term = version.
- `Confman.Store` applies entries only after commit index advances.

## Leader and Followers (Consensus)

Key ideas (Fowler):
- Only leader handles writes; followers replicate and forward client writes.
- Leader heartbeats prevent split-brain and trigger elections on timeout.

Key ideas (Burns):
- Ownership/master pattern: use a single leader for coordinated decisions.

ASCII diagram:

```
Follower --forward--> Leader --replicate--> Follower
    ^                                      |
    +--------------heartbeat---------------+
```

Implementation notes (Confman):
- Term increments each election; followers reject stale-term requests.
- `Confman.Api` redirects writes to leader, returns leader hint.

## Majority Quorum and Safety

Key ideas (Fowler):
- Majority quorum ensures committed updates survive leader changes.
- Leaders must be most up-to-date (highest term, then highest log index).
- New leader must complete replication of any uncommitted entries.

ASCII diagram:

```
3-node cluster quorum = 2
commit when >=2 acks
```

Implementation notes (Confman):
- Prefer 3 or 5 nodes per Raft group; writes require quorum.
- Use commit index / high-water mark for safe follower commits.

## Follower Reads + Read-Your-Writes

Key ideas (Fowler):
- Followers can serve reads, but may be stale.
- Versioned values and high-water marks enable read-your-writes.

Key ideas (Burns):
- Replication can offload reads from leaders when staleness is acceptable.

ASCII diagram:

```
Client write -> leader returns version V
Client read -> follower waits until commit_index >= V
```

Implementation notes (Confman):
- Return `X-Confman-Version` (term:index) on write.
- Followers block reads until commit index reaches version or timeout.

## Consistency and Linearizability

Key ideas (Fowler):
- Quorum read/write alone can still show anomalies.
- Linearizable reads require coordination with leader or validated commit index.

Key ideas (Burns):
- Consistency models are not the focus; the book emphasizes infrastructure patterns.

Implementation notes (Confman):
- Default to leader reads for linearizability.
- Allow follower reads only with explicit consistency mode or version token.

## Partitioning (Sharding) + Routing (Ambassador)

Key ideas (Fowler):
- Partitioning splits data across nodes; use logical partitions to ease rebalancing.

Key ideas (Burns):
- Sharding needs routing logic; implement via service-side routing or client-side ambassador.
- Stateful shards often use stable network identities (e.g., StatefulSets).

ASCII diagram:

```
Client/App -> Ambassador (localhost) -> Shard A/B/C
Key -> hash/prefix -> logical partition -> node
```

Implementation notes (Confman):
- Shard by key prefix or hash; keep a logical shard map for rebalancing.
- Each shard is its own Raft group; a routing layer maps keys to shard leaders.
- Use `Confman.Api` as stable gateway to avoid rewriting clients.

## Replicated Services (Stateless Tier)

Key ideas (Burns):
- Stateless services are replicated behind a load balancer.
- Health checks prevent traffic to unhealthy nodes.

Key ideas (Fowler):
- Replication masks failures but requires software to handle inconsistencies.

Implementation notes (Confman):
- `Confman.Api` is stateless and horizontally replicated.
- Expose `/health` and `/health/ready` for load balancers.

## Replication vs Partitioning Trade-offs

Key ideas (Fowler + Burns):
- Replication improves resilience and read throughput.
- Partitioning scales state size and isolates workloads.

Implementation notes (Confman):
- Partition by namespace/key prefix and replicate within each shard via Raft.

## Distributed Time and Ordering

Key ideas (Fowler):
- System clocks are unreliable for ordering; use Lamport or Hybrid clocks.
- Clock-bound wait can enforce visibility ordering when required.

Key ideas (Burns):
- Time issues are referenced operationally; not a primary focus.

Implementation notes (Confman):
- Raft log order is authoritative for a shard.
- For cross-shard ordering/audit, use HLC or monotonic IDs.

## Leader Election with a Consistent Core (Optional)

Key ideas (Fowler + Burns):
- External coordination stores (etcd/ZooKeeper) can handle leader election with CAS + TTL + watch.

Implementation notes (Confman):
- Not required for small Raft groups (3-5), but useful for membership/control plane.
- Treat external stores as coordination only, not as the data source.
