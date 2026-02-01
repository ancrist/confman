---
title: "feat: Add linearizable reads via ReadBarrierMiddleware"
type: feat
date: 2026-02-01
brainstorm: docs/brainstorms/2026-02-01-linearizable-reads-brainstorm.md
---

# feat: Add linearizable reads via ReadBarrierMiddleware

## Overview

Add an ASP.NET Core middleware that calls `IRaftCluster.ApplyReadBarrierAsync()` before any GET request to `/api` paths reaches a controller. This guarantees every read returns data reflecting all committed writes — making the system a true "single source of truth."

Currently all 12 store reads bypass Raft entirely and go straight to LiteDB, meaning followers (or deposed leaders) can serve stale data.

## Problem Statement

Confman claims to be the single source of truth for configuration, but reads have no consistency guarantee:

- **Stale follower**: client reads from a follower that hasn't applied recent commits
- **Stale leader**: a partitioned leader serves reads during the detection window
- **Read-after-write**: client writes via leader (307 redirect), reads from follower before replication

For a config system where clients make operational decisions based on values (rate limits, feature flags, timeouts), stale reads are a correctness risk.

## Proposed Solution

A `ReadBarrierMiddleware` placed **after authentication/authorization** in the pipeline. On every GET request to `/api/*`:

1. Create a linked `CancellationToken` from `HttpContext.RequestAborted` + a configurable timeout
2. Call `IRaftCluster.ApplyReadBarrierAsync(token)`
   - **On leader**: forces a heartbeat round to confirm majority acknowledgment
   - **On follower**: contacts leader to get commit index, waits for local state machine to catch up
3. On success: request proceeds to controller with linearizable data
4. On failure: behavior depends on `ReadBarrier:FailureMode` config

## Technical Considerations

### Pipeline Placement

```
UseConsensusProtocolHandler()   ← Raft protocol (must be first)
UseCorrelationId()              ← Correlation ID for tracing
UseCors("Dashboard")            ← CORS
UseSwagger()                    ← API docs
UseAuthentication()             ← Auth
UseAuthorization()              ← AuthZ
UseReadBarrier()                ← NEW — after auth, before endpoints
MapControllers() / endpoints    ← Controllers
```

Placing the barrier **after auth** prevents unauthenticated requests from triggering Raft round-trips (DoS mitigation). The anonymous dashboard endpoint `GET /api/v1/configs` is registered before `MapControllers()` and runs before the barrier middleware would intercept it in the pipeline — it needs to be handled explicitly (see Implementation Phase 2).

### Exception Handling Matrix

| Exception | Client disconnected? | FailureMode=reject | FailureMode=stale | FailureMode=timeout |
|-----------|---------------------|--------------------|--------------------|---------------------|
| `OperationCanceledException` | Yes (`RequestAborted`) | No response (client gone) | No response | No response |
| `OperationCanceledException` | No (barrier timeout) | 503 | Warn + proceed | 504 |
| `QuorumUnreachableException` | N/A | 503 | Warn + proceed | 504 |
| Any other exception | N/A | 503 | Warn + proceed | 504 |
| Invalid FailureMode value | N/A | 503 (treat as reject) | — | — |

### Configuration Schema

```json
{
  "ReadBarrier": {
    "Enabled": true,
    "FailureMode": "reject",
    "TimeoutMs": 5000
  }
}
```

- `Enabled` (default: `true`) — kill switch to disable without removing middleware
- `FailureMode` (default: `"reject"`) — `reject` | `stale` | `timeout`
- `TimeoutMs` (default: `5000`) — dedicated barrier timeout, linked with `RequestAborted`

All values are read from `IConfiguration` per-request for hot-reload support.

### Response Format

Barrier failures return RFC 7807 ProblemDetails (consistent with all other error responses):

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.4",
  "title": "Read barrier failed",
  "status": 503,
  "detail": "Cluster quorum unreachable. Configured failure mode: reject.",
  "extensions": {
    "traceId": "correlation-id-here",
    "retryAfter": 2
  }
}
```

Include `Retry-After: 2` header (matching upper election timeout of 2000ms).

### HTTP Methods

- **GET**: barrier applied
- **HEAD**: barrier applied (semantically a read)
- **PUT, POST, DELETE, OPTIONS, PATCH**: no barrier (writes go through Raft replication, OPTIONS is CORS preflight)

## Acceptance Criteria

- [x] All GET and HEAD requests to `/api/*` paths pass through a read barrier before reaching controllers
- [x] Non-GET/HEAD requests pass through without barrier
- [x] Non-`/api` paths (health, swagger, Raft protocol) pass through without barrier
- [x] `ReadBarrier:FailureMode` = `reject` returns 503 ProblemDetails on barrier failure
- [x] `ReadBarrier:FailureMode` = `stale` logs warning and serves (possibly stale) data
- [x] `ReadBarrier:FailureMode` = `timeout` returns 504 ProblemDetails on barrier failure
- [x] `ReadBarrier:TimeoutMs` controls barrier timeout independently of request timeout
- [x] `ReadBarrier:Enabled` = `false` disables barrier entirely
- [x] Configuration is hot-reloadable (no restart needed)
- [x] Unrecognized FailureMode values fall back to `reject` with a warning log
- [x] Client disconnect during barrier is handled gracefully (no response written)
- [x] Error responses include `Retry-After` header
- [x] Barrier success logged at DEBUG level with elapsed time
- [x] Barrier failure logged at WARNING level with exception details

## Implementation Phases

### Phase 1: ReadBarrierMiddleware

Create `src/Confman.Api/Middleware/ReadBarrierMiddleware.cs`:

```
ReadBarrierMiddleware
├── Constructor(RequestDelegate next)
├── InvokeAsync(HttpContext context, IRaftCluster cluster, IConfiguration config)
│   ├── Check: is GET or HEAD?
│   ├── Check: path starts with /api?
│   ├── Check: ReadBarrier:Enabled?
│   ├── Read: ReadBarrier:TimeoutMs, ReadBarrier:FailureMode
│   ├── Create: linked CancellationTokenSource(RequestAborted, timeout)
│   ├── Call: cluster.ApplyReadBarrierAsync(token)
│   ├── On success: await _next(context)
│   └── On failure: write ProblemDetails response per FailureMode
└── Extension: UseReadBarrier(this IApplicationBuilder)
```

Follow the existing `CorrelationIdMiddleware` pattern exactly:
- Same namespace: `Confman.Api.Middleware`
- Same naming: `ReadBarrierMiddleware` + `ReadBarrierMiddlewareExtensions`
- XML doc summaries
- Resolve `IRaftCluster` and `IConfiguration` from `HttpContext.RequestServices` (middleware is singleton)

### Phase 2: Pipeline Registration

Modify `src/Confman.Api/Program.cs`:

1. Add `app.UseReadBarrier()` after `app.UseAuthorization()` (line 156)
2. Move the anonymous `GET /api/v1/configs` endpoint **before** `UseReadBarrier()` if needed, or handle it within the middleware (anonymous endpoints registered via `MapGet` run through the endpoint routing pipeline, not middleware — verify behavior)

### Phase 3: Configuration

Modify `src/Confman.Api/appsettings.json`:

Add `ReadBarrier` section:

```json
{
  "ReadBarrier": {
    "Enabled": true,
    "FailureMode": "reject",
    "TimeoutMs": 5000
  }
}
```

Also add to each `appsettings.node*.json` if node-specific overrides are desired.

### Phase 4: Unit Tests

Create `tests/Confman.Tests/ReadBarrierMiddlewareTests.cs`:

Follow the `AuthenticationTests` pattern (hand-rolled test doubles, `DefaultHttpContext`, in-memory config):

| Test | Scenario |
|------|----------|
| `GetApiPath_BarrierSucceeds_RequestProceeds` | Happy path |
| `GetApiPath_BarrierFails_RejectMode_Returns503` | QuorumUnreachableException + reject |
| `GetApiPath_BarrierFails_StaleMode_RequestProceeds` | Exception + stale mode logs and proceeds |
| `GetApiPath_BarrierFails_TimeoutMode_Returns504` | Exception + timeout mode |
| `GetApiPath_BarrierTimeout_Returns503` | OperationCanceledException from timeout |
| `PutApiPath_BypassesBarrier` | Write request skips barrier |
| `GetHealthPath_BypassesBarrier` | Non-/api path skips barrier |
| `HeadApiPath_BarrierApplied` | HEAD treated like GET |
| `GetApiPath_BarrierDisabled_BypassesBarrier` | Enabled=false |
| `InvalidFailureMode_FallsBackToReject` | Typo in config → reject + warning |
| `ClientDisconnect_NoResponseWritten` | RequestAborted fires → graceful exit |
| `ResponseIncludesRetryAfterHeader` | 503 response has Retry-After: 2 |
| `ResponseIsProblemDetails` | Error body is RFC 7807 |

Stub `IRaftCluster` with a hand-rolled test double (no mocking library in test project):

```
TestRaftCluster : IRaftCluster
├── ApplyReadBarrierAsync → configurable: succeed, throw QuorumUnreachable, throw OperationCanceled
└── Other members → throw NotImplementedException
```

## Dependencies & Risks

| Risk | Mitigation |
|------|------------|
| Barrier adds latency to every read | Configurable timeout + `Enabled` kill switch + measurable via DEBUG logs |
| Dashboard polling (2s) hammers barrier | Dashboard is anonymous — confirm it runs before middleware in pipeline |
| IRaftCluster.ApplyReadBarrierAsync API changes in DotNext upgrades | Pin DotNext version (5.26.2), test on upgrade |
| Cluster startup: all reads fail until leader elected | Expected — `/health/ready` already returns 503, load balancers handle it |
| Hand-rolling IRaftCluster test double is tedious | Only need `ApplyReadBarrierAsync` — stub everything else as `NotImplementedException` |

## Files to Create

- `src/Confman.Api/Middleware/ReadBarrierMiddleware.cs`
- `tests/Confman.Tests/ReadBarrierMiddlewareTests.cs`

## Files to Modify

- `src/Confman.Api/Program.cs` — add `app.UseReadBarrier()` after auth
- `src/Confman.Api/appsettings.json` — add `ReadBarrier` section

## References

- Brainstorm: `docs/brainstorms/2026-02-01-linearizable-reads-brainstorm.md`
- Existing middleware pattern: `src/Confman.Api/Middleware/CorrelationIdMiddleware.cs`
- DotNext `ApplyReadBarrierAsync` source: `RaftCluster.cs` in `dotnet/dotNext`
- RFC 7807 Problem Details: https://tools.ietf.org/html/rfc7807
- DotNext Raft documentation: https://dotnet.github.io/dotNext/features/cluster/raft.html
- Institutional learning: `docs/solutions/2026-01-24-dotnext-raft-cluster-members-configuration.md`
