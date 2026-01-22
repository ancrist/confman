# API Design Brainstorm

**Date:** 2026-01-23
**Status:** Complete
**Next Step:** `/workflows:plan` for implementation

---

## What We're Building

A **REST API** with URL path versioning, content negotiation, RFC 7807 errors, and API key authentication.

---

## Why This Approach

### Constraints We Learned

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| Versioning | URL path (`/api/v1/...`) | Explicit, easy routing, common pattern |
| Response format | Content negotiation | Clients request JSON, YAML, or raw |
| Bulk operations | No bulk | Single resource per request, consistent with workflow |
| Errors | RFC 7807 Problem Details | Standard, machine-readable, ASP.NET Core native |
| Authentication | API key (`X-API-Key`) | Simple, static keys in config |
| Key management | Static keys in config | No management API initially |
| Pagination | Cursor-based | Consistent with large datasets, no skips/dups |
| Filtering | Start simple | Namespace listing first, add filters when needed |
| Health checks | Liveness + readiness | Separate endpoints for load balancer integration |

---

## Key Decisions

### 1. URL Structure

```
/api/v1/
│
├── config/                           # Published config (consumers)
│   └── {namespace}/{key}             GET - read published value
│
├── drafts/                           # Draft management (editors)
│   └── {namespace}/{key}             GET - read draft
│                                     PUT - create/update draft
│                                     DELETE - discard draft
│
├── namespaces/                       # Namespace management
│   ├──                               POST - create namespace
│   ├── {path}                        GET - read namespace
│   │                                 PUT - update namespace
│   │                                 DELETE - delete namespace
│   ├── {path}/keys                   GET - list keys in namespace
│   ├── {path}/schema                 GET - read schema
│   │                                 PUT - update schema
│   └── {path}/roles                  GET - read role assignments
│                                     PUT - update role assignments
│
├── publish-requests/                 # Publishing workflow
│   ├──                               POST - submit draft for review
│   │                                 GET - list requests (filtered)
│   └── {id}                          GET - read request details
│       ├── /approve                  POST - approve request
│       └── /reject                   POST - reject request
│
├── audit/                            # Audit trail
│   └──                               GET - query audit events
│
├── admin/                            # Cluster operations
│   ├── status                        GET - cluster health, leader info
│   └── rollback                      POST - rollback a key to version
│
└── health/                           # Health checks (no auth)
    ├──                               GET - liveness (process alive)
    └── ready                         GET - readiness (in quorum)
```

### 2. Content Negotiation

Clients specify desired format via `Accept` header:

| Accept Header | Response Format |
|---------------|-----------------|
| `application/json` (default) | Full JSON with metadata |
| `application/yaml` | YAML format |
| `text/plain` | Raw value only, no metadata |
| `application/octet-stream` | Binary value (for binary configs) |

**Example: JSON response**
```json
{
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "value": "2025-01-01",
  "version": 6,
  "type": "string",
  "publishedAt": "2026-01-22T14:00:00Z",
  "publishedBy": "oncall@company.com"
}
```

**Example: Raw response**
```
2025-01-01
```

### 3. Error Format (RFC 7807)

```json
{
  "type": "https://confman.company.com/errors/not-found",
  "title": "Config Not Found",
  "status": 404,
  "detail": "No published config found for key 'stripe/api-version' in namespace '/teams/payments'",
  "instance": "/api/v1/config/teams/payments/stripe/api-version",
  "traceId": "abc123"
}
```

**Error types:**

| Type | Status | Description |
|------|--------|-------------|
| `validation-failed` | 400 | Schema validation errors |
| `unauthorized` | 401 | Missing or invalid API key |
| `forbidden` | 403 | Insufficient permissions |
| `not-found` | 404 | Resource doesn't exist |
| `conflict` | 409 | Concurrent modification conflict |
| `leader-required` | 503 | Write requires leader, node is follower |

### 4. Authentication

**Request header:**
```
X-API-Key: confman_prod_abc123def456
```

**API key format:**
```
confman_{environment}_{random}
```

**Key configuration (appsettings.json):**
```json
{
  "Auth": {
    "ApiKeys": [
      {
        "key": "confman_prod_abc123def456",
        "name": "Production Service Account",
        "principal": "svc-payments@company.com",
        "roles": {
          "/teams/payments/*": "editor",
          "/*": "viewer"
        }
      }
    ]
  }
}
```

### 5. Pagination

**Request:**
```
GET /api/v1/namespaces/teams/payments/keys?limit=50&cursor=eyJrIjoic3RyaXBlL3RpbWVvdXQifQ==
```

**Response:**
```json
{
  "items": [
    { "key": "stripe/timeout-ms", "version": 3, ... },
    { "key": "stripe/webhook-url", "version": 1, ... }
  ],
  "pagination": {
    "limit": 50,
    "hasMore": true,
    "nextCursor": "eyJrIjoic3RyaXBlL3dlYmhvb2stdXJsIn0="
  }
}
```

**Cursor format:** Base64-encoded JSON with last key seen.

### 6. Health Endpoints

**Liveness (`/health`):**
```json
{
  "status": "healthy",
  "timestamp": "2026-01-23T10:00:00Z"
}
```
- Returns 200 if process is running
- No authentication required
- Used by process monitors

**Readiness (`/health/ready`):**
```json
{
  "status": "ready",
  "timestamp": "2026-01-23T10:00:00Z",
  "cluster": {
    "nodeId": "node1",
    "role": "follower",
    "leaderKnown": true,
    "inQuorum": true
  }
}
```
- Returns 200 if ready to serve requests
- Returns 503 if not in quorum
- No authentication required
- Used by load balancers

### 7. Request/Response Examples

**Read published config:**
```http
GET /api/v1/config/teams/payments/stripe/api-version HTTP/1.1
Host: confman.company.com
X-API-Key: confman_prod_abc123
Accept: application/json

HTTP/1.1 200 OK
Content-Type: application/json

{
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "value": "2025-01-01",
  "version": 6,
  "type": "string",
  "publishedAt": "2026-01-22T14:00:00Z"
}
```

**Create/update draft:**
```http
PUT /api/v1/drafts/teams/payments/stripe/api-version HTTP/1.1
Host: confman.company.com
X-API-Key: confman_prod_abc123
Content-Type: application/json

{
  "value": "2026-01-01",
  "comment": "Upgrading to 2026 API version"
}

HTTP/1.1 200 OK
Content-Type: application/json

{
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "draft": {
    "value": "2026-01-01",
    "version": 7,
    "updatedAt": "2026-01-23T10:00:00Z",
    "updatedBy": "svc-payments@company.com"
  }
}
```

**Submit for review:**
```http
POST /api/v1/publish-requests HTTP/1.1
Host: confman.company.com
X-API-Key: confman_prod_abc123
Content-Type: application/json

{
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "comment": "Ready for production"
}

HTTP/1.1 201 Created
Content-Type: application/json
Location: /api/v1/publish-requests/pr-789

{
  "id": "pr-789",
  "state": "pending_review",
  ...
}
```

**Approve request:**
```http
POST /api/v1/publish-requests/pr-789/approve HTTP/1.1
Host: confman.company.com
X-API-Key: confman_prod_def456
Content-Type: application/json

{
  "comment": "LGTM, tested in staging"
}

HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": "pr-789",
  "state": "published",
  "resolution": {
    "resolvedAt": "2026-01-23T11:00:00Z",
    "publishedVersion": 7
  }
}
```

### 8. URL Encoding for Namespaces

Namespace paths contain `/` characters. Options:

**Option A: URL-encoded paths**
```
GET /api/v1/config/teams%2Fpayments/stripe%2Fapi-version
```

**Option B: Catch-all route (chosen)**
```
GET /api/v1/config/{*namespacePath}/keys/{key}
```

ASP.NET Core supports catch-all parameters. We'll use:
```
/api/v1/config/{**path}
```

Where `path` is `teams/payments/stripe/api-version` and we split into namespace + key.

**Convention:** Everything after last `/` is the key, everything before is namespace.

### 9. Conditional Requests

Support `ETag` and `If-None-Match` for efficient caching:

```http
GET /api/v1/config/teams/payments/stripe/api-version HTTP/1.1
X-API-Key: confman_prod_abc123

HTTP/1.1 200 OK
ETag: "v6"
Content-Type: application/json

{ "value": "2025-01-01", "version": 6, ... }
```

```http
GET /api/v1/config/teams/payments/stripe/api-version HTTP/1.1
X-API-Key: confman_prod_abc123
If-None-Match: "v6"

HTTP/1.1 304 Not Modified
```

---

## Open Questions (For Planning Phase)

### URL Design
- [ ] Exact path parsing for namespace/key split
- [ ] Handling of special characters in keys

### Performance
- [ ] Rate limiting strategy
- [ ] Response caching headers

### Documentation
- [ ] OpenAPI/Swagger generation
- [ ] API reference documentation

---

## Success Criteria

1. **Versioned URLs** — All endpoints under `/api/v1/`
2. **Content negotiation** — JSON, YAML, raw responses work
3. **RFC 7807 errors** — Consistent error format
4. **API key auth** — Static keys validated
5. **Health endpoints** — Liveness and readiness work
6. **Pagination** — Cursor-based for list endpoints

---

## Next Steps

1. Combine with other brainstorms
2. Run `/workflows:plan` to create implementation plan
3. Implement ASP.NET Core controllers
4. Add authentication middleware
5. Generate OpenAPI documentation
