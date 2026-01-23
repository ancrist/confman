---
title: DotNext Raft HTTP Cluster Member Configuration
category: distributed-systems
tags: [dotnext, raft, cluster, configuration, aspnetcore]
module: Confman.Api
symptoms:
  - Nodes start in Standby state
  - "Node started in Standby state" in logs
  - Cluster members not discovering each other
  - members array in appsettings.json ignored
date: 2026-01-24
---

# DotNext Raft HTTP Cluster Member Configuration

## Problem

When configuring a DotNext Raft HTTP cluster, adding a `members` array to `appsettings.json` does not work. Nodes start in **Standby state** instead of participating in the cluster, even with the configuration:

```json
{
  "publicEndPoint": "http://127.0.0.1:6000/",
  "coldStart": false,
  "members": [
    "http://127.0.0.1:6000/",
    "http://127.0.0.1:6001/",
    "http://127.0.0.1:6002/"
  ]
}
```

## Root Cause

**DotNext's `JoinCluster()` does NOT read a `members` array from configuration.**

The HTTP cluster uses **persistent storage** (`IClusterConfigurationStorage`) for cluster membership, not the ASP.NET Core configuration system. The `members` key in appsettings.json is simply ignored.

When a node starts with `coldStart=false` and no members in persistent storage, it enters **Standby mode** - waiting to be added to an existing cluster via `AddMemberAsync()`.

## Solution

Use `UseInMemoryConfigurationStorage()` to pre-populate the cluster members from configuration:

```csharp
// In Program.cs, BEFORE calling JoinCluster()

var membersConfig = builder.Configuration.GetSection("members").Get<string[]>() ?? [];
if (membersConfig.Length > 0)
{
    builder.Services.UseInMemoryConfigurationStorage(members =>
    {
        foreach (var member in membersConfig)
        {
            if (Uri.TryCreate(member, UriKind.Absolute, out var uri))
            {
                members.Add(new UriEndPoint(uri));
            }
        }
    });
}

builder.JoinCluster();
```

Required using statement:
```csharp
using Microsoft.AspNetCore.Connections;
```

## Key Insights

1. **`JoinCluster()` uses persistent storage, not config** - The default behavior reads cluster membership from disk, not from `appsettings.json`

2. **Standby mode = no members configured** - When `coldStart=false` and no members exist in storage, the node waits to be added externally

3. **Pre-populate for parallel startup** - With `UseInMemoryConfigurationStorage`, all nodes know about each other at startup and can elect a leader without needing `coldStart=true` on any node

4. **Quorum required for leader election** - For a 3-node cluster, at least 2 nodes must be running before a leader can be elected

## Configuration Files

Node-specific configuration files (`appsettings.node1.json`, etc.):

```json
{
  "publicEndPoint": "http://127.0.0.1:6000/",
  "coldStart": false,
  "standby": false,
  "lowerElectionTimeout": 150,
  "upperElectionTimeout": 300,
  "members": [
    "http://127.0.0.1:6000/",
    "http://127.0.0.1:6001/",
    "http://127.0.0.1:6002/"
  ]
}
```

Load with `ASPNETCORE_ENVIRONMENT=node1` to pick up `appsettings.node1.json`.

## Running the Cluster

```bash
# Terminal 1
ASPNETCORE_ENVIRONMENT=node1 dotnet run --no-launch-profile --urls "http://127.0.0.1:6000"

# Terminal 2
ASPNETCORE_ENVIRONMENT=node2 dotnet run --no-launch-profile --urls "http://127.0.0.1:6001"

# Terminal 3
ASPNETCORE_ENVIRONMENT=node3 dotnet run --no-launch-profile --urls "http://127.0.0.1:6002"
```

## Additional Issue: Log Entry Term

### Problem

`ReplicateAsync` hangs or times out even though the cluster has a leader and quorum.

### Cause

The `IRaftLogEntry.Term` property was hardcoded to `0` instead of using the cluster's current term.

### Fix

Pass the cluster's current term when creating log entries:

```csharp
// Wrong - term hardcoded to 0
public long Term => 0;

// Correct - use cluster's current term
var entry = new BinaryLogEntry(bytes, _cluster.Term);

public BinaryLogEntry(byte[] data, long term)
{
    _data = data;
    _term = term;
}

public long Term => _term;
```

## Log Spam and Tuning

### Heartbeat Spam

At `Debug` level, DotNext logs every heartbeat (~150-300ms):
```
Member is downgraded to follower state with term 1
Incoming configuration with fingerprint from leader
Election timeout is refreshed
```

**Fix:** Set `DotNext` log level to `Information` or `Warning`.

### Connection Refused Spam

When starting nodes sequentially, the first node spams connection errors for unreachable nodes:
```
Cluster member http://127.0.0.1:6002/ is unavailable
System.Net.Http.HttpRequestException: Connection refused
```

**Fix:** Set `DotNext.Net.Cluster.Consensus.Raft.Http.RaftHttpCluster` to `Error` level:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "DotNext": "Warning",
        "DotNext.Net.Cluster.Consensus.Raft.Http.RaftHttpCluster": "Error"
      }
    }
  }
}
```

## Replication Timeout

Add a timeout to `ReplicateAsync` to prevent indefinite hangs:

```csharp
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

var result = await _cluster.ReplicateAsync(entry, timeoutCts.Token);
```

## Health Check Endpoints

Use `/health/ready` to check cluster status:

```bash
curl -s http://localhost:6000/health/ready | jq
```

Response includes:
- `status`: "ready" or "not_ready"
- `cluster.role`: "leader" or "follower"
- `cluster.leaderKnown`: whether a leader exists
- `cluster.leader`: leader's endpoint URL
- `cluster.term`: current Raft term

## Network Configuration Tips

1. **Use `127.0.0.1` not `localhost`** - Explicit loopback IP avoids DNS resolution issues
2. **Trailing slashes matter** - Use `http://127.0.0.1:6000/` consistently
3. **All nodes need same member list** - Every node's config should list all cluster members

## Summary of DotNext Gotchas

| Issue | Symptom | Fix |
|-------|---------|-----|
| Members not discovered | Standby state | `UseInMemoryConfigurationStorage` |
| Replication hangs | Timeout/no response | Set correct `Term` on log entry |
| Log spam (heartbeats) | Console flooded | Set DotNext to Warning level |
| Log spam (connections) | Exception traces | Set RaftHttpCluster to Error |
| Writes hang forever | No timeout | Add `CancelAfter(10s)` to replication |

## References

- [DotNext Raft Documentation](https://dotnet.github.io/dotNext/features/cluster/raft.html)
- [DotNext GitHub - Seed Members Discussion](https://github.com/dotnet/dotNext/discussions/62)
- `src/Confman.Api/Program.cs:57-70` - Cluster configuration
- `src/Confman.Api/Cluster/RaftService.cs:85-90` - Log entry creation
- `src/Confman.Api/appsettings.json` - Log level configuration
