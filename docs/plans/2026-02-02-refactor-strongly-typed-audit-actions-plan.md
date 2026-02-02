---
title: "refactor: Strongly-typed AuditAction value object"
type: refactor
date: 2026-02-02
---

# refactor: Strongly-typed AuditAction value object

## Overview

Replace the string-based `AuditEvent.Action` property with a sealed `AuditAction` record that carries structured `ResourceType` and `Verb` properties while serializing as a flat string (`"config.created"`) for full backward compatibility.

## Problem Statement / Motivation

Audit action types are scattered as inline string literals across 4 command files:

- `SetConfigCommand.cs:36` — `"config.created"` / `"config.updated"`
- `DeleteConfigCommand.cs:26` — `"config.deleted"`
- `SetNamespaceCommand.cs:32` — `"namespace.created"` / `"namespace.updated"`
- `DeleteNamespaceCommand.cs:24` — `"namespace.deleted"`

This is fragile. Typos compile silently, there's no way to query by resource type or verb programmatically, and the upcoming publishing workflow will add 5+ more action types (`config.submitted`, `config.approved`, `config.rejected`, `config.published`, `config.rolled_back`). A strongly-typed value object provides compile-time safety, IntelliSense, and rich querying without changing the wire format.

## Proposed Solution

A sealed `AuditAction` record with:
- `ResourceType` (string) — `"config"` or `"namespace"` (extensible)
- `Verb` (string) — `"created"`, `"updated"`, `"deleted"` (extensible)
- `Value` (string) — computed `$"{ResourceType}.{Verb}"` for serialization
- Static members for all known actions
- `Parse(string)` factory for deserialization
- `ToString()` override returning `Value`
- Custom `JsonConverter<AuditAction>` for System.Text.Json
- Custom `BsonMapper` registration for LiteDB

**Key constraint:** The serialized form must remain the flat string `"config.created"` everywhere — JSON API responses, BSON storage, Raft snapshots, and structured logs.

## Technical Considerations

### Serialization Boundaries

| Boundary | Format | Strategy |
|----------|--------|----------|
| REST API (JSON) | `"action": "config.created"` | Custom `JsonConverter<AuditAction>` — read/write as string |
| LiteDB (BSON) | String field | `BsonMapper.Global.RegisterType<AuditAction>()` — serialize/deserialize as BsonValue string |
| Raft snapshots (JSON) | `"Action": "config.created"` | Same `JsonConverter` — snapshot format unchanged, `SnapshotData.Version` stays at `1` |
| Structured logging | `{Action}` template | `ToString()` returns `"config.created"` — Serilog renders correctly |
| Dashboard JS | `action.split('.')[1]` | No change — API still returns flat string |

### Backward Compatibility

- **Existing BSON data**: The `BsonMapper` deserializer must handle plain strings from pre-migration audit events. Since the new type also serializes as a plain string, this is transparent.
- **Existing snapshots**: JSON snapshots contain `"Action": "config.created"` as a string. The `JsonConverter` reads this identically.
- **Raft replay**: Commands re-apply and produce `AuditAction` objects. The `AuditIdGenerator` excludes action from the ID hash, so the upsert works correctly even if the action mutates (created → updated on replay).
- **Mixed clusters**: Not a concern — the wire format is identical. A node running old code and a node running new code produce the same JSON/BSON string.

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| `ResourceType` / `Verb` types | `string` (not enum) | Extensible without recompilation. Publishing workflow adds new verbs. |
| Input validation | None at construction | Value object is a data carrier. Validation happens at the command layer. |
| `AuditEventDto.Action` | Stays `string` | API contract unchanged. `FromModel` calls `.ToString()`. |
| `AuditIdGenerator.action` param | Change to `AuditAction` | Type consistency, even though unused. |
| Implicit string conversion | No | Explicit `.ToString()` and `Parse()` prevent hidden type coercion. |
| `SnapshotData.Version` | Stays `1` | Wire format unchanged. |
| Multi-word verbs | Snake_case (`rolled_back`) | Matches dotted format convention. |

## Acceptance Criteria

- [x] `AuditAction` sealed record with `ResourceType`, `Verb`, `Value` properties
- [x] Static members: `ConfigCreated`, `ConfigUpdated`, `ConfigDeleted`, `NamespaceCreated`, `NamespaceUpdated`, `NamespaceDeleted`
- [x] `AuditAction.Parse(string)` factory (e.g., `"config.created"` → `AuditAction`)
- [x] `ToString()` returns `"resourceType.verb"`
- [x] Custom `JsonConverter<AuditAction>` — serializes as flat string
- [x] `BsonMapper.Global.RegisterType<AuditAction>()` — serializes as flat string
- [x] `AuditEvent.Action` property type changed from `string` to `AuditAction`
- [x] All 4 command files use static members instead of string literals
- [x] `AuditEventDto.Action` stays `string`, `FromModel` calls `.ToString()`
- [x] `AuditIdGenerator.Generate` parameter changed to `AuditAction`
- [x] All existing tests pass (updated assertions)
- [x] New test: JSON round-trip serialization produces flat string
- [x] New test: BSON round-trip serialization produces flat string
- [x] New test: backward-compat — plain string BSON deserializes to `AuditAction`
- [x] New test: backward-compat — plain string JSON snapshot deserializes to `AuditAction`
- [x] `GetAuditEventsAsync_ReturnsOrderedByTimestamp` test updated to use valid `AuditAction` values

## Implementation

### Files to Create

#### `src/Confman.Api/Models/AuditAction.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Confman.Api.Models;

[JsonConverter(typeof(AuditActionJsonConverter))]
public sealed record AuditAction
{
    public string ResourceType { get; }
    public string Verb { get; }
    public string Value { get; }

    private AuditAction(string resourceType, string verb)
    {
        ResourceType = resourceType;
        Verb = verb;
        Value = $"{resourceType}.{verb}";
    }

    // Known actions
    public static readonly AuditAction ConfigCreated = new("config", "created");
    public static readonly AuditAction ConfigUpdated = new("config", "updated");
    public static readonly AuditAction ConfigDeleted = new("config", "deleted");
    public static readonly AuditAction NamespaceCreated = new("namespace", "created");
    public static readonly AuditAction NamespaceUpdated = new("namespace", "updated");
    public static readonly AuditAction NamespaceDeleted = new("namespace", "deleted");

    public static AuditAction Parse(string value)
    {
        var dot = value.IndexOf('.');
        if (dot < 0)
            throw new FormatException($"Invalid audit action format: '{value}'. Expected 'resourceType.verb'.");

        var resourceType = value[..dot];
        var verb = value[(dot + 1)..];
        return new AuditAction(resourceType, verb);
    }

    public override string ToString() => Value;
}

public sealed class AuditActionJsonConverter : JsonConverter<AuditAction>
{
    public override AuditAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => AuditAction.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, AuditAction value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
```

### Files to Modify

#### `src/Confman.Api/Models/AuditEvent.cs`

- Change `public required string Action { get; set; }` → `public required AuditAction Action { get; set; }`

#### `src/Confman.Api/Storage/LiteDbConfigStore.cs`

- Add `BsonMapper.Global.RegisterType<AuditAction>()` in constructor (before collection access):

```csharp
BsonMapper.Global.RegisterType<AuditAction>(
    serialize: action => action.Value,
    deserialize: bson => AuditAction.Parse(bson.AsString));
```

#### `src/Confman.Api/Cluster/Commands/SetConfigCommand.cs`

- Line 36: Replace `var action = existing is null ? "config.created" : "config.updated"` with `var action = existing is null ? AuditAction.ConfigCreated : AuditAction.ConfigUpdated`

#### `src/Confman.Api/Cluster/Commands/DeleteConfigCommand.cs`

- Line 26: Replace `"config.deleted"` with `AuditAction.ConfigDeleted`

#### `src/Confman.Api/Cluster/Commands/SetNamespaceCommand.cs`

- Line 32: Replace string conditional with `AuditAction.NamespaceCreated` / `AuditAction.NamespaceUpdated`

#### `src/Confman.Api/Cluster/Commands/DeleteNamespaceCommand.cs`

- Line 24: Replace `"namespace.deleted"` with `AuditAction.NamespaceDeleted`

#### `src/Confman.Api/Cluster/Commands/AuditIdGenerator.cs`

- Line 16: Change `string action` parameter to `AuditAction action`

#### `src/Confman.Api/Controllers/AuditController.cs`

- DTO mapping: `Action = evt.Action.ToString()`

#### `tests/Confman.Tests/CommandTests.cs`

- Update 7 assertions to compare against `AuditAction` static members (e.g., `Assert.Equal(AuditAction.ConfigCreated, audit[0].Action)`)

#### `tests/Confman.Tests/LiteDbConfigStoreTests.cs`

- Update `AuditEvent` constructions to use `AuditAction.Parse()` or static members
- Rewrite `GetAuditEventsAsync_ReturnsOrderedByTimestamp` to use valid actions instead of `"oldest"`, `"newest"`, `"middle"`
- Add JSON round-trip test
- Add BSON round-trip test
- Add backward-compat deserialization test

## References & Research

### Internal References

- Audit model: `src/Confman.Api/Models/AuditEvent.cs`
- Command files: `src/Confman.Api/Cluster/Commands/Set*.cs`, `Delete*.cs`
- ID generator: `src/Confman.Api/Cluster/Commands/AuditIdGenerator.cs:16`
- Storage: `src/Confman.Api/Storage/LiteDbConfigStore.cs:144-151`
- Snapshots: `src/Confman.Api/Cluster/ConfigStateMachine.cs:116-148`
- Dashboard JS: `src/Confman.Dashboard/index.html:932,957`
- Tests: `tests/Confman.Tests/CommandTests.cs`, `LiteDbConfigStoreTests.cs`
- CLAUDE.md: Audit Event Structure section documents the JSON schema

### Design Patterns

- Value Object pattern (DDD)
- Smart Enum pattern (type-safe alternatives to string constants)
- Custom JsonConverter for transparent serialization
