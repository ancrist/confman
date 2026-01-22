---
title: "feat: Implement Confman Distributed Configuration Service"
type: feat
date: 2026-01-23
revised: 2026-01-23 (simplified based on review feedback)
---

# Implement Confman Distributed Configuration Service

## Overview

Build a distributed configuration service using Raft consensus. Start simple, iterate based on real user needs.

**Key Technologies:**
- .NET 10 / ASP.NET Core
- DotNext.AspNetCore.Cluster 5.26.1 (Raft consensus)
- LiteDB (storage — pure .NET, no native dependencies)

**Scale Target:** 100K+ keys, 100-200 req/sec, 70-90% reads

## Design Principles (From Review)

1. **Ship something small, iterate based on feedback**
2. **YAGNI** — Don't build features until someone asks for them
3. **Two projects max** — Extract libraries only when boundaries are proven
4. **Trust DotNext** — Don't re-test consensus; test your code

## What's Deferred to v2

Based on reviewer feedback, these features are explicitly **NOT** in v1:

| Feature | Why Deferred |
|---------|--------------|
| Draft/Publish workflow | Governance before users |
| Schema validation | No users need it yet |
| Webhooks | No integration requirement exists |
| Namespace inheritance | Flat is simpler |
| YAML/raw content negotiation | JSON is sufficient |
| 4-role RBAC | Two roles cover 95% of use cases |
| RocksDB | LiteDB handles 100K keys trivially |
| Chaos tests | Trust DotNext for consensus correctness |
| OpenTelemetry + Prometheus | Structured logging is enough for v1 |

## Proposed Solution

Single Raft cluster where all nodes run identical code. Direct writes (no draft state). Two roles.

```
┌─────────────────────────────────────────────────────────┐
│                    Load Balancer                        │
└─────────────────────┬───────────────────────────────────┘
                      │
        ┌─────────────┼─────────────┐
        ▼             ▼             ▼
   ┌─────────┐   ┌─────────┐   ┌─────────┐
   │ Node 1  │   │ Node 2  │   │ Node 3  │
   │ (Leader)│◄─►│(Follower)◄─►│(Follower)│
   ├─────────┤   ├─────────┤   ├─────────┤
   │ REST API│   │ REST API│   │ REST API│
   │ Raft    │   │ Raft    │   │ Raft    │
   │ LiteDB  │   │ LiteDB  │   │ LiteDB  │
   └─────────┘   └─────────┘   └─────────┘
```

---

## Technical Approach

### Project Structure (2 Projects)

```
Confman.sln
src/
└── Confman.Api/
    ├── Confman.Api.csproj
    ├── Program.cs
    ├── appsettings.json
    │
    ├── Models/
    │   ├── Namespace.cs
    │   ├── ConfigEntry.cs
    │   └── AuditEvent.cs
    │
    ├── Cluster/
    │   ├── ClusterLifetime.cs
    │   ├── ConfigStateMachine.cs
    │   └── Commands/
    │       ├── ICommand.cs
    │       ├── SetConfigCommand.cs
    │       └── DeleteConfigCommand.cs
    │
    ├── Storage/
    │   ├── IConfigStore.cs
    │   └── LiteDbConfigStore.cs
    │
    ├── Auth/
    │   ├── ApiKeyAuthHandler.cs
    │   └── PermissionRequirements.cs
    │
    └── Controllers/
        ├── ConfigController.cs
        ├── NamespacesController.cs
        ├── AuditController.cs
        └── HealthController.cs

tests/
└── Confman.Tests/
    ├── Confman.Tests.csproj
    ├── ClusterTests.cs
    ├── ConfigControllerTests.cs
    └── AuthTests.cs
```

### Implementation Phases

---

#### Phase 1: Raft Cluster Foundation (Week 1)

**Goal:** 3-node cluster that forms, elects leader, and stays healthy.

##### Tasks

- [ ] Create solution with 2 projects
- [ ] Configure DotNext.AspNetCore.Cluster
- [ ] Implement `ClusterLifetime` for event handling
- [ ] Add health endpoints (`/health`, `/health/ready`)
- [ ] Write cluster formation test

##### Deliverables

**1.1 Program.cs**

```csharp
// src/Confman.Api/Program.cs
var builder = WebApplication.CreateBuilder(args);

// Raft cluster
builder.Services.AddSingleton<IClusterMemberLifetime, ClusterLifetime>();
builder.Services.AddSingleton<IConfigStore, LiteDbConfigStore>();
builder.JoinCluster();

// Auth
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>("ApiKey", null);
builder.Services.AddAuthorization();

builder.Services.AddControllers();

var app = builder.Build();

app.UseConsensusProtocolHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", (IRaftCluster cluster) =>
{
    var ready = cluster.Leader is not null;
    return ready
        ? Results.Ok(new { status = "ready", leader = cluster.Leader?.EndPoint.ToString() })
        : Results.StatusCode(503);
});

app.Run();
```

**1.2 Cluster Configuration**

```json
{
  "Cluster": {
    "members": [
      "http://localhost:5000",
      "http://localhost:5001",
      "http://localhost:5002"
    ],
    "lowerElectionTimeout": 150,
    "upperElectionTimeout": 300,
    "requestTimeout": 5000,
    "coldStart": false
  },
  "Storage": {
    "DataPath": "./data"
  },
  "Auth": {
    "ApiKeys": [
      {
        "Key": "confman_dev_abc123",
        "Name": "Development",
        "Principal": "dev@local",
        "Role": "admin"
      }
    ]
  }
}
```

**1.3 Cluster Lifetime**

```csharp
// src/Confman.Api/Cluster/ClusterLifetime.cs
public sealed class ClusterLifetime : IClusterMemberLifetime
{
    private readonly ILogger<ClusterLifetime> _logger;

    public ClusterLifetime(ILogger<ClusterLifetime> logger) => _logger = logger;

    public void Initialize(IRaftCluster cluster, IDictionary<string, string> metadata)
    {
        cluster.LeaderChanged += (_, leader) =>
            _logger.LogInformation("Leader changed to: {Leader}", leader?.EndPoint);
    }

    public void Shutdown(IRaftCluster cluster)
        => _logger.LogInformation("Node shutting down");
}
```

##### Success Criteria

- [ ] 3 nodes form cluster within 5 seconds
- [ ] Leader election works after leader kill
- [ ] `/health/ready` returns 503 when no leader

---

#### Phase 2: KV Storage & State Machine (Week 2)

**Goal:** Writes replicated via Raft, reads from local LiteDB.

##### Tasks

- [ ] Implement `LiteDbConfigStore`
- [ ] Implement `ConfigStateMachine` (decoupled from store)
- [ ] Implement `SetConfigCommand` and `DeleteConfigCommand`
- [ ] Add write commit verification (critical fix from review)
- [ ] Write replication tests

##### Deliverables

**2.1 Config Store Interface (Decoupled)**

```csharp
// src/Confman.Api/Storage/IConfigStore.cs
public interface IConfigStore
{
    Task<ConfigEntry?> GetAsync(string ns, string key, CancellationToken ct = default);
    Task<IReadOnlyList<ConfigEntry>> ListAsync(string ns, CancellationToken ct = default);
    Task SetAsync(ConfigEntry entry, CancellationToken ct = default);
    Task DeleteAsync(string ns, string key, CancellationToken ct = default);
    Task AppendAuditAsync(AuditEvent evt, CancellationToken ct = default);
}
```

**2.2 State Machine (Decoupled from IConfigStore)**

```csharp
// src/Confman.Api/Cluster/ConfigStateMachine.cs
public sealed class ConfigStateMachine : PersistentState
{
    private readonly IConfigStore _store;
    private readonly ILogger<ConfigStateMachine> _logger;

    public ConfigStateMachine(
        IConfigStore store,
        ILogger<ConfigStateMachine> logger,
        string path,
        int recordsPerPartition = 50)
        : base(path, recordsPerPartition)
    {
        _store = store;
        _logger = logger;
    }

    protected override async ValueTask ApplyAsync(LogEntry entry)
    {
        var command = await DeserializeCommandAsync(entry);
        await command.ApplyAsync(_store);
        _logger.LogDebug("Applied command: {Type}", command.GetType().Name);
    }

    private static async ValueTask<ICommand> DeserializeCommandAsync(LogEntry entry)
    {
        using var stream = new MemoryStream();
        await entry.WriteToAsync(stream);
        stream.Position = 0;
        return JsonSerializer.Deserialize<ICommand>(stream)
            ?? throw new InvalidOperationException("Failed to deserialize command");
    }
}
```

**2.3 Command Interface**

```csharp
// src/Confman.Api/Cluster/Commands/ICommand.cs
[JsonDerivedType(typeof(SetConfigCommand), "set")]
[JsonDerivedType(typeof(DeleteConfigCommand), "delete")]
public interface ICommand
{
    Task ApplyAsync(IConfigStore store);
}
```

**2.4 Set Config Command**

```csharp
// src/Confman.Api/Cluster/Commands/SetConfigCommand.cs
public sealed record SetConfigCommand : ICommand
{
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public required string Type { get; init; }
    public required string Author { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public async Task ApplyAsync(IConfigStore store)
    {
        var entry = new ConfigEntry
        {
            Namespace = Namespace,
            Key = Key,
            Value = Value,
            Type = Type,
            Version = 1, // Will be incremented by store if exists
            UpdatedAt = Timestamp,
            UpdatedBy = Author
        };

        await store.SetAsync(entry);

        await store.AppendAuditAsync(new AuditEvent
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = Timestamp,
            Action = "config.set",
            Actor = Author,
            Namespace = Namespace,
            Key = Key,
            NewValue = Value
        });
    }
}
```

**2.5 LiteDB Store**

```csharp
// src/Confman.Api/Storage/LiteDbConfigStore.cs
public sealed class LiteDbConfigStore : IConfigStore, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ConfigEntry> _configs;
    private readonly ILiteCollection<AuditEvent> _audit;

    public LiteDbConfigStore(IConfiguration config)
    {
        var path = config["Storage:DataPath"] ?? "./data";
        Directory.CreateDirectory(path);
        _db = new LiteDatabase(Path.Combine(path, "confman.db"));
        _configs = _db.GetCollection<ConfigEntry>("configs");
        _audit = _db.GetCollection<AuditEvent>("audit");

        _configs.EnsureIndex(x => x.Namespace);
        _configs.EnsureIndex(x => x.Key);
    }

    public Task<ConfigEntry?> GetAsync(string ns, string key, CancellationToken ct = default)
    {
        var entry = _configs.FindOne(x => x.Namespace == ns && x.Key == key);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<ConfigEntry>> ListAsync(string ns, CancellationToken ct = default)
    {
        var entries = _configs.Find(x => x.Namespace == ns).ToList();
        return Task.FromResult<IReadOnlyList<ConfigEntry>>(entries);
    }

    public Task SetAsync(ConfigEntry entry, CancellationToken ct = default)
    {
        var existing = _configs.FindOne(x => x.Namespace == entry.Namespace && x.Key == entry.Key);
        if (existing is not null)
        {
            entry.Version = existing.Version + 1;
            entry.Id = existing.Id;
            _configs.Update(entry);
        }
        else
        {
            entry.Version = 1;
            _configs.Insert(entry);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string ns, string key, CancellationToken ct = default)
    {
        _configs.DeleteMany(x => x.Namespace == ns && x.Key == key);
        return Task.CompletedTask;
    }

    public Task AppendAuditAsync(AuditEvent evt, CancellationToken ct = default)
    {
        _audit.Insert(evt);
        return Task.CompletedTask;
    }

    public void Dispose() => _db.Dispose();
}
```

##### Success Criteria

- [ ] Write on leader replicates to followers
- [ ] Write returns success ONLY after Raft commit (critical)
- [ ] Node restart recovers state from log
- [ ] Reads work from any node

---

#### Phase 3: REST API & Auth (Week 3)

**Goal:** Full CRUD API with two-role authentication.

##### Tasks

- [ ] Implement API key authentication (2 roles: reader, admin)
- [ ] Build ConfigController with write commit verification
- [ ] Build NamespacesController
- [ ] Build AuditController
- [ ] Add RFC 7807 error handling
- [ ] Write API tests

##### Deliverables

**3.1 Models**

```csharp
// src/Confman.Api/Models/ConfigEntry.cs
public class ConfigEntry
{
    public ObjectId Id { get; set; }
    public required string Namespace { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public required string Type { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}

// src/Confman.Api/Models/Namespace.cs
public class Namespace
{
    public ObjectId Id { get; set; }
    public required string Path { get; set; }
    public string? Description { get; set; }
    public required string Owner { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// src/Confman.Api/Models/AuditEvent.cs
public class AuditEvent
{
    public required string Id { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public required string Action { get; set; }
    public required string Actor { get; set; }
    public required string Namespace { get; set; }
    public string? Key { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
```

**3.2 API Key Auth (2 Roles)**

```csharp
// src/Confman.Api/Auth/ApiKeyAuthHandler.cs
public class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    private readonly IConfiguration _config;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration config)
        : base(options, logger, encoder)
    {
        _config = config;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader))
            return Task.FromResult(AuthenticateResult.Fail("Missing X-API-Key header"));

        var apiKey = apiKeyHeader.ToString();
        var keys = _config.GetSection("Auth:ApiKeys").Get<List<ApiKeyConfig>>() ?? [];
        var keyConfig = keys.FirstOrDefault(k => k.Key == apiKey);

        if (keyConfig is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, keyConfig.Principal),
            new Claim(ClaimTypes.Role, keyConfig.Role) // "reader" or "admin"
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

public class ApiKeyConfig
{
    public required string Key { get; set; }
    public required string Name { get; set; }
    public required string Principal { get; set; }
    public required string Role { get; set; } // "reader" or "admin"
}
```

**3.3 Config Controller (With Write Commit Verification)**

```csharp
// src/Confman.Api/Controllers/ConfigController.cs
[ApiController]
[Route("api/v1/config")]
[Authorize]
public class ConfigController : ControllerBase
{
    private readonly IRaftCluster _cluster;
    private readonly IConfigStore _store;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(IRaftCluster cluster, IConfigStore store, ILogger<ConfigController> logger)
    {
        _cluster = cluster;
        _store = store;
        _logger = logger;
    }

    [HttpGet("{ns}/{key}")]
    public async Task<IActionResult> Get(string ns, string key, CancellationToken ct)
    {
        var entry = await _store.GetAsync(ns, key, ct);
        if (entry is null)
            return NotFound(new ProblemDetails
            {
                Type = "https://confman/errors/not-found",
                Title = "Config Not Found",
                Status = 404,
                Detail = $"No config '{key}' in namespace '{ns}'"
            });

        return Ok(entry);
    }

    [HttpGet("{ns}")]
    public async Task<IActionResult> List(string ns, CancellationToken ct)
    {
        var entries = await _store.ListAsync(ns, ct);
        return Ok(new { items = entries });
    }

    [HttpPut("{ns}/{key}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Set(string ns, string key, [FromBody] SetConfigRequest request, CancellationToken ct)
    {
        // Forward to leader if not leader
        if (!_cluster.LeadershipToken.IsCancellationRequested && _cluster.Leader?.EndPoint != _cluster.LocalMember?.EndPoint)
        {
            var leader = _cluster.Leader;
            if (leader is null)
                return StatusCode(503, new ProblemDetails
                {
                    Type = "https://confman/errors/no-leader",
                    Title = "No Leader",
                    Status = 503,
                    Detail = "Cluster has no leader. Try again."
                });

            return RedirectPreserveMethod($"{leader.EndPoint}/api/v1/config/{ns}/{key}");
        }

        var command = new SetConfigCommand
        {
            Namespace = ns,
            Key = key,
            Value = request.Value,
            Type = request.Type ?? "string",
            Author = User.Identity?.Name ?? "unknown",
            Timestamp = DateTimeOffset.UtcNow
        };

        // CRITICAL: Wait for Raft commit before returning success
        var committed = await ReplicateAndWaitAsync(command, ct);
        if (!committed)
        {
            return StatusCode(503, new ProblemDetails
            {
                Type = "https://confman/errors/replication-failed",
                Title = "Replication Failed",
                Status = 503,
                Detail = "Write could not be replicated to quorum"
            });
        }

        var entry = await _store.GetAsync(ns, key, ct);
        return Ok(entry);
    }

    [HttpDelete("{ns}/{key}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(string ns, string key, CancellationToken ct)
    {
        if (_cluster.Leader?.EndPoint != _cluster.LocalMember?.EndPoint)
        {
            var leader = _cluster.Leader;
            if (leader is null)
                return StatusCode(503, new ProblemDetails { Title = "No Leader", Status = 503 });
            return RedirectPreserveMethod($"{leader.EndPoint}/api/v1/config/{ns}/{key}");
        }

        var command = new DeleteConfigCommand
        {
            Namespace = ns,
            Key = key,
            Author = User.Identity?.Name ?? "unknown",
            Timestamp = DateTimeOffset.UtcNow
        };

        var committed = await ReplicateAndWaitAsync(command, ct);
        if (!committed)
            return StatusCode(503, new ProblemDetails { Title = "Replication Failed", Status = 503 });

        return NoContent();
    }

    private async Task<bool> ReplicateAndWaitAsync(ICommand command, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(command);
            var entry = new BinaryLogEntry(json);

            // Append to log and wait for commit
            var result = await _cluster.ReplicateAsync(entry, ct);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replication failed");
            return false;
        }
    }
}

public record SetConfigRequest(string Value, string? Type);
```

**3.4 Audit Controller**

```csharp
// src/Confman.Api/Controllers/AuditController.cs
[ApiController]
[Route("api/v1/audit")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly LiteDatabase _db;

    public AuditController(IConfigStore store)
    {
        // Access LiteDB directly for audit queries
        _db = ((LiteDbConfigStore)store).Database;
    }

    [HttpGet("{ns}")]
    public IActionResult GetAudit(
        string ns,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null)
    {
        var audit = _db.GetCollection<AuditEvent>("audit");

        var query = audit.Query()
            .Where(x => x.Namespace == ns)
            .OrderByDescending(x => x.Timestamp)
            .Limit(limit + 1);

        var events = query.ToList();
        var hasMore = events.Count > limit;
        if (hasMore) events = events.Take(limit).ToList();

        return Ok(new
        {
            items = events,
            pagination = new
            {
                limit,
                hasMore,
                nextCursor = hasMore ? events.Last().Id : null
            }
        });
    }
}
```

##### Success Criteria

- [ ] `GET /api/v1/config/{ns}/{key}` returns config
- [ ] `PUT` only succeeds after Raft commit
- [ ] `PUT` on follower redirects to leader
- [ ] Unauthorized requests return 401
- [ ] Reader role cannot write

---

#### Phase 4: Polish & Testing (Week 4)

**Goal:** Production-ready with proper logging and tests.

##### Tasks

- [ ] Add structured logging (Serilog)
- [ ] Add request correlation IDs
- [ ] Write integration tests (3-node cluster)
- [ ] Document API (basic OpenAPI)
- [ ] Create deployment guide

##### Deliverables

**4.1 Serilog Setup**

```csharp
// In Program.cs
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Confman")
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));
```

**4.2 Integration Test**

```csharp
// tests/Confman.Tests/ClusterTests.cs
public class ClusterTests : IAsyncLifetime
{
    private readonly List<WebApplication> _nodes = new();

    public async Task InitializeAsync()
    {
        // Start 3 nodes
        for (int i = 0; i < 3; i++)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["Cluster:members:0"] = "http://localhost:5000";
            builder.Configuration["Cluster:members:1"] = "http://localhost:5001";
            builder.Configuration["Cluster:members:2"] = "http://localhost:5002";
            // ... configure node
            var app = builder.Build();
            _nodes.Add(app);
        }

        await Task.WhenAll(_nodes.Select(n => n.StartAsync()));
        await Task.Delay(5000); // Wait for leader election
    }

    [Fact]
    public async Task Cluster_ShouldElectLeader()
    {
        var client = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(body.GetProperty("leader").GetString());
    }

    [Fact]
    public async Task Write_ShouldReplicateToAllNodes()
    {
        var client = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
        client.DefaultRequestHeaders.Add("X-API-Key", "confman_dev_abc123");

        // Write to leader
        await client.PutAsJsonAsync("/api/v1/config/test/key1", new { value = "hello" });

        // Read from each node
        foreach (var port in new[] { 5000, 5001, 5002 })
        {
            var nodeClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            nodeClient.DefaultRequestHeaders.Add("X-API-Key", "confman_dev_abc123");

            var response = await nodeClient.GetAsync("/api/v1/config/test/key1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_nodes.Select(n => n.StopAsync()));
    }
}
```

##### Success Criteria

- [ ] All tests pass
- [ ] Logs include correlation IDs
- [ ] OpenAPI spec generated at `/swagger`
- [ ] README has deployment instructions

---

## API Endpoints (v1)

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/v1/config/{ns}/{key}` | reader | Read config value |
| `GET` | `/api/v1/config/{ns}` | reader | List configs in namespace |
| `PUT` | `/api/v1/config/{ns}/{key}` | admin | Set config value |
| `DELETE` | `/api/v1/config/{ns}/{key}` | admin | Delete config |
| `GET` | `/api/v1/audit/{ns}` | reader | Query audit log |
| `GET` | `/health` | none | Liveness check |
| `GET` | `/health/ready` | none | Readiness (has leader) |

**Total: 7 endpoints**

---

## Acceptance Criteria

### Functional

- [ ] 3-node cluster forms and elects leader
- [ ] Config CRUD works correctly
- [ ] Writes only succeed after Raft commit
- [ ] Audit log captures all mutations
- [ ] Two roles enforced (reader, admin)

### Non-Functional

- [ ] Leader election < 5 seconds
- [ ] Read latency < 10ms
- [ ] Write latency < 100ms (including replication)

---

## NuGet Packages

```xml
<PackageReference Include="DotNext.AspNetCore.Cluster" Version="5.26.1" />
<PackageReference Include="LiteDB" Version="5.*" />
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
```

---

## What's Next (v2 Candidates)

When users ask for these, add them:

1. **Draft/Publish workflow** — When governance is required
2. **Schema validation** — When config errors become a problem
3. **Webhooks** — When integrations are needed
4. **Namespace inheritance** — When hierarchy is needed
5. **More RBAC roles** — When two roles aren't enough
6. **Metrics/tracing** — When debugging production issues

---

## References

### Brainstorms (Decisions Made)

- [System Architecture](../brainstorms/2026-01-23-system-architecture-brainstorm.md)
- [Data Model](../brainstorms/2026-01-23-data-model-brainstorm.md)
- [Publishing Workflow](../brainstorms/2026-01-23-publishing-workflow-brainstorm.md) — Deferred
- [API Design](../brainstorms/2026-01-23-api-design-brainstorm.md)

### External

- [DotNext ASP.NET Core Cluster](https://sakno.github.io/dotNext/features/cluster/aspnetcore.html)
- [DotNext Raft](https://sakno.github.io/dotNext/features/cluster/raft.html)
- [LiteDB Documentation](https://www.litedb.org/docs/)

---

## Estimated Timeline

| Phase | Duration | Deliverable |
|-------|----------|-------------|
| 1. Raft Foundation | Week 1 | Cluster forms, health endpoints |
| 2. Storage | Week 2 | KV operations with replication |
| 3. API & Auth | Week 3 | Full REST API, 2 roles |
| 4. Polish | Week 4 | Tests, logging, docs |

**Total: 4 weeks to production-ready v1**
