# Publishing Workflow Brainstorm

**Date:** 2026-01-23
**Status:** Complete
**Next Step:** `/workflows:plan` for implementation

---

## What We're Building

A **governed publishing workflow** that enforces four-eyes principle, configurable approvals, and webhook-based notifications.

---

## Why This Approach

### Constraints We Learned

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| Self-approval | Never allowed | Four-eyes principle for all changes |
| Approver count | Configurable (1-N) | Namespace admin sets requirement |
| Notifications | Webhooks | External systems handle delivery (Slack, Teams) |
| Expiration | No expiration | Requests stay until acted on |
| Batch publish | Single key per request | Simplifies workflow, avoids atomic multi-key |
| Rollback | Configurable per namespace | Production can instant rollback, sensitive needs approval |
| Comments | Optional on all actions | Provides context without mandatory overhead |

---

## Key Decisions

### 1. Workflow States

```
┌─────────┐     ┌──────────────┐     ┌───────────┐     ┌───────────┐
│  Draft  │────►│   Pending    │────►│  Approved │────►│ Published │
│         │     │   Review     │     │           │     │           │
└─────────┘     └──────────────┘     └───────────┘     └───────────┘
                       │
                       │ reject
                       ▼
                ┌──────────────┐
                │   Rejected   │
                │ (back to     │
                │   draft)     │
                └──────────────┘
```

**State Definitions:**

| State | Description | Who Can Act |
|-------|-------------|-------------|
| `draft` | Work in progress, not submitted | Editor, Publisher, Admin |
| `pending_review` | Awaiting approval(s) | Publisher, Admin (not submitter) |
| `approved` | All required approvals received | System auto-transitions to published |
| `published` | Live configuration | — |
| `rejected` | Sent back for changes | Returns to draft state |

### 2. Publish Request Document

```json
{
  "id": "pr-456",
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "state": "pending_review",

  "submission": {
    "submittedBy": "dev@company.com",
    "submittedAt": "2026-01-22T09:00:00Z",
    "comment": "Upgrading to latest Stripe API version",
    "draftVersion": 6,
    "diff": {
      "previousPublished": "2024-01-01",
      "proposed": "2025-01-01"
    }
  },

  "reviews": [
    {
      "reviewer": "lead@company.com",
      "decision": "approved",
      "comment": "Tested in staging, looks good",
      "at": "2026-01-22T11:00:00Z"
    },
    {
      "reviewer": "oncall@company.com",
      "decision": "approved",
      "comment": null,
      "at": "2026-01-22T14:00:00Z"
    }
  ],

  "resolution": {
    "state": "published",
    "resolvedAt": "2026-01-22T14:00:00Z",
    "publishedVersion": 6
  }
}
```

### 3. Four-Eyes Enforcement

```
Submitter: dev@company.com

Eligible reviewers (publishers on this namespace):
- lead@company.com ✓
- oncall@company.com ✓
- dev@company.com ✗ (is submitter)

minApprovers: 2
Current approvals: 1

Status: Needs 1 more approval from eligible reviewer
```

### 4. Namespace Workflow Settings

```json
{
  "path": "/teams/payments",
  "settings": {
    "workflow": {
      "requireApproval": true,
      "minApprovers": 2,
      "allowSelfApproval": false,
      "rollbackRequiresApproval": false
    },
    "webhooks": [
      {
        "event": "publish_request.created",
        "url": "https://hooks.slack.com/services/T00/B00/xxx",
        "secret": "keyvault://webhooks/slack-signing"
      },
      {
        "event": "config.published",
        "url": "https://internal.company.com/config-events",
        "headers": {
          "X-Source": "confman"
        }
      }
    ]
  }
}
```

### 5. Webhook Events

| Event | Trigger | Payload |
|-------|---------|---------|
| `publish_request.created` | Draft submitted for review | `{ requestId, namespace, key, submitter, diff }` |
| `publish_request.approved` | Reviewer approves | `{ requestId, reviewer, approvalsReceived, approvalsRequired }` |
| `publish_request.rejected` | Reviewer rejects | `{ requestId, reviewer, comment }` |
| `config.published` | Config goes live | `{ namespace, key, previousVersion, newVersion, publishedBy }` |
| `config.rolled_back` | Rollback executed | `{ namespace, key, fromVersion, toVersion, rolledBackBy }` |

**Webhook Payload Example:**

```json
{
  "event": "publish_request.created",
  "timestamp": "2026-01-22T09:00:00Z",
  "data": {
    "requestId": "pr-456",
    "namespace": "/teams/payments",
    "key": "stripe/api-version",
    "submitter": "dev@company.com",
    "diff": {
      "from": "2024-01-01",
      "to": "2025-01-01"
    },
    "approvalsRequired": 2,
    "reviewUrl": "https://confman.company.com/requests/pr-456"
  }
}
```

**Webhook Delivery:**
- POST to configured URL
- Sign payload with HMAC-SHA256 using secret
- Retry 3 times with exponential backoff on failure
- Log delivery status for debugging

### 6. Rollback Flow

**Instant Rollback (when allowed):**
```
Publisher clicks "Rollback to v5"
    │
    ▼
System:
1. Validates publisher has permission
2. Checks namespace allows instant rollback
3. Creates new version (v7) with v5's value
4. Marks v7 as published
5. Fires config.rolled_back webhook
6. Records audit event
```

**Rollback with Approval (when required):**
```
Publisher clicks "Rollback to v5"
    │
    ▼
System:
1. Creates draft with v5's value
2. Creates publish request (rollback type)
3. Fires publish_request.created webhook
4. Normal approval flow proceeds
```

### 7. API Endpoints

```
# Submit draft for review
POST /api/v1/publish-requests
{
  "namespace": "/teams/payments",
  "key": "stripe/api-version",
  "comment": "Upgrading to latest API version"
}

# Get publish request details
GET /api/v1/publish-requests/{id}

# Approve a request
POST /api/v1/publish-requests/{id}/approve
{
  "comment": "Tested in staging"  // optional
}

# Reject a request
POST /api/v1/publish-requests/{id}/reject
{
  "comment": "Missing test results"  // optional
}

# List pending requests for a namespace
GET /api/v1/namespaces/{path}/publish-requests?state=pending_review

# Rollback a key
POST /api/v1/config/{namespace}/{key}/rollback
{
  "toVersion": 5,
  "comment": "Reverting due to production issue"
}
```

### 8. Validation on Submit

When a draft is submitted for review:

```
1. Load namespace schema (with inheritance)
2. Validate draft value against schema
3. If invalid:
   - Reject submission
   - Return validation errors
   - Draft remains in draft state
4. If valid:
   - Create publish request
   - Fire webhook
   - Transition to pending_review
```

### 9. Audit Trail

Every workflow action creates an audit event:

```json
{
  "id": "evt-789",
  "timestamp": "2026-01-22T11:00:00Z",
  "action": "publish_request.approved",
  "actor": {
    "id": "lead@company.com",
    "type": "user",
    "ip": "10.0.1.50"
  },
  "resource": {
    "type": "publish_request",
    "id": "pr-456",
    "namespace": "/teams/payments",
    "key": "stripe/api-version"
  },
  "details": {
    "comment": "Tested in staging, looks good",
    "approvalsAfter": 1,
    "approvalsRequired": 2
  },
  "raftTerm": 5,
  "raftIndex": 3456
}
```

---

## Workflow Diagram (Complete)

```
                    EDITOR                           PUBLISHER
                      │                                  │
                      │ create/edit draft                │
                      ▼                                  │
               ┌─────────────┐                          │
               │    DRAFT    │                          │
               │             │                          │
               │ • No schema │                          │
               │   validation│                          │
               │ • Can edit  │                          │
               │   freely    │                          │
               └──────┬──────┘                          │
                      │                                  │
                      │ submit for review                │
                      │ (validates schema)               │
                      ▼                                  │
               ┌─────────────┐                          │
               │   PENDING   │                          │
               │   REVIEW    │──────────────────────────┤
               │             │                          │
               │ • Webhook   │◄─────────────────────────┤
               │   fires     │     approve/reject       │
               │ • Submitter │     (not submitter)      │
               │   locked out│                          │
               └──────┬──────┘                          │
                      │                                  │
         ┌────────────┼────────────┐                    │
         │ reject     │            │ approve            │
         ▼            │            ▼                    │
  ┌─────────────┐     │     ┌─────────────┐            │
  │  REJECTED   │     │     │  APPROVED   │            │
  │             │     │     │  (if N met) │            │
  │ • Returns   │     │     │             │            │
  │   to draft  │     │     │ • Auto-     │            │
  │ • Comment   │     │     │   publishes │            │
  │   for       │     │     └──────┬──────┘            │
  │   context   │     │            │                    │
  └─────────────┘     │            ▼                    │
                      │     ┌─────────────┐            │
                      │     │  PUBLISHED  │            │
                      │     │             │◄───────────┤
                      │     │ • Live      │  rollback  │
                      │     │ • Webhook   │  (if       │
                      │     │ • Audit     │  allowed)  │
                      │     └─────────────┘            │
                      │                                  │
                      └──────────────────────────────────┘
```

---

## Open Questions (For Planning Phase)

### Edge Cases
- [ ] What if last eligible reviewer leaves the company mid-review?
- [ ] Can a rejected request be resubmitted, or must editor create new draft?
- [ ] What if schema changes while request is pending?

### Performance
- [ ] Index publish requests by state for efficient "my pending reviews" queries
- [ ] Webhook delivery queue and retry mechanism

### Future Enhancements
- [ ] Scheduled publishing ("publish at 2am")
- [ ] Change windows ("only publish Mon-Thu 9am-5pm")
- [ ] Auto-approve for specific change patterns
- [ ] Approval delegation ("approve on my behalf")

---

## Success Criteria

1. **Four-eyes enforced** — Submitter cannot approve their own request
2. **Approval counting** — Correctly tracks approvals toward threshold
3. **Webhooks fire** — On all workflow state changes
4. **Validation gates publish** — Invalid values cannot be published
5. **Rollback works** — Both instant and approval-required modes
6. **Full audit trail** — Every action recorded with actor and context

---

## Next Steps

1. Combine with system architecture and data model brainstorms
2. Run `/workflows:plan` to create implementation plan
3. Implement publish request state machine
4. Build webhook delivery system
5. Add workflow API endpoints
