# Patterns of Distributed Systems (Fowler) - Confman Notes

This document extracts and adapts relevant patterns from "Patterns of Distributed Systems" for Confman (a strongly consistent, partitioned, Raft-based config store). It focuses on consensus, replication, quorum, leader/follower, follower reads, linearizability, and distributed time.

## Replicated Log + Quorum Commit

Key ideas:
- Write-Ahead Log (WAL) gives a linear history of state changes; replaying the log reproduces state deterministically.
- Replication becomes copying log entries to followers; a leader commits only after a Majority Quorum acknowledges.
- Uncommitted entries must not be applied to the state machine until quorum ack arrives.

ASCII diagram:

```
Client -> Leader
             | append log entry (uncommitted)
             | replicate to followers
             v
          [Majority ACK] -> commit index advances
```

Implementation details (Confman):
- Map WAL to Raft log; log index + term = version (resource version).
- `Confman.Raft` holds the log and replication; `Confman.Store` applies committed entries only.

High-level C# sketch:

```csharp
// DotNext-style pseudo-code
if (!raft.IsLeader) return Redirect(raft.LeaderHint);
var entry = new LogEntry(term: raft.CurrentTerm, payload: cmd);
await raft.ReplicateAsync(entry); // returns after quorum commit
store.Apply(entry); // apply only after commit
```

## Leader and Followers (Consensus)

Key ideas:
- Only one leader processes writes; followers replicate.
- Followers forward writes to leader.
- Leader sends periodic heartbeats; absence triggers election.

ASCII diagram:

```
Follower --forward--> Leader --replicate--> Follower
    ^                                      |
    +--------------heartbeat---------------+
```

Implementation details (Confman):
- Term ("generation clock" in the book) increments each election.
- Followers reject stale-term requests and return leader hint.

High-level C# sketch:

```csharp
if (req.Term < raft.CurrentTerm) return Results.Conflict();
if (!raft.IsLeader) return Results.Redirect(raft.LeaderHint);
```

## Majority Quorum and Safety

Key ideas:
- A majority quorum ensures committed entries survive leader changes.
- Leaders must be the most up-to-date (highest term, then highest log index).
- A new leader completes replication of any uncommitted entries before serving writes.

ASCII diagram:

```
3-node cluster quorum = 2
commit when >=2 acks
```

Implementation details (Confman):
- Configure Raft groups as 3 or 5 nodes; require quorum for writes.
- Use `raft.CommitIndex` as the high-water mark for follower commits.

## Follower Reads + Read-Your-Writes

Key ideas:
- Followers can serve reads to reduce leader load but may be stale.
- To achieve read-your-writes, return a version token on write and require the follower to wait until it has caught up to that version.
- In Raft, followers can use a high-water mark (commit index) before answering.

ASCII diagram:

```
Client write -> leader returns version V
Client read -> follower waits until commit_index >= V
```

Implementation details (Confman):
- Include `X-Confman-Version` in write responses (term:index).
- Follower reads block (within SLA) until commit index >= requested version.

High-level C# sketch:

```csharp
var version = await raft.ReplicateAsync(cmd); // returns (term,index)
return Results.Ok(new { version = version.ToString() });

// Follower read
await raft.WaitForCommitAsync(version.Index, timeout);
return Results.Ok(store.Read(key));
```

## Consistency and Linearizability

Key ideas:
- Quorum read/write alone can still expose anomalies; linearizable reads require coordination with the leader or a proven commit index.
- Linearizable read options: leader-only reads, or quorum/lease reads with commit index guarantees.

ASCII diagram:

```
Linearizable read
Client -> Leader (or quorum) -> commit index verified -> respond
```

Implementation details (Confman):
- Default to leader reads for strict correctness.
- Allow follower reads only with explicit consistency mode or version token.

## Partitioning (Sharding)

Key ideas:
- Data is partitioned to scale state size; each partition owns a key range.
- Use logical partitions (many more than physical nodes) so rebalancing moves only some partitions.

ASCII diagram:

```
Key -> hash -> logical partition -> node
```

Implementation details (Confman):
- Use prefix or hash-based routing for `/namespace/...` keys.
- Keep a stable logical shard map; remap shards to nodes on scale-out.

High-level C# sketch:

```csharp
int partition = Hash(key) % logicalPartitions;
string node = shardMap[partition];
```

## Replication vs Partitioning Trade-offs

Key ideas:
- Replication provides resilience and read scaling; partitioning provides data size scaling.
- For Confman, use replication within each shard (Raft group) and partitioning across shards.

## Distributed Time and Ordering

Key ideas:
- System clocks are not reliable for ordering writes across nodes (clock skew).
- Lamport clocks provide partial order; Hybrid clocks combine wall time + logical counter.
- Clock-bound wait uses a maximum skew bound to ensure ordering visibility.

ASCII diagram:

```
Hybrid clock: (wall_time, logical_counter)
```

Implementation details (Confman):
- Prefer Raft log order for authoritative sequencing.
- For cross-shard reads or global audit ordering, use hybrid timestamps (HLC) or monotonic IDs.

High-level C# sketch:

```csharp
// Hybrid clock (simplified)
record Hlc(long WallTimeMs, int Counter);

Hlc Next(Hlc local, Hlc remote)
{
    var wall = Math.Max(local.WallTimeMs, remote.WallTimeMs);
    var counter = wall == local.WallTimeMs ? local.Counter + 1 : 0;
    return new Hlc(wall, counter);
}
```

## Leader Election with a Consistent Core (Optional)

Key ideas:
- Large clusters may offload leader election to a consistent core (etcd/ZooKeeper).
- Uses compare-and-swap, TTL, and watch/notify semantics.

Implementation details (Confman):
- Not required for Raft groups of 3-5 nodes, but useful for cluster membership services.
- If used, treat the external store as the source of membership and leader hints, not as the data store.
