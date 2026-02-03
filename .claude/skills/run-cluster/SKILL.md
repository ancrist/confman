---
name: run-cluster
description: Start, stop, or check status of the 3-node Confman Raft cluster for local development. Use when the user wants to run the cluster, start nodes, stop nodes, or check if the cluster is healthy.
---

# Run Cluster

Manage the local 3-node Confman Raft cluster using the cluster script.

## Node Configuration

| Node | Environment | URL | Port |
|------|-------------|-----|------|
| Node 1 | `node1` | `http://127.0.0.1:6100` | 6100 |
| Node 2 | `node2` | `http://127.0.0.1:6200` | 6200 |
| Node 3 | `node3` | `http://127.0.0.1:6300` | 6300 |

## Commands

All cluster operations use `scripts/cluster.sh` relative to this skill's base directory.

### Start Cluster

```bash
.claude/skills/run-cluster/scripts/cluster.sh start
```

Starts all 3 nodes in the background, waits for quorum, and prints status. Skips nodes that are already running. Logs are written to `/tmp/confman-node{1,2,3}.log`.

### Stop Cluster

```bash
.claude/skills/run-cluster/scripts/cluster.sh stop
```

### Check Status

```bash
.claude/skills/run-cluster/scripts/cluster.sh status
```

### Wipe Data

```bash
.claude/skills/run-cluster/scripts/cluster.sh wipe
```

Stops nodes and deletes all data directories (`./data-6100/`, `./data-6200/`, `./data-6300/`).

## Guidelines

- Always start all 3 nodes for a functioning cluster. A single node cannot elect itself leader (`coldStart` is false).
- The `ASPNETCORE_ENVIRONMENT` variable selects `appsettings.node1.json`, `appsettings.node2.json`, or `appsettings.node3.json` for node-specific config (member list, public endpoint).
- A healthy cluster has one `Leader` and two `Follower` nodes. Quorum requires 2 of 3 nodes.
- Data is stored in `./data-{port}/` directories relative to the repo root.

## Warning: Never Use `coldStart=true` with Static Members

**Do NOT start any node with `coldStart=true` when using static cluster membership.**

With static member configuration (all nodes listed in `appsettings.json`), using `coldStart=true` causes:

1. **Leader tracking loss** - Nodes lose track of who the leader is after initial election
2. **Cluster instability** - Repeated leader elections and inconsistent state
3. **Client request failures** - HTTP 503 errors due to missing leader references

The `coldStart` flag is designed for **dynamic membership** scenarios where nodes discover each other at runtime. With static membership, all nodes already know about each other from configuration.

**Correct configuration** (already set in `appsettings.nodeN.json`):
```json
{
  "Cluster": {
    "coldStart": false
  }
}
```

See: `docs/solutions/2026-02-01-dotnext-coldstart-leader-tracking-loss.md` for detailed analysis.
