# Confman - Implementation Notes

## Overview

Distributed Configuration Service built on Raft consensus, implemented in .NET Core.

**Core Value Proposition:** A governed, hierarchical key-value store that serves as the single source of truth for configuration data with unified authorship and publishing workflows.

---

## Data Model

### Core Entities

| Entity | Purpose |
|--------|---------|
| **Namespace** | Logical grouping with ownership, RBAC, and schema bindings |
| **ConfigEntry** | Key + value + metadata (version, author, timestamps) |
| **Schema** | JSON Schema defining valid structure for values in a namespace |
| **Principal** | User or service identity |
| **Role** | Permission set (viewer, editor, publisher, admin) |
| **AuditEvent** | Immutable record of every action |
| **PublishRequest** | Workflow item for change approval |

### Hierarchical Key Structure

```
/                           # Root
├── environments/
│   ├── prod/
│   │   ├── us-east/
│   │   │   └── api-gateway/
│   │   │       ├── rate-limit → 1000
│   │   │       └── timeout-ms → 5000
│   │   └── eu-west/
│   └── staging/
├── teams/
│   └── payments/
│       └── stripe/
│           ├── api-version → "2024-01-01"
│           └── webhook-secret → [encrypted]
└── schemas/                # Meta-namespace for schemas
    └── payments/
        └── stripe.schema.json
```

### Publishing Workflow States

```
┌─────────┐     ┌──────────┐     ┌───────────┐     ┌───────────┐
│  Draft  │ ──► │ Pending  │ ──► │ Approved  │ ──► │ Published │
└─────────┘     │  Review  │     └───────────┘     └───────────┘
                └──────────┘            │
                     │                  ▼
                     │            ┌──────────┐
                     └─────────►  │ Rejected │
                                  └──────────┘
```

- **Draft**: Author is editing, not visible to consumers
- **Pending Review**: Submitted for approval, triggers notifications
- **Approved**: Passed review, ready to publish
- **Published**: Live and served to clients
- **Rejected**: Sent back with feedback

### Access Control Model

```
Namespace: /teams/payments/stripe
├── Owner: payments-team@company.com
├── Roles:
│   ├── admin:     [payments-leads]     # Full control, manage roles
│   ├── publisher: [payments-oncall]    # Approve and publish
│   ├── editor:    [payments-devs]      # Create/edit drafts
│   └── viewer:    [all-engineers]      # Read published values
└── Inherit: true  # Inherit parent namespace permissions
```

**Permission Matrix:**

| Action | Viewer | Editor | Publisher | Admin |
|--------|--------|--------|-----------|-------|
| Read published | ✓ | ✓ | ✓ | ✓ |
| Read drafts | | ✓ | ✓ | ✓ |
| Create/edit draft | | ✓ | ✓ | ✓ |
| Submit for review | | ✓ | ✓ | ✓ |
| Approve/reject | | | ✓ | ✓ |
| Publish | | | ✓ | ✓ |
| Rollback | | | ✓ | ✓ |
| Manage roles | | | | ✓ |
| Delete namespace | | | | ✓ |

### Schema & Validation

Schemas use JSON Schema with custom extensions:

```json
{
  "$id": "/schemas/payments/stripe",
  "type": "object",
  "properties": {
    "api-version": {
      "type": "string",
      "pattern": "^\\d{4}-\\d{2}-\\d{2}$"
    },
    "webhook-secret": {
      "type": "string",
      "x-confman-encrypted": true,
      "x-confman-rotation-days": 90
    }
  },
  "required": ["api-version"]
}
```

**Custom Extensions (`x-confman-*`):**
- `x-confman-encrypted` — Value stored encrypted at rest
- `x-confman-rotation-days` — Warn if not rotated within N days
- `x-confman-deprecated` — Warn on read, block new writes
- `x-confman-allowed-values` — Enum with descriptions

### Audit Event Structure

```json
{
  "id": "evt_abc123",
  "timestamp": "2024-01-15T10:30:00Z",
  "raftTerm": 5,
  "raftIndex": 1234,
  "action": "config.updated",
  "actor": {
    "type": "user",
    "id": "user@company.com",
    "ip": "10.0.1.50"
  },
  "resource": {
    "namespace": "/teams/payments/stripe",
    "key": "rate-limit"
  },
  "change": {
    "previousValue": 500,
    "newValue": 1000,
    "previousVersion": 12,
    "newVersion": 13
  },
  "workflow": {
    "requestId": "pr_xyz789",
    "state": "published"
  }
}
```

### Entity Relationships

```
┌─────────────┐       ┌─────────────┐
│  Principal  │◄──────│    Role     │
│  (User/Svc) │       │  Assignment │
└─────────────┘       └──────┬──────┘
                             │
                             ▼
┌─────────────┐       ┌─────────────┐       ┌─────────────┐
│   Schema    │◄──────│  Namespace  │──────►│ ConfigEntry │
└─────────────┘       └──────┬──────┘       └──────┬──────┘
                             │                     │
                             │                     ▼
                             │              ┌─────────────┐
                             └─────────────►│ AuditEvent  │
                                            └─────────────┘
                                                   ▲
                                                   │
                                            ┌──────┴──────┐
                                            │  Publish    │
                                            │  Request    │
                                            └─────────────┘
```

## Technology Stack

- **Runtime:** .NET 10+
- **API:** ASP.NET Core (REST for clients, gRPC for inter-node)
- **Consensus:** Raft via DotNext.Net.Cluster
- **Storage:** RocksDB or LiteDB for persistent state

---

## Distributed Systems Considerations

### 1. Networking & RPC

| Consideration | Recommendation |
|---------------|----------------|
| Inter-node communication | gRPC for Raft node-to-node (efficient, streaming support) |
| Client API | REST (ASP.NET Core) for external clients, gRPC for internal |
| Service discovery | Consul, etcd, or Kubernetes DNS for node membership |

**Libraries:**
- `Grpc.AspNetCore` — gRPC server/client
- `Microsoft.AspNetCore.Mvc` — REST controllers
- `Steeltoe.Discovery` — Service discovery integration

### 2. Consensus & Coordination

| Option | Notes |
|--------|-------|
| Roll your own Raft | Educational, but complex (leader election, log compaction, snapshots) |
| DotNext.Net.Cluster | Production-ready Raft implementation for .NET |
| Orleans | Virtual actor model with built-in clustering (different paradigm) |

**Libraries:**
- `DotNext.Net.Cluster` — Raft consensus, log replication, snapshotting
- `DotNext.IO` — Persistent log storage
- `Akka.NET` — Actor model with clustering (alternative to Raft)

### 3. Storage & Persistence

| Layer | Options |
|-------|---------|
| Raft log | File-based (append-only), RocksDB, SQLite |
| State machine (KV) | In-memory + snapshots, LiteDB, RocksDB |
| Snapshots | Binary serialization to disk |

**Libraries:**
- `RocksDbSharp` — Embedded key-value store (used by Kafka, CockroachDB)
- `LiteDB` — Embedded NoSQL (simpler, .NET native)
- `MessagePack-CSharp` — Fast binary serialization for snapshots

### 4. Observability

| Aspect | Tools |
|--------|-------|
| Metrics | Prometheus (leader state, commit latency, quorum health) |
| Tracing | OpenTelemetry for distributed request tracing |
| Logging | Structured logging with correlation IDs |
| Health checks | ASP.NET Core health checks for load balancers |

**Libraries:**
- `OpenTelemetry.Extensions.Hosting` — Metrics, tracing, logging
- `prometheus-net.AspNetCore` — Prometheus metrics endpoint
- `Serilog` — Structured logging
- `Microsoft.Extensions.Diagnostics.HealthChecks` — Built-in health checks

### 5. Resilience & Fault Tolerance

| Pattern | Purpose |
|---------|---------|
| Retry with backoff | Transient failures in node communication |
| Circuit breaker | Prevent cascade failures |
| Timeout policies | Bound latency for client requests |
| Bulkhead isolation | Isolate shard failures |

**Libraries:**
- `Polly` — Resilience policies (retry, circuit breaker, timeout)
- `Microsoft.Extensions.Http.Resilience` — HttpClient resilience (.NET 8+)

### 6. Configuration & Secrets

| Concern | Approach |
|---------|----------|
| Node config | `appsettings.json` + environment variables |
| Secrets | Azure Key Vault, HashiCorp Vault, or k8s secrets |
| Cluster topology | Seed nodes in config, dynamic discovery via gossip |

**Libraries:**
- `Azure.Extensions.AspNetCore.Configuration.Secrets` — Key Vault integration
- `VaultSharp` — HashiCorp Vault client

### 7. Testing

| Type | Focus |
|------|-------|
| Unit | Log replication logic, state machine transitions |
| Integration | Multi-node cluster in Docker |
| Chaos/Fault injection | Network partitions, leader kills |

**Libraries:**
- `xUnit` or `NUnit` — Test framework
- `Testcontainers` — Spin up multi-node clusters in Docker
- `NBomber` — Load testing
- `Simmy` (Polly extension) — Chaos engineering / fault injection

---

## Project Structure

```
src/
├── Confman.Api/              # ASP.NET Core REST API
│   ├── Controllers/
│   │   ├── ConfigController.cs    # GET /config/{key}
│   │   └── AdminController.cs     # POST /rollback, cluster status
│   └── Program.cs
├── Confman.Raft/             # Consensus layer
│   ├── RaftNode.cs
│   ├── LogReplication.cs
│   └── LeaderElection.cs
├── Confman.Store/            # KV state machine + snapshots
│   ├── KeyValueStore.cs
│   ├── SnapshotManager.cs
│   └── SchemaValidator.cs
├── Confman.Core/             # Shared models, interfaces
│   ├── Models/
│   └── Interfaces/
└── Confman.Infrastructure/   # gRPC, persistence adapters
    ├── Grpc/
    └── Persistence/

tests/
├── Confman.UnitTests/
├── Confman.IntegrationTests/
└── Confman.ChaosTests/
```

---

## Key NuGet Packages

```xml
<!-- Core -->
<PackageReference Include="DotNext.Net.Cluster" Version="5.*" />
<PackageReference Include="Grpc.AspNetCore" Version="2.*" />

<!-- Storage -->
<PackageReference Include="RocksDbSharp" Version="8.*" />
<PackageReference Include="MessagePack" Version="2.*" />

<!-- Resilience -->
<PackageReference Include="Polly.Extensions" Version="8.*" />

<!-- Observability -->
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.*" />
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
<PackageReference Include="prometheus-net.AspNetCore" Version="8.*" />

<!-- Testing -->
<PackageReference Include="Testcontainers" Version="3.*" />
<PackageReference Include="Simmy" Version="8.*" />
```

---

## Build & Run Commands

```bash
# Build
dotnet build

# Run tests
dotnet test

# Run API (development)
dotnet run --project src/Confman.Api

# Run with specific node config
CONFMAN_NODE_ID=node1 dotnet run --project src/Confman.Api
```

---

## API Endpoints (Planned)

### Config Operations
- `GET /api/config/{namespace}/{key}` — Read a config value
- `PUT /api/config/{namespace}/{key}` — Write a config value (leader only)
- `DELETE /api/config/{namespace}/{key}` — Delete a config value
- `GET /api/config/{namespace}` — List keys in namespace

### Admin Operations
- `GET /api/admin/status` — Cluster health and leader info
- `POST /api/admin/rollback` — Rollback to previous snapshot
- `GET /api/admin/audit/{namespace}` — Audit log for namespace

### Health
- `GET /health` — Liveness check
- `GET /health/ready` — Readiness check (includes quorum status)

---

## Design Decisions

### Decided
- [x] **Consensus:** DotNext.Net.Cluster (production-ready Raft)
- [x] **Architecture:** Single Raft cluster, all nodes identical
- [x] **Node discovery:** Static configuration (node addresses in appsettings.json)
- [x] **Data model:** Document-oriented (self-contained namespace/config documents)
- [x] **Schema format:** JSON Schema with `x-confman-*` extensions
- [x] **Schema validation:** On publish only (drafts can be invalid)
- [x] **Secrets:** External references (keyvault://, vault://), client resolves
- [x] **RBAC:** Fixed roles initially (Viewer, Editor, Publisher, Admin)
- [x] **Inheritance:** Enabled by default (RBAC + schemas cascade)
- [x] **Versioning:** Immutable history (every version retained)
- [x] **Auth:** Azure AD / OIDC
- [x] **Self-approval:** Never allowed (four-eyes principle)
- [x] **Approvers:** Configurable per namespace (1-N)
- [x] **Notifications:** Webhooks (Slack, Teams, custom)
- [x] **Batch publish:** Single key per request
- [x] **Rollback:** Configurable per namespace (instant or approval-required)
- [x] **API versioning:** URL path (`/api/v1/...`)
- [x] **Response formats:** Content negotiation (JSON, YAML, raw)
- [x] **Errors:** RFC 7807 Problem Details
- [x] **API auth:** Static API keys (`X-API-Key` header)
- [x] **Pagination:** Cursor-based
- [x] **Health checks:** `/health` (liveness) + `/health/ready` (readiness)

### Open
- [ ] Choose storage backend (RocksDB vs LiteDB) — needs performance testing
- [ ] Snapshot/rollback retention policy
- [ ] History chunking strategy (by time? by count?)
- [ ] Staged rollout targeting logic (deferred feature)
- [ ] mTLS for inter-node and client auth (future)
- [ ] Custom RBAC roles (future)

## Brainstorm Documents

- `docs/brainstorms/2026-01-23-system-architecture-brainstorm.md` — Single Raft cluster architecture
- `docs/brainstorms/2026-01-23-data-model-brainstorm.md` — Document-oriented data model
- `docs/brainstorms/2026-01-23-publishing-workflow-brainstorm.md` — Governed publishing workflow
- `docs/brainstorms/2026-01-23-api-design-brainstorm.md` — REST API design
