# Repository Guidelines

## Architecture Overview (Distributed Config Service)

- Consensus: Use Raft for leader election and log replication; a quorum (majority) is required for writes and leader changes.
- Primary/Secondary: The Raft leader acts as primary; followers are secondaries that replicate the log and can serve consistent reads when configured.
- Split-brain avoidance: Reject writes without quorum and fence old leaders using term checks.
- Log replication: Append-only log drives the distributed, hierarchical key-value state machine.
- Partitioning: Shard by key prefix/namespace (e.g., `/env/team/app/*`) with independent Raft groups per shard.

## Distributed Concepts: Must-Have vs Optional

### Must-Have (Correctness/HA)

- Consensus (Raft) + quorum for linearizable writes and safe leader election.
- Log replication for the authoritative state machine.
- Split-brain prevention via terms, quorum checks, and leader fencing.
- Failover strategy via leader election timeouts and health checks.
- Primary/Secondary roles (leader/follower) for coordinated writes.
- Read/write API separation so writes always flow to the leader.

### Usually Required (Scale/Performance)

- Partitioning/sharding when keyspace or throughput outgrows a single Raft group.
- Read replicas for scaling read traffic; document staleness or use quorum reads.
- Capacity sizing (N+1, N+M) for durability and maintenance windows.

### Optional/Conditional

- Eventual consistency only for non-critical paths (caches, secondary indexes).
- Last/first-write-wins only for non-critical metadata; avoid for configs.
- Vector clocks only if you introduce offline or concurrent writers outside Raft.
- Active/passive patterns are unnecessary for the core write path in Raft.

## Consistency & Conflict Resolution

- Strong consistency: All writes go through the leader and are committed after quorum replication.
- Read path: Support linearizable reads via leader or quorum reads; expose read replicas only for explicitly stale reads.
- Eventual consistency: Only for opt-in, non-critical paths; document staleness guarantees.
- Conflict resolution: Prefer last-write-wins only for non-critical metadata; otherwise reject conflicting writes.
- Vector clocks: Use for detecting concurrent updates in non-linearizable flows (e.g., offline clients).

## Availability, Failover, and Node Configurations

- Deploy in active/active (multiple voters) with N+1 or N+M capacity; avoid active/passive for core writes.
- Failover: Leader election handles node loss; ensure timeouts and priorities prevent churn.
- Quorum sizing: Typical 3 or 5 nodes per shard; tolerate 1 or 2 failures respectively.
- Read replicas: Optional non-voting followers for read scaling.

## API & Governance

- REST controllers: Separate read (`GET`) and write (`PUT/POST/DELETE`) paths; writes are routed to leader.
- Schema validation: Enforce per-key schemas at write time; store schemas alongside config metadata.
- Rollback: Maintain last-known-good snapshots per namespace; allow `POST /rollback` with audit trail.
- Audit/logs: Record actor, timestamp, term/index, and diff for each change.

## Project Structure & Module Organization

- Suggested layout: `src/` (service), `src/raft/` (consensus), `src/store/` (KV + snapshots), `src/api/`, `tests/`.
- Keep shard/namespace logic isolated from transport and storage adapters.

## Build, Test, and Development Commands

- Tooling not configured yet; document `make test`, `go test ./...`, or `npm test` once selected.

## Coding Style & Naming Conventions

- Keep style consistent per language; prefer explicit names like `leader_election.go` or `raft_log.rs`.

## Testing Guidelines

- Add unit tests for log replication, snapshotting, quorum reads, and rollback behavior.
- Add fault-injection tests for split-brain, partial network partitions, and leader churn.

## Commit & Pull Request Guidelines

- Use short, imperative commit subjects and include testing notes in PRs.

## Security & Configuration Tips

- Keep secrets out of repo; document required env vars and defaults in `docs/`.
