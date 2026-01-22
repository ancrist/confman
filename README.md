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

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/v1/config/{namespace}/{key}` | Read config value |
| PUT | `/api/v1/config/{namespace}/{key}` | Write config value |
| DELETE | `/api/v1/config/{namespace}/{key}` | Delete config |
| GET | `/api/v1/namespaces/{namespace}/keys` | List keys in namespace |
| GET | `/health` | Liveness check |
| GET | `/health/ready` | Readiness check (includes quorum status) |

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

```json
{
  "Cluster": {
    "NodeId": "node1",
    "Nodes": [
      { "Id": "node1", "Address": "10.0.1.10", "ApiPort": 5000, "RaftPort": 5001 },
      { "Id": "node2", "Address": "10.0.1.11", "ApiPort": 5000, "RaftPort": 5001 },
      { "Id": "node3", "Address": "10.0.1.12", "ApiPort": 5000, "RaftPort": 5001 }
    ]
  },
  "Auth": {
    "ApiKeys": [
      {
        "Key": "confman_prod_abc123",
        "Name": "Production Service",
        "Role": "admin"
      }
    ]
  }
}
```

## Technology Stack

- **.NET 10** — Runtime
- **DotNext.AspNetCore.Cluster** — Raft consensus
- **LiteDB** — Embedded document storage
- **ASP.NET Core** — REST API

## Documentation

- [Implementation Plan](docs/plans/2026-01-23-feat-confman-distributed-config-service-plan.md)
- [System Architecture](docs/brainstorms/2026-01-23-system-architecture-brainstorm.md)
- [Data Model](docs/brainstorms/2026-01-23-data-model-brainstorm.md)
- [API Design](docs/brainstorms/2026-01-23-api-design-brainstorm.md)

## License

MIT
