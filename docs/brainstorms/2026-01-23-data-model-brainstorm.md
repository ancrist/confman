# Data Model Brainstorm

**Date:** 2026-01-23
**Status:** Complete
**Next Step:** `/workflows:plan` for implementation

---

## What We're Building

A **document-oriented data model** with flexible, user-defined namespaces, cascading inheritance, and immutable history.

---

## Why This Approach

### Constraints We Learned

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| Hierarchy | Flexible, user-defined | Users create their own namespace structures |
| Inheritance | Default on | Child namespaces inherit RBAC + schemas from parents |
| Value types | All four | Strings, JSON, binary, typed primitives |
| Versioning | Immutable history | Every version retained for audit/rollback |
| Secrets | External references | Store pointers (keyvault://...), not actual secrets |
| Schema governance | Flexible per namespace | Some teams self-manage, others need central oversight |
| Validation | On publish only | Drafts can be invalid, validation gates publishing |
| Roles | Fixed initially | Viewer, Editor, Publisher, Admin; custom roles later |

### Why Document-Oriented Wins

1. **Maps to hierarchical structure** — Namespaces and entries are natural documents
2. **Self-contained** — Each document has its metadata, no joins needed
3. **History embedded** — Immutable versions stored alongside current state
4. **REST-friendly** — GET requests return complete, coherent objects
5. **Works with RocksDB** — Key-value storage with document values

---

## Key Decisions

### 1. Core Entities

```
┌─────────────────┐     ┌─────────────────┐
│    Namespace    │────►│     Schema      │
│  (governance)   │     │  (validation)   │
└────────┬────────┘     └─────────────────┘
         │
         │ contains
         ▼
┌─────────────────┐     ┌─────────────────┐
│  ConfigEntry    │────►│  ValueHistory   │
│ (current state) │     │ (all versions)  │
└─────────────────┘     └─────────────────┘
```

### 2. Namespace Document

```json
{
  "path": "/teams/payments",
  "displayName": "Payments Team",
  "description": "Configuration for payment processing services",
  "owner": "payments-team@company.com",
  "inherit": true,
  "schema": {
    "$ref": "/schemas/payments/base"
  },
  "roles": {
    "admin": ["payments-leads@company.com"],
    "publisher": ["payments-oncall@company.com"],
    "editor": ["payments-devs@company.com"],
    "viewer": ["all-engineers@company.com"]
  },
  "settings": {
    "schemaEnforcement": "on-publish",
    "requireApproval": true,
    "minApprovers": 1
  },
  "metadata": {
    "created": "2026-01-15T10:00:00Z",
    "createdBy": "admin@company.com",
    "updated": "2026-01-20T14:30:00Z",
    "updatedBy": "lead@company.com"
  }
}
```

### 3. ConfigEntry Document

```json
{
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "type": "string",
  "description": "Stripe API version for payment processing",
  "published": {
    "value": "2024-01-01",
    "version": 5,
    "publishedAt": "2026-01-20T12:00:00Z",
    "publishedBy": "oncall@company.com",
    "publishRequestId": "pr-123"
  },
  "draft": {
    "value": "2025-01-01",
    "version": 6,
    "state": "pending_review",
    "updatedAt": "2026-01-22T09:00:00Z",
    "updatedBy": "dev@company.com",
    "publishRequestId": "pr-456"
  },
  "metadata": {
    "created": "2025-06-01T...",
    "createdBy": "...",
    "tags": ["stripe", "api", "version"]
  }
}
```

### 4. History Document (Separate for Scale)

```json
{
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "versions": [
    {
      "version": 1,
      "value": "2023-01-01",
      "publishedAt": "2025-06-01T...",
      "publishedBy": "user1@...",
      "raftTerm": 3,
      "raftIndex": 1001
    },
    {
      "version": 2,
      "value": "2023-06-01",
      "publishedAt": "2025-09-15T...",
      "publishedBy": "user2@...",
      "raftTerm": 3,
      "raftIndex": 2045
    }
  ]
}
```

### 5. Storage Key Layout

```
Keys in RocksDB:

/ns:/teams/payments                     → Namespace document
/ns:/teams/payments/stripe              → Child namespace (if exists)
/cfg:/teams/payments/stripe/api-version → ConfigEntry document
/hist:/teams/payments/stripe/api-version → History document
/schema:/schemas/payments/base          → Schema document
/audit:2026-01-23:evt-abc123            → Audit event
```

**Prefix-based organization enables:**
- Efficient namespace listing (`/ns:/teams/payments/*`)
- Bulk operations per namespace
- Separate compaction strategies per prefix

### 6. Value Types

| Type | Storage | Schema Validation |
|------|---------|-------------------|
| `string` | UTF-8 string | JSON Schema `type: string` |
| `number` | JSON number | JSON Schema `type: number/integer` |
| `boolean` | JSON boolean | JSON Schema `type: boolean` |
| `json` | JSON object/array | Full JSON Schema validation |
| `binary` | Base64-encoded | `contentEncoding: base64`, optional `contentMediaType` |
| `secret-ref` | URI string | Pattern: `^(keyvault|vault|env)://` |

### 7. Secret References

Secrets are stored as references, resolved at read time:

```json
{
  "key": "stripe/webhook-secret",
  "type": "secret-ref",
  "published": {
    "value": "keyvault://prod-vault/stripe-webhook-secret",
    "version": 2
  }
}
```

**Resolution options:**
- Client resolves (Confman returns reference as-is)
- Confman resolves (adds latency, requires vault access)
- Sidecar resolves (local agent handles resolution)

**Decision:** Client resolves by default; server-side resolution as opt-in feature.

### 8. Inheritance Resolution

When reading a config or checking permissions:

```
1. Start at target namespace
2. Check for local value/permission
3. If not found or inherit=true, walk up to parent
4. Continue until found or reach root
5. Root has system defaults
```

**Example:**
```
/teams/payments/stripe inherits from /teams/payments inherits from /teams inherits from /
```

**Permission check for user@company.com on /teams/payments/stripe:**
```
1. Check /teams/payments/stripe roles → not found
2. Check /teams/payments roles → user has "editor"
3. Return: editor permission
```

### 9. RBAC Permission Matrix

| Role | Permissions |
|------|-------------|
| **viewer** | `namespace:read`, `config:read:published` |
| **editor** | viewer + `config:read:draft`, `config:write:draft`, `publish-request:create` |
| **publisher** | editor + `publish-request:approve`, `publish-request:reject`, `config:publish`, `config:rollback` |
| **admin** | publisher + `namespace:write`, `roles:manage`, `schema:write`, `namespace:delete` |

### 10. Publishing Workflow

```
                    ┌─────────────────┐
                    │     Draft       │
                    │  (editor edits) │
                    └────────┬────────┘
                             │ submit for review
                             ▼
                    ┌─────────────────┐
              ┌─────│ Pending Review  │─────┐
              │     └─────────────────┘     │
              │ reject                      │ approve
              ▼                             ▼
     ┌─────────────────┐          ┌─────────────────┐
     │    Rejected     │          │    Approved     │
     │ (back to draft) │          │ (ready to pub)  │
     └─────────────────┘          └────────┬────────┘
                                           │ publish
                                           ▼
                                  ┌─────────────────┐
                                  │   Published     │
                                  │  (live config)  │
                                  └─────────────────┘
```

**Publish Request document:**
```json
{
  "id": "pr-456",
  "namespace": "/teams/payments",
  "keys": ["stripe/api-version", "stripe/timeout-ms"],
  "state": "pending_review",
  "submittedBy": "dev@company.com",
  "submittedAt": "2026-01-22T09:00:00Z",
  "changes": [
    {
      "key": "stripe/api-version",
      "fromVersion": 5,
      "toVersion": 6,
      "diff": { "old": "2024-01-01", "new": "2025-01-01" }
    }
  ],
  "reviews": [
    {
      "reviewer": "lead@company.com",
      "decision": "approved",
      "comment": "Tested in staging",
      "at": "2026-01-22T11:00:00Z"
    }
  ]
}
```

---

## Schema Design

### JSON Schema with Extensions

```json
{
  "$id": "/schemas/payments/stripe",
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "properties": {
    "api-version": {
      "type": "string",
      "pattern": "^\\d{4}-\\d{2}-\\d{2}$",
      "description": "Stripe API version in YYYY-MM-DD format"
    },
    "timeout-ms": {
      "type": "integer",
      "minimum": 100,
      "maximum": 30000,
      "default": 5000
    },
    "webhook-secret": {
      "type": "string",
      "x-confman-secret-ref": true,
      "description": "Reference to webhook secret in vault"
    }
  },
  "required": ["api-version"]
}
```

### Custom Extensions

| Extension | Purpose |
|-----------|---------|
| `x-confman-secret-ref` | Marks field as secret reference |
| `x-confman-deprecated` | Warns on read, soft-blocks new writes |
| `x-confman-immutable` | Cannot be changed after initial set |
| `x-confman-rotation-days` | Reminder to rotate (for secret refs) |
| `x-confman-allowed-environments` | Restrict to specific environments |

---

## Open Questions (For Planning Phase)

### Storage Optimization
- [ ] When to split history into chunks (by time? by count?)
- [ ] Compression strategy for large JSON values
- [ ] Index strategy for tag-based queries

### Inheritance Edge Cases
- [ ] What happens when parent namespace is deleted?
- [ ] How to handle circular inheritance (prevent?)
- [ ] Cache invalidation when parent changes

### Workflow Customization
- [ ] Multi-approver requirements per namespace
- [ ] Auto-approve for certain change types
- [ ] Scheduled publishing (publish at time X)

---

## Success Criteria

1. **Namespaces** can be created with arbitrary hierarchy
2. **Inheritance** correctly resolves RBAC and schemas
3. **All value types** stored and retrieved correctly
4. **History** is immutable and queryable
5. **Publishing workflow** enforces validation and approvals
6. **Secret references** are stored (not resolved) by Confman

---

## Next Steps

1. Combine with system architecture brainstorm
2. Run `/workflows:plan` to create implementation plan
3. Define C# entity classes and interfaces
4. Implement storage layer with RocksDB
5. Build API endpoints
