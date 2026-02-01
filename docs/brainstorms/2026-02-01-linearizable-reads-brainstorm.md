---
date: 2026-02-01
topic: linearizable-reads
---

# Linearizable Reads

## What We're Building

Add a read barrier to all API read operations so that every GET request returns data that reflects all committed writes up to the moment the request is processed. Currently, reads go directly to LiteDB on whichever node handles the request, which can return stale data on followers (or even on a deposed leader that hasn't detected a partition yet).

The barrier will use DotNext's `IRaftCluster.ApplyReadBarrierAsync()`:
- **On the leader**: triggers a heartbeat round to confirm majority acknowledgment
- **On followers**: contacts the leader to get the current commit index, then waits until the local state machine has applied up to that index

Failure behavior is configurable at runtime via `ReadBarrier:FailureMode` in `appsettings.json`.

## Why This Approach

Three approaches were considered:

| Approach | Description | Verdict |
|----------|-------------|---------|
| **ASP.NET Core Middleware** | `ReadBarrierMiddleware` intercepts all GET requests to `/api` paths before they reach controllers | **Chosen** — zero controller changes, automatic coverage of future endpoints |
| **IRaftService wrapper** | Add `EnsureLinearizableAsync()` to `IRaftService`, call explicitly in each controller action | Rejected — requires touching 6+ places, easy to forget on new endpoints |
| **IConfigStore decorator** | Wrap `IConfigStore` with a barrier-aware decorator | Rejected — couples Raft to storage, would also fire during snapshot creation |

The middleware approach was chosen because:
1. Confman is a "single source of truth" — strong consistency everywhere is the right default
2. Zero controller changes means nothing can slip through
3. The configurable failure mode gives operational flexibility without code changes

## Key Decisions

- **Consistency level**: Strong (linearizable) for all reads on all nodes
- **Read routing**: Followers serve reads after applying a read barrier (distributes load)
- **Barrier scope**: GET requests to `/api` paths only. Health, Swagger, and Raft protocol paths are excluded.
- **Write-path reads**: No barrier. Pre-write existence checks on the leader are low-risk because `ReplicateAsync` will fail if leadership is lost. Avoids adding latency to writes.
- **Failure modes** (configurable via `ReadBarrier:FailureMode`):
  - `reject` (default) — Return 503 Service Unavailable
  - `stale` — Log warning, serve potentially stale data
  - `timeout` — Return 504 Gateway Timeout
- **Hot-reload**: Failure mode is read from `IConfiguration` per-request, so it can be changed at runtime by editing `appsettings.json`

## Affected Read Paths

| Endpoint | Store Method | Current Barrier |
|----------|-------------|-----------------|
| `GET /api/v1/namespaces/{ns}/config` | `ListAsync` | None |
| `GET /api/v1/namespaces/{ns}/config/{key}` | `GetAsync` | None |
| `GET /api/v1/namespaces` | `ListNamespacesAsync` | None |
| `GET /api/v1/namespaces/{ns}` | `GetNamespaceAsync` | None |
| `GET /api/v1/namespaces/{ns}/audit` | `GetAuditEventsAsync` | None |
| `GET /api/v1/configs` (dashboard) | `ListAllAsync` | None |

All six endpoints will be covered by the middleware.

## Open Questions

- Should we add a response header (e.g., `X-Confman-Consistency: linearizable`) to indicate the read was barrier-protected? Useful for debugging.
- Should there be a per-request opt-out mechanism (e.g., `?consistency=eventual` query parameter) for clients that prefer speed over consistency?
- Timeout duration for the read barrier — should it match `requestTimeout` from Raft config, or have its own config?

## Next Steps

-> `/workflows:plan` for implementation details
