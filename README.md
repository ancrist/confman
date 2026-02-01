# Confman

A distributed configuration management service built on Raft consensus.

## Overview

Confman provides a centralized, highly-available configuration store for distributed systems. It uses Raft consensus to ensure strong consistency for writes while allowing bounded-staleness reads from any node.

## Features

- **Distributed consensus** — Raft-based replication via DotNext.AspNetCore.Cluster
- **Hierarchical key-value store** — Organize configs by namespace
- **REST API** — Simple HTTP interface with JSON responses
- **API key authentication** — Static keys with role-based access (reader/admin)
- **Audit trail** — Track all configuration changes
- **Health endpoints** — Liveness and readiness probes for load balancers

## Architecture

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
   │ LiteDB  │   │ LiteDB  │   │ LiteDB  │
   └─────────┘   └─────────┘   └─────────┘
```

## API

### Health & Status

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/health` | None | Liveness check - returns `healthy` status |
| `GET` | `/health/ready` | None | Readiness check - includes cluster role, leader info, and Raft term |

### Namespaces

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `GET` | `/api/v1/namespaces` | ReadOnly | List all namespaces |
| `GET` | `/api/v1/namespaces/{namespace}` | ReadOnly | Get a specific namespace |
| `PUT` | `/api/v1/namespaces/{namespace}` | Admin | Create or update a namespace |
| `DELETE` | `/api/v1/namespaces/{namespace}` | Admin | Delete a namespace |

### Configuration Entries

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `GET` | `/api/v1/namespaces/{namespace}/config` | ReadOnly | List all config entries in a namespace |
| `GET` | `/api/v1/namespaces/{namespace}/config/{key}` | ReadOnly | Get a specific config entry |
| `PUT` | `/api/v1/namespaces/{namespace}/config/{key}` | Write | Create or update a config entry |
| `DELETE` | `/api/v1/namespaces/{namespace}/config/{key}` | Write | Delete a config entry |
| `GET` | `/api/v1/configs` | None | List all configs across namespaces (dashboard) |

### Audit Trail

| Method | Endpoint | Auth Policy | Description |
|--------|----------|-------------|-------------|
| `GET` | `/api/v1/namespaces/{namespace}/audit?limit=50` | ReadOnly | Get audit events for a namespace (max 1000) |

> **Note:** Write operations (`PUT`, `DELETE`) are leader-aware. If a request hits a follower node, it returns a `307 Temporary Redirect` to the current leader. Clients can connect to any node for reads, but writes must go to the leader. All error responses use [RFC 7807 Problem Details](https://tools.ietf.org/html/rfc7807) for consistent, machine-parseable error handling.

## Quick Start

```bash
# Clone the repository
git clone git@github.com:ancrist/confman.git
cd confman

# Build
dotnet build

# Run a single node (development)
dotnet run --project src/Confman.Api

# Run tests
dotnet test
```

## Configuration

### Base Configuration (`appsettings.json`)

```json
{
  "lowerElectionTimeout": 150,
  "upperElectionTimeout": 300,
  "requestTimeout": "00:00:05",
  "heartbeatThreshold": 0.5,
  "coldStart": false,
  "Storage": {
    "DataPath": "./data"
  },
  "Auth": {
    "ApiKeys": [
      {
        "Key": "confman_dev_abc123",
        "Name": "Development Admin",
        "Roles": ["admin"]
      },
      {
        "Key": "confman_dev_reader",
        "Name": "Development Reader",
        "Roles": ["reader"]
      }
    ]
  }
}
```

### Node Configuration (`appsettings.node1.json`)

Each node requires its own config file specifying its endpoint and cluster members:

```json
{
  "publicEndPoint": "http://127.0.0.1:6100/",
  "coldStart": false,
  "standby": false,
  "lowerElectionTimeout": 150,
  "upperElectionTimeout": 300,
  "members": [
    "http://127.0.0.1:6100/",
    "http://127.0.0.1:6200/",
    "http://127.0.0.1:6300/"
  ]
}
```

| Setting | Description |
|---------|-------------|
| `publicEndPoint` | This node's advertised address for cluster communication |
| `members` | Static list of all cluster member endpoints |
| `coldStart` | Must be `false` when members are pre-configured. Only use `true` for dynamic membership without a `members` list |
| `lowerElectionTimeout` / `upperElectionTimeout` | Raft election timeout range in milliseconds |

### Running a 3-Node Cluster

All nodes use `coldStart: false` with pre-configured members. Start at least 2 nodes to form a quorum and elect a leader.

```bash
# Terminal 1 - Node 1 (port 6100)
ASPNETCORE_ENVIRONMENT=node1 dotnet run --no-launch-profile --project src/Confman.Api \
  --urls http://127.0.0.1:6100

# Terminal 2 - Node 2 (port 6200)
ASPNETCORE_ENVIRONMENT=node2 dotnet run --no-launch-profile --project src/Confman.Api \
  --urls http://127.0.0.1:6200

# Terminal 3 - Node 3 (port 6300)
ASPNETCORE_ENVIRONMENT=node3 dotnet run --no-launch-profile --project src/Confman.Api \
  --urls http://127.0.0.1:6300
```

> **Note:** Do NOT pass `--coldStart true` on the command line. With pre-configured `members`, cold start is unnecessary and causes leader tracking failures after re-election. See [DotNext Raft docs](https://dotnet.github.io/dotNext/features/cluster/raft.html).

## Dashboard

Confman includes a real-time web dashboard for monitoring cluster health and viewing stored configurations.

### Features

- **Cluster Health Overview** — Shows overall cluster status (Healthy/Degraded/No Quorum)
- **Node Status Cards** — Displays each node's role (Leader/Follower), Raft term, and connectivity
- **Configuration Browser** — Lists all stored key-value pairs across namespaces
- **Auto-refresh** — Updates every 2 seconds (toggleable)

### Running the Dashboard

```bash
cd src/Confman.Dashboard
npm install
npm run dev
```

The dashboard runs on `http://localhost:5173` and connects to the cluster nodes at ports 6100, 6200, and 6300.

### Screenshot

```
┌─────────────────────────────────────────────┐
│           Confman Cluster                   │
│        Raft Consensus Dashboard             │
├─────────────────────────────────────────────┤
│              ● HEALTHY                      │
│    3/3 nodes online • Leader: 127.0.0.1:6100│
├─────────────────────────────────────────────┤
│  ┌─────────┐  ┌─────────┐  ┌─────────┐     │
│  │ Node 1  │  │ Node 2  │  │ Node 3  │     │
│  │ LEADER  │  │FOLLOWER │  │FOLLOWER │     │
│  │ Term: 5 │  │ Term: 5 │  │ Term: 5 │     │
│  └─────────┘  └─────────┘  └─────────┘     │
├─────────────────────────────────────────────┤
│         Stored Configurations               │
│  app/database-url  │  postgres://...        │
│  app/cache-ttl     │  3600                  │
└─────────────────────────────────────────────┘
```

## Technology Stack

- **.NET 10** — Runtime
- **DotNext.AspNetCore.Cluster** — Raft consensus
- **LiteDB** — Embedded document storage
- **ASP.NET Core** — REST API

## Documentation

- [Feature Status](FEATURES.md) — Complete list of working, partial, and planned features
- [Cluster Behavior](CLUSTER_NOTES.md) — Raft consensus behavior, failover scenarios, and recovery
- [FAQ](FAQ.md) — Why Raft, persistence model, and architecture decisions
- [Implementation Plan](docs/plans/2026-01-23-feat-confman-distributed-config-service-plan.md)
- [System Architecture](docs/brainstorms/2026-01-23-system-architecture-brainstorm.md)
- [Data Model](docs/brainstorms/2026-01-23-data-model-brainstorm.md)
- [API Design](docs/brainstorms/2026-01-23-api-design-brainstorm.md)

## License

MIT
