---
title: "Role Name Mismatch Between Config and Authorization Policies"
category: auth
tags: [rbac, authorization, api-keys, configuration, aspnetcore]
module: Confman.Api
symptoms:
  - Reader API key returns 403 Forbidden on all requests
  - "AuthenticationScheme: ApiKey was forbidden" in logs
  - API key authenticates successfully but authorization fails
date: 2026-02-02
---

# Role Name Mismatch Between Config and Authorization Policies

## Problem

The `confman_dev_reader` API key returned **403 Forbidden** on all requests, despite authenticating successfully. The API key had role `"reader"` in `appsettings.json`, but the `ReadOnly` authorization policy checked for `"read"`.

```bash
curl -s http://127.0.0.1:6100/api/v1/namespaces/test/config/hello \
  -H "X-Api-Key: confman_dev_reader"
# 403 Forbidden
```

Log output:
```
[DBG] Authenticated API key: Development Reader with roles: reader
[INF] AuthenticationScheme: ApiKey was forbidden.
```

## Root Cause

The auth flow in Confman is a **passthrough chain**:

1. `appsettings.json` defines role strings per API key
2. `ApiKeyAuthenticationHandler` maps them directly to `ClaimTypes.Role` claims (no validation)
3. Authorization policies check roles via `IsInRole()` — must match exactly

The role names in config (`"reader"`) and policy (`"read"`) diverged because they were defined independently with no shared constant or enum.

```
appsettings.json       →  ApiKeyAuthenticationHandler  →  AuthorizationPolicy
"reader"               →  Claim(Role, "reader")        →  IsInRole("read") ← NO MATCH
```

Additionally, the code used ad-hoc role names (`read`, `write`) that didn't match the RBAC model documented in CLAUDE.md (`viewer`, `editor`, `publisher`, `admin`).

## Solution

Aligned all role names with the documented RBAC model: `viewer`, `editor`, `publisher`, `admin`.

### 1. Authorization policies (`NamespaceAuthorizationHandler.cs`)

**Before:**
```csharp
options.AddPolicy("ReadOnly", policy =>
    policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.IsInRole("admin") ||
            ctx.User.IsInRole("read") ||       // ← ad-hoc name
            ctx.User.IsInRole("write")));       // ← ad-hoc name

options.AddPolicy("Write", policy =>
    policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.IsInRole("admin") ||
            ctx.User.IsInRole("write")));       // ← ad-hoc name
```

**After:**
```csharp
options.AddPolicy("ReadOnly", policy =>
    policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.IsInRole("admin") ||
            ctx.User.IsInRole("publisher") ||
            ctx.User.IsInRole("editor") ||
            ctx.User.IsInRole("viewer")));

options.AddPolicy("Write", policy =>
    policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.IsInRole("admin") ||
            ctx.User.IsInRole("publisher") ||
            ctx.User.IsInRole("editor")));
```

### 2. API key configuration (`appsettings.json`)

**Before:**
```json
{ "Key": "confman_dev_reader", "Name": "Development Reader", "Roles": ["reader"] }
```

**After:**
```json
{ "Key": "confman_dev_reader", "Name": "Development Reader", "Roles": ["viewer"] }
```

## Key Insights

1. **Passthrough auth handlers amplify typos** — When the handler blindly copies config strings into claims, there's no compile-time or startup-time validation that role names match policy expectations. A single character difference causes silent 403s.

2. **Use a single source of truth for role names** — The RBAC model was documented in CLAUDE.md but the code used different names. Aligning to the documented model (`viewer/editor/publisher/admin`) prevents future drift.

3. **Role hierarchy matters for policies** — The `ReadOnly` policy now accepts all four roles (any authenticated user can read), while `Write` excludes `viewer`. This matches the permission matrix in the RBAC model where viewers can read but not write.

4. **Symptom: auth succeeds, authz fails** — When logs show "Authenticated API key... was forbidden", the issue is always in the authorization policy assertions, not the authentication handler. Check `IsInRole()` values against the actual claim values.

## Verification

```bash
# Should return 200 with config data
curl -s http://127.0.0.1:6100/api/v1/namespaces/test/config/hello \
  -H "X-Api-Key: confman_dev_reader"

# Should return 403 (viewer cannot write)
curl -s -X PUT http://127.0.0.1:6100/api/v1/namespaces/test/config/hello \
  -H "X-Api-Key: confman_dev_reader" \
  -H "Content-Type: application/json" \
  -d '{"value": "world"}'
```

## References

- [GitHub Issue #7](https://github.com/ancrist/confman/issues/7) — Original bug report
- `src/Confman.Api/Auth/NamespaceAuthorizationHandler.cs:89-101` — Policy definitions
- `src/Confman.Api/Auth/ApiKeyAuthenticationHandler.cs:70-73` — Role claim passthrough
- `src/Confman.Api/appsettings.json:26-37` — API key configuration
- `CLAUDE.md` — RBAC model (viewer, editor, publisher, admin)
