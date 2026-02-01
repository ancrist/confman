---
title: "DotNext coldStart Conflicts with Pre-Configured Members"
category: distributed-systems
tags: [dotnext, raft, cluster, coldstart, leader-election, configuration]
module: Confman.Api
symptoms:
  - Node reports leaderKnown false after re-election
  - "No leader in cluster" logged but never recovers
  - /health/ready returns 503 on one node while others are healthy
  - Node receives replicated data but cannot serve reads
  - ApplyReadBarrierAsync throws QuorumUnreachableException on affected node
date: 2026-02-01
---

# DotNext coldStart Conflicts with Pre-Configured Members

## Problem

In a 3-node Raft cluster using `UseInMemoryConfigurationStorage` with all members pre-configured, starting one node with `coldStart: true` causes it to **permanently lose leader tracking** after re-election. The node continues replicating data (state machine is up-to-date) but reports `leaderKnown: false`, returning 503 on all read requests.

```bash
# Node 1 started with coldStart override
ASPNETCORE_ENVIRONMENT=node1 dotnet run --no-launch-profile --coldStart true \
  --urls "http://127.0.0.1:6100"
```

```
[WRN] ClusterLifetime: No leader in cluster        ← never recovers
[DBG] ConfigStateMachine: Applied SetConfigCommand   ← still replicating
```

```bash
curl -s http://127.0.0.1:6100/health/ready | jq .cluster
# { "role": "follower", "leaderKnown": false, "leader": null, "term": 1 }
```

## Root Cause

**DotNext has two mutually exclusive bootstrapping strategies. Mixing them corrupts the node's internal state.**

### Strategy 1: Cold Start (dynamic membership)

One node starts with `coldStart: true`, self-elects, and other nodes join via `AddMemberAsync`. Each node discovers the cluster incrementally.

### Strategy 2: Pre-Populated Members (static membership)

All nodes start with `UseInMemoryConfigurationStorage` containing the full member list. All use `coldStart: false`. Nodes can elect a leader as soon as a quorum is available.

### Why Mixing Breaks

When `coldStart: true` is used on a node that already has pre-populated members:

1. **Fingerprint divergence** — DotNext generates a **random `long`** as the configuration fingerprint (not a content hash). The cold start code path generates a different fingerprint than the builder, even though the member list is identical.

2. **Re-election triggers the failure** — When the cold-started node steps down from leader:
   - `VoteAsync` sets `Leader = null` (fires "No leader in cluster")
   - First heartbeat from new leader has a **mismatched fingerprint** → rejected
   - `TryGetMember(sender)` returns null (cold start path's incomplete member initialization)
   - `Leader` is never set back to a valid value

3. **Permanent state** — Subsequent heartbeats sync fingerprints, but `TryGetMember` keeps returning null. The node processes heartbeats (election timeout refreshes, stays follower) but never identifies the leader.

### Debug Log Evidence

```
[03:25:20.336Z] No leader in cluster
[03:25:20.338Z] Incoming configuration with 4417827301438578331 fingerprint from leader.
                Local fingerprint is 6878525909156576378, apply False    ← REJECTED
[03:25:21.063Z] Incoming configuration with 4417827301438578331 fingerprint from leader.
                Local fingerprint is 4417827301438578331, apply True     ← synced
[03:25:21.750Z] Member is downgrading to follower state with term 1     ← repeats forever
[03:25:21.750Z] Election timeout is refreshed                           ← heartbeats arrive
                                                                        ← but NO "Leader changed to"
```

## Solution

**Never use `coldStart: true` when members are pre-configured via `UseInMemoryConfigurationStorage`.**

All nodes should use `coldStart: false` (already the default in `appsettings.nodeN.json`). Do not override with `--coldStart true` on the command line.

### Correct startup

```bash
# All nodes — no coldStart override
ASPNETCORE_ENVIRONMENT=node1 dotnet run --no-launch-profile --urls http://127.0.0.1:6100
ASPNETCORE_ENVIRONMENT=node2 dotnet run --no-launch-profile --urls http://127.0.0.1:6200
ASPNETCORE_ENVIRONMENT=node3 dotnet run --no-launch-profile --urls http://127.0.0.1:6300
```

Start at least 2 nodes to form a quorum and elect a leader. Nodes can be started in any order.

### Incorrect startup (causes this bug)

```bash
# DON'T DO THIS — coldStart conflicts with pre-configured members
ASPNETCORE_ENVIRONMENT=node1 dotnet run --no-launch-profile --coldStart true \
  --urls http://127.0.0.1:6100
```

## Key Insights

1. **Configuration fingerprints are random, not content-based** — Two nodes with identical member lists will have different fingerprints. The fingerprint is a change token replicated by the leader to followers, not a hash of the configuration.

2. **Cold start + pre-populated members = conflicting state** — Cold start generates its own fingerprint and initialization path. Pre-populated members generate a different fingerprint via the builder. The divergence causes heartbeat rejection after re-election.

3. **`TryGetMember` is the proximate failure** — Even after fingerprints sync, the `TryGetMember(sender)` lookup in `AppendEntriesAsync` returns null, preventing the `Leader` property from ever being set on the affected node.

4. **Data replication still works** — The affected node applies log entries normally (state machine stays up-to-date). Only the `Leader` property is broken, which affects read barriers and health checks but not write replication.

5. **DotNext docs confirm the design** — [Official documentation](https://dotnet.github.io/dotNext/features/cluster/raft.html): "Another way of cluster bootstrapping is to pre-populate a list of cluster members with 3 or more cluster members and start them in parallel. In this case, Cold start mode is unnecessary."

## When to Use coldStart

| Scenario | coldStart | Members |
|----------|-----------|---------|
| Static cluster (all members known) | `false` on all nodes | Pre-configured via `UseInMemoryConfigurationStorage` |
| Dynamic cluster (nodes join over time) | `true` on first node only | Not pre-configured; use `AddMemberAsync` |
| Single-node development | `true` | Not needed (single member) |

## References

- [DotNext Raft Documentation](https://dotnet.github.io/dotNext/features/cluster/raft.html)
- [dotnet/dotNext#153](https://github.com/dotnet/dotNext/issues/153) — Cluster fails to elect new leader after nodes rejoin
- [dotnet/dotNext#135](https://github.com/dotnet/dotNext/issues/135) — Dynamic cluster membership not working as expected
- [GitHub Issue #6](https://github.com/ancrist/confman/issues/6) — Original bug report
- `src/Confman.Api/Program.cs:61-77` — Cluster configuration with `UseInMemoryConfigurationStorage`
- `src/Confman.Api/Cluster/ClusterLifetime.cs` — Leader change event handler
- `src/Confman.Api/appsettings.node1.json` — Node configuration with `coldStart: false`
