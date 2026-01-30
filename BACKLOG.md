# Confman Backlog

Features and improvements planned for future releases.

---

## High Priority

### Version Token Support for Follower Reads

**Status:** Not Started
**Complexity:** Medium
**Value:** High (enables read scaling while maintaining consistency)

#### Problem

Raft guarantees that all committed entries are applied in the same order on all nodes, but it does NOT guarantee that a follower has applied a specific entry *right now*. This creates a read-your-writes violation:

```
Timeline:
─────────────────────────────────────────────────────────►

Client          Leader              Follower
   │                │                    │
   │──PUT foo=bar──►│                    │
   │                │──replicate────────►│ (in flight)
   │                │◄──quorum ack───────│
   │◄──200 OK───────│                    │
   │                │                    │ (still applying...)
   │──GET foo──────────────────────────►│
   │◄──foo=??? ─────────────────────────│ ← STALE!
```

The follower acknowledged the replication (for quorum), but may not have *applied* it to the state machine yet. Or the client might hit a different follower that wasn't in the quorum.

#### Current Behavior

Confman allows reads from any node without checking freshness:
- Client writes `foo=bar`, gets success
- Immediately reads `foo` from a follower
- Might get the old value (or nothing)

This violates **read-your-writes consistency**.

#### Why Not Just Force All Reads to Leader?

For a config service that's 70-90% reads, forcing all reads through the leader wastes the followers' capacity:

| Approach | Consistency | Throughput | Leader Load |
|----------|-------------|------------|-------------|
| Leader-only reads | Linearizable | Limited by 1 node | 100% |
| Follower reads (no version) | Eventual | High | Low |
| Follower reads + version token | Read-your-writes | High | Low |

#### Solution: Version Tokens

```
Timeline with version tokens:
─────────────────────────────────────────────────────────►

Client          Leader              Follower
   │                │                    │
   │──PUT foo=bar──►│                    │
   │                │──replicate────────►│
   │◄──200 OK───────│                    │
   │  (version=5:42)│                    │
   │                │                    │
   │──GET foo (version≥5:42)───────────►│
   │                │                    │ (commit_index=5:41, wait...)
   │                │                    │ (commit_index=5:42, OK!)
   │◄──foo=bar─────────────────────────│ ← CORRECT!
```

The follower blocks briefly until it has caught up, then serves the read.

#### Implementation Requirements

1. **Write responses return version token**
   - Header: `X-Confman-Version: {term}:{index}`
   - Body: Include `version` field in JSON response

2. **Read requests accept version constraint**
   - Header: `X-Confman-Min-Version: {term}:{index}`
   - Query param alternative: `?minVersion=5:42`

3. **Follower read logic**
   ```csharp
   // Pseudocode
   if (minVersion != null)
   {
       await raft.WaitForCommitAsync(minVersion.Index, timeout);
   }
   return store.Read(key);
   ```

4. **Timeout handling**
   - If follower can't catch up within SLA (e.g., 5s), return 503 or redirect to leader

5. **Consistency mode header** (optional)
   - `X-Confman-Consistency: linearizable` → force leader read
   - `X-Confman-Consistency: read-your-writes` → use version token
   - `X-Confman-Consistency: eventual` → no waiting (current behavior)

#### When This Matters

- UI updates: User sets config, page refreshes, expects to see new value
- Test automation: Write config, immediately verify it was set
- Chained operations: Service A writes, tells Service B to read

#### When Current Behavior Is Fine

- Periodic polling (every 30s doesn't care about 50ms staleness)
- Single-client sessions always hitting same node
- Workloads tolerant of eventual consistency

#### Files to Modify

- `src/Confman.Api/Controllers/ConfigController.cs` — Add version header to write responses, version check to reads
- `src/Confman.Api/Controllers/NamespacesController.cs` — Same
- `src/Confman.Api/Services/RaftService.cs` — Expose commit index wait functionality
- `src/Confman.Api/Middleware/` — Add middleware to parse consistency headers

#### References

- [DIST.md](./DIST.md) — Section "Follower Reads and Read-Your-Writes"
- [FAILURE_MODES.md](./FAILURE_MODES.md) — Consistency during failures

---

## Medium Priority

### Consistency Mode Selection

**Status:** Not Started
**Depends on:** Version Token Support

Allow clients to explicitly choose consistency level per request:
- `linearizable` — All operations through leader
- `read-your-writes` — Version token verification
- `eventual` — Direct follower reads (current behavior)

---

### Sharding / Multi-Raft Support

**Status:** Not Started
**Complexity:** High

Partition data across multiple Raft groups for horizontal scaling:
- Prefix-based routing (`/teams/payments/*` → shard-1)
- Hash-based routing for even distribution
- Shard registry mapping logical partitions to Raft groups
- Gateway routing layer in `Confman.Api`

**Prerequisite for:** Hybrid Logical Clock (needed for cross-shard ordering)

---

## Low Priority

### Hybrid Logical Clock for Audit

**Status:** Not Started
**Depends on:** Sharding

Only needed when aggregating audit events across multiple Raft groups. Single-cluster deployment uses Raft log order.

---

## Completed

_None yet — this backlog was just created._
