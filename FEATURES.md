# Confman Features

> Current implementation status of the distributed configuration management service.

---

## Feature Status Legend

| Status | Meaning |
|--------|---------|
| ✅ | Fully implemented and tested |
| 🟡 | Core functionality works, some aspects pending |
| 🔴 | Designed but not yet implemented |

---

## Core Features

### Distributed Consensus

| Feature | Status | Description |
|---------|--------|-------------|
| Raft Leader Election | ✅ | Automatic leader election using DotNext.Net.Cluster with configurable election timeouts (150-300ms) |
| Log Replication | ✅ | Commands are replicated across all nodes before being committed |
| Write-Ahead Log (WAL) | ✅ | Persistent Raft log stored on disk for crash recovery |
| Automatic Snapshots | ✅ | Snapshots created every 100 entries for log compaction |
| Leader Forwarding | ✅ | Write requests to followers return `307 Redirect` to the current leader |
| Cluster Health Checks | ✅ | `/health/ready` endpoint reports quorum status, leader info, and Raft term |

### Configuration Management

| Feature | Status | Description |
|---------|--------|-------------|
| Hierarchical Namespaces | ✅ | Organize configs under logical namespaces (e.g., `prod/api-gateway`) |
| Key-Value Storage | ✅ | Store typed configuration values with versioning |
| CRUD Operations | ✅ | Full create, read, update, delete via REST API |
| Automatic Versioning | ✅ | Each config entry tracks version number, updated timestamp, and author |
| Cross-Namespace Listing | ✅ | Dashboard endpoint lists all configs across namespaces |

### Persistence

| Feature | Status | Description |
|---------|--------|-------------|
| LiteDB Storage | ✅ | Embedded NoSQL database for config entries, namespaces, and audit events |
| Per-Node Data Isolation | ✅ | Each node stores data in a port-specific directory (e.g., `./data-6100`) |
| State Recovery | ✅ | WAL replay on startup restores state machine to last committed index |

### Authentication & Authorization

| Feature | Status | Description |
|---------|--------|-------------|
| API Key Authentication | ✅ | `X-Api-Key` header validation with configurable keys |
| Role-Based Access Control | ✅ | Three roles: `admin`, `write`, `read` with hierarchical permissions |
| Namespace Scoping | ✅ | API keys can be restricted to specific namespaces or wildcards (`*`) |
| Policy-Based Authorization | ✅ | Separate policies for Admin, Write, and ReadOnly operations |

### Audit & Observability

| Feature | Status | Description |
|---------|--------|-------------|
| Audit Trail | ✅ | Every config change logged with timestamp, actor, action, and before/after values |
| Audit Query API | ✅ | Retrieve audit events per namespace with configurable limit (max 1000) |
| Correlation IDs | ✅ | `X-Correlation-Id` header propagated through requests for distributed tracing |
| Structured Logging | ✅ | Serilog with enriched context (CorrelationId, Application, SourceContext) |

### API & Developer Experience

| Feature | Status | Description |
|---------|--------|-------------|
| REST API v1 | ✅ | Full CRUD for namespaces, configs, and audit events |
| Swagger/OpenAPI | ✅ | Interactive API documentation at `/swagger` in development mode |
| RFC 7807 Problem Details | ✅ | Standardized error responses with `type`, `title`, `detail`, `status` |
| CORS Support | ✅ | Configurable CORS for dashboard and external clients |
| JSON Serialization | ✅ | camelCase property naming with System.Text.Json |

### Dashboard

| Feature | Status | Description |
|---------|--------|-------------|
| Cluster Overview | ✅ | Real-time display of cluster health (Healthy/Degraded/No Quorum) |
| Node Status Cards | ✅ | Per-node view of role, Raft term, leader reference, and connectivity |
| Configuration Browser | ✅ | Lists all stored key-value pairs with namespace grouping |
| Auto-Refresh | ✅ | 2-second polling with toggle control |
| Offline Detection | ✅ | Visual indicators for unreachable nodes |

---

## Planned Features

These features are designed in the architecture documents but not yet implemented:

| Feature | Status | Priority | Notes |
|---------|--------|----------|-------|
| JSON Schema Validation | 🔴 | High | Validate config values against namespace-bound schemas |
| Publishing Workflow | 🔴 | High | Draft → Review → Approve → Publish states with approval gates |
| Encrypted Values | 🔴 | Medium | `x-confman-encrypted` schema extension for secrets |
| External Secret References | 🔴 | Medium | `keyvault://`, `vault://` URI schemes resolved by clients |
| Custom RBAC Roles | 🔴 | Low | User-defined roles beyond admin/write/read |
| mTLS Authentication | 🔴 | Low | Mutual TLS for inter-node and client auth |
| Staged Rollouts | 🔴 | Low | Gradual configuration deployment with targeting rules |

---

## Architecture Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Consensus Protocol | Raft via DotNext | Production-ready, well-tested .NET implementation |
| Storage Backend | LiteDB | Embedded, zero-config, .NET native; suitable for moderate scale |
| Cluster Topology | Single Raft cluster | Simplicity; all nodes participate in consensus |
| Node Discovery | Static configuration | Predictable for development; production may use dynamic discovery |
| API Transport | HTTP/REST | Universal client compatibility; gRPC reserved for inter-node |
| State Machine | WAL + LiteDB hybrid | WAL for Raft log, LiteDB for queryable config state |

---

## Performance Characteristics

| Metric | Typical Value | Notes |
|--------|---------------|-------|
| Write Latency | ~10-50ms | Depends on quorum round-trip; 10s timeout configured |
| Read Latency | <5ms | Direct LiteDB query, no consensus required |
| Election Timeout | 150-300ms | Randomized to prevent split votes |
| Heartbeat Threshold | 0.5 | Leader sends heartbeats at half the election timeout |
| Snapshot Interval | Every 100 entries | Balances log size vs. snapshot overhead |

---

## References

- [Implementation Plan](docs/plans/2026-01-23-feat-confman-distributed-config-service-plan.md)
- [System Architecture](docs/brainstorms/2026-01-23-system-architecture-brainstorm.md)
- [Data Model](docs/brainstorms/2026-01-23-data-model-brainstorm.md)
- [API Design](docs/brainstorms/2026-01-23-api-design-brainstorm.md)
