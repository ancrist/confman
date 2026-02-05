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
- **API:** ASP.NET Core REST (clients and inter-node via HTTP)
- **Consensus:** Raft via DotNext.AspNetCore.Cluster (local project reference)
- **Storage:** LiteDB for persistent state

---

## Distributed Systems Considerations

### 1. Networking & RPC

| Consideration | Implementation |
|---------------|----------------|
| Inter-node communication | HTTP via DotNext.AspNetCore.Cluster (Raft over HTTP) |
| Client API | REST (ASP.NET Core) for external clients |
| Service discovery | Static configuration (node addresses in appsettings.json) |

**Libraries:**
- `DotNext.AspNetCore.Cluster` — Raft over HTTP transport
- `Microsoft.AspNetCore.Mvc` — REST controllers

### 2. Consensus & Coordination

| Choice | Notes |
|--------|-------|
| DotNext.AspNetCore.Cluster | Production-ready Raft implementation for .NET (chosen) |

**Libraries:**
- `DotNext.AspNetCore.Cluster` — Raft consensus, log replication, snapshotting (local project reference for debugging)

### 3. Storage & Persistence

| Layer | Implementation |
|-------|----------------|
| Raft log | WAL (DotNext persistent log on disk) |
| State machine (KV) | LiteDB (embedded NoSQL) |
| Snapshots | JSON serialization to disk |

**Libraries:**
- `LiteDB` — Embedded NoSQL (Version 5.0.21)

### 4. Observability

| Aspect | Implementation |
|--------|----------------|
| Logging | Structured logging with Serilog + correlation IDs |
| Health checks | ASP.NET Core health checks for load balancers |

**Libraries:**
- `Serilog.AspNetCore` — Structured logging (Version 10.0.0)
- Built-in ASP.NET Core health checks

### 5. Configuration & Secrets

| Concern | Implementation |
|---------|----------------|
| Node config | `appsettings.json` + `appsettings.{node}.json` per node |
| Cluster topology | Static member list in per-node config |

### 6. Testing

| Type | Implementation |
|------|----------------|
| Unit | xUnit + NSubstitute |
| Integration | Microsoft.AspNetCore.Mvc.Testing |

**Libraries:**
- `xunit` Version 2.9.3
- `NSubstitute` Version 5.3.0
- `Microsoft.AspNetCore.Mvc.Testing` Version 10.0.2
- `coverlet.collector` Version 6.0.4

---

## Project Structure

```
src/
├── Confman.Api/                   # ASP.NET Core REST API (monolith)
│   ├── Auth/
│   │   ├── ApiKeyAuthenticationHandler.cs
│   │   └── NamespaceAuthorizationHandler.cs
│   ├── Cluster/
│   │   ├── Commands/              # Raft command types
│   │   │   ├── ICommand.cs
│   │   │   ├── SetConfigCommand.cs
│   │   │   ├── DeleteConfigCommand.cs
│   │   │   ├── SetNamespaceCommand.cs
│   │   │   ├── DeleteNamespaceCommand.cs
│   │   │   └── AuditIdGenerator.cs
│   │   ├── ClusterLifetime.cs
│   │   ├── ConfigStateMachine.cs
│   │   ├── RaftService.cs
│   │   └── SnapshotData.cs
│   ├── Controllers/
│   │   ├── AuditController.cs
│   │   ├── ConfigController.cs
│   │   └── NamespacesController.cs
│   ├── Middleware/
│   │   ├── CorrelationIdMiddleware.cs
│   │   └── ReadBarrierMiddleware.cs
│   ├── Models/
│   │   ├── AuditAction.cs
│   │   ├── AuditEvent.cs
│   │   ├── ConfigEntry.cs
│   │   └── Namespace.cs
│   ├── Storage/
│   │   ├── IConfigStore.cs
│   │   └── LiteDbConfigStore.cs
│   └── Program.cs
└── Confman.Dashboard/             # Vite + vanilla JS dashboard

tests/
└── Confman.Tests/                 # xUnit + NSubstitute
```

---

## Key Dependencies

```xml
<!-- Confman.Api -->
<!-- DotNext.AspNetCore.Cluster — local project reference for debugging WAL behavior -->
<ProjectReference Include=".../DotNext.AspNetCore.Cluster.csproj" />
<PackageReference Include="LiteDB" Version="5.0.21" />
<PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="8.0.0" />
<PackageReference Include="Workleap.DotNet.CodingStandards" Version="1.1.47" />

<!-- Confman.Tests -->
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="NSubstitute" Version="5.3.0" />
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.2" />
<PackageReference Include="coverlet.collector" Version="6.0.4" />
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

## API Endpoints

### Config Operations (v1)
- `GET /api/v1/namespaces/{namespace}/config` — List all configs in namespace
- `GET /api/v1/namespaces/{namespace}/config/{key}` — Read a config value
- `PUT /api/v1/namespaces/{namespace}/config/{key}` — Create/update config (redirects to leader)
- `DELETE /api/v1/namespaces/{namespace}/config/{key}` — Delete config (redirects to leader)

### Namespace Operations (v1)
- `GET /api/v1/namespaces` — List all namespaces
- `GET /api/v1/namespaces/{namespace}` — Get namespace details
- `PUT /api/v1/namespaces/{namespace}` — Create/update namespace (redirects to leader)
- `DELETE /api/v1/namespaces/{namespace}` — Delete namespace (redirects to leader)

### Audit Operations (v1)
- `GET /api/v1/namespaces/{namespace}/audit?limit=N` — Audit log for namespace

### Dashboard/Utility
- `GET /api/v1/configs` — List ALL configs across all namespaces (used by dashboard)

### Health
- `GET /health` — Liveness check
- `GET /health/ready` — Readiness check (includes quorum status)

### Documentation
- `GET /swagger` — Swagger UI (OpenAPI documentation)

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
- [x] **Storage backend:** LiteDB (embedded NoSQL, .NET native)
- [x] **Inter-node transport:** HTTP via DotNext (not gRPC)

### Open
- [ ] Snapshot/rollback retention policy
- [ ] History chunking strategy (by time? by count?)
- [ ] Staged rollout targeting logic (deferred feature)
- [ ] mTLS for inter-node and client auth (future)
- [ ] Custom RBAC roles (future)

## Known Issues

### GitHub Issues
- ~~**#2** Audit events not replicated across cluster nodes~~ — **FIXED**: Audit events now created on all nodes with idempotent upsert
- ~~**#3** Snapshots don't include full state~~ — **FIXED**: Snapshots now serialize all configs, namespaces, and audit events

### Architecture Notes
- **Audit events**: Created on ALL nodes during state machine apply. Uses deterministic ID (timestamp+namespace+key) with upsert for idempotency during log replay.
- **Snapshots**: `PersistAsync` serializes full state (configs, namespaces, audit) to JSON. `RestoreAsync` clears and restores all data.
- **Leader forwarding**: Write operations (PUT/DELETE) return HTTP 307 redirect to leader. Clients must follow redirects.
- **Dashboard polling**: 2-second interval via `setInterval(refresh, 2000)`. No real-time push (SSE/WebSocket). Dashboard now queries any single node (all have consistent audit data).

---

## Brainstorm Documents

- `docs/brainstorms/2026-01-23-system-architecture-brainstorm.md` — Single Raft cluster architecture
- `docs/brainstorms/2026-01-23-data-model-brainstorm.md` — Document-oriented data model
- `docs/brainstorms/2026-01-23-publishing-workflow-brainstorm.md` — Governed publishing workflow
- `docs/brainstorms/2026-01-23-api-design-brainstorm.md` — REST API design
- `docs/brainstorms/2026-02-01-linearizable-reads-brainstorm.md` — Linearizable reads / version tokens
- `docs/brainstorms/2026-02-02-cluster-benchmark-brainstorm.md` — Cluster benchmarking strategy
