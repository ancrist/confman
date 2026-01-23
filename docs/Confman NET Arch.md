# ConfMan Architecture & Design Patterns (C#)

## Executive Summary

ConfMan is a .NET ASP.NET Core Web API that applies Clean Architecture with CQRS, MediatR, and DDD. The design emphasizes strict boundaries, testability, and clear separation of concerns.

## Architecture Patterns

### Clean Architecture (Onion)

Layered dependency flow (inward only):

```
Infrastructure -> Application -> Domain
```

Logical layout:

```
┌──────────────────────────────────────────┐
│ ConfMan.Web (Presentation)               │
│ - Controllers (HTTP entry points)        │
│ - Middleware pipeline                    │
└──────────────────┬───────────────────────┘
                   │ depends on
┌──────────────────▼───────────────────────┐
│ ConfMan.Application                      │
│ - Commands & Queries (CQRS)              │
│ - MediatR handlers                       │
└──────────────────┬───────────────────────┘
                   │ depends on
┌──────────────────▼───────────────────────┐
│ ConfMan.Domain                           │
│ - Business logic                         │
│ - Entities, aggregates, value objects    │
│ - Domain services                        │
└──────────────────────────────────────────┘

┌──────────────────────────────────────────┐
│ ConfMan.Infrastructure                   │
│ - External dependencies                  │
│ - Data access (repositories)             │
└──────────────────────────────────────────┘
```

Layer structure:
- Domain: `ConfMan.Domain`
- Application: `ConfMan.Application`
- Contracts: `ConfMan.Contracts`
- Infrastructure: `ConfMan.Infrastructure`
- Persistence: `ConfMan.Persistence`
- Common: `ConfMan.Common`
- Web: `ConfMan.Web`
- Provisioning: `ConfMan.Provisioning`

## Patterns Applied

### 1) CQRS (Command Query Responsibility Segregation)

```csharp
// Query (read)
public async Task<ActionResult<GetLabelValueResult>> GetLabelValue(...)
{
    var result = await _mediator.Send(new GetLabelValueQuery { ... });
    return Ok(result);
}

// Command (write)
public async Task<ActionResult> UpsertServiceNamespace(...)
{
    await _mediator.Send(new UpsertServiceNamespaceCommand { ... });
    return Ok();
}
```

### 2) Mediator (MediatR)

```csharp
private readonly IMediator _mediator;

public async Task<ActionResult> GetData(...)
{
    return Ok(await _mediator.Send(new GetDataQuery { ... }));
}
```

### 3) Repository

```csharp
// Domain abstraction
IRepository<TEntity>

// Infrastructure implementation
Repository<TEntity> : IRepository<TEntity>

// Registration
services.AddConfManRepositories();
```

### 4) Dependency Injection

```csharp
// Lifetimes
services.AddSingleton<IWebControllerMetrics, WebControllerMetrics>();
services.AddScoped<IUserContext, UserContext>();
services.AddTransient<IConfigureOptions<T>, ConfigureOptions>();

// Constructor injection
public class MyController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger _logger;

    public MyController(IMediator mediator, ILogger logger)
    {
        _mediator = mediator;
        _logger = logger;
    }
}
```

### 5) Middleware Pipeline

```csharp
app
    .UseResponseCompression()
    .UseMiddleware<UnhandledExceptionHandlerMiddleware>()
    .UseMiddleware<RequestResponseLoggingMiddleware>()
    .UseRouting()
    .UseAuthentication()
    .UseAuthorization()
    .UseEndpoints(endpoints => endpoints.MapControllers())
    .UseResponseCaching();
```

### 6) Options Pattern (Configuration)

```csharp
services.Configure<ErrorRegistryConfiguration>(
    Configuration.GetSection("ErrorRegistry")
);

public class MyService
{
    public MyService(IOptions<ErrorRegistryConfiguration> options)
    {
        var config = options.Value;
    }
}
```

### 7) Factory

```csharp
services.AddPersistenceProvider(
    Configuration.GetValue<PersistenceProviders>(ConfigKeys.PERSISTENCE_PROVIDER)
);
```

### 8) Strategy

```csharp
services.AddConfManAuthentication(
    Configuration,
    ConfManAuthType.ConfMan,
    ConfManAuthType.DSTS,
    ConfManAuthType.APPKI
);
```

### 9) Action Filter

```csharp
services.AddScoped<ApiMetricsAttribute>();
services.AddControllers(options =>
    options.Filters.AddService<ApiMetricsAttribute>()
);

[ApiMetrics]
public class MyController : ControllerBase { }
```

### 10) Builder (Fluent Configuration)

```csharp
services
    .AddConfManDomainResolvers()
    .AddConfManErrorTraceability()
    .AddPersistenceProvider(persistenceProvider)
    .AddConfManRepositories()
    .AddConfManValueObjectBuilders(Configuration)
    .RegisterConfManWithMediatR();
```

### 11) Adapter

```csharp
public class ConfManClaimsMapper : IClaimsMapper
{
    public InternalClaims Map(ExternalClaims claims) { ... }
}
```

### 12) Chain of Responsibility

```csharp
public class CustomMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context)
    {
        // Pre-processing
        await _next(context);
        // Post-processing
    }
}
```

## SOLID Principles (Examples)

### Single Responsibility Principle

```csharp
public class ServicesReadController : ControllerBase
{
    public async Task<ActionResult> Get(...)
    {
        var result = await _mediator.Send(new Query());
        return Ok(result);
    }
}
```

### Open/Closed Principle

```csharp
mapper.AddExceptionDetails(typeof(AuthenticationException), HttpStatusCode.Unauthorized);
mapper.AddExceptionDetails(typeof(SecurityException), HttpStatusCode.Forbidden);
```

### Dependency Inversion Principle

```csharp
private readonly IMediator _mediator;
private readonly ILogger _logger;
```

### Interface Segregation Principle

```csharp
IRequest<GetLabelValueResult>
IRequest<GetAllServiceNamespacesResult>
```

### Liskov Substitution Principle

```csharp
public class ServicesReadController : ControllerBase { }
```

## Pattern Usage Quick Reference

| Pattern | Typical Use |
| --- | --- |
| CQRS | Application layer operations |
| Mediator | Decouple controllers from handlers |
| Repository | Data access abstraction |
| DI | Service composition |
| Middleware | Cross-cutting concerns |
| Options | Configuration binding |
| Factory | Dynamic implementation selection |
| Strategy | Pluggable behaviors |
| Action Filter | Declarative cross-cutting concerns |
| Builder | Fluent service registration |
| Adapter | External model translation |
| Chain of Responsibility | Sequential request handling |

## Exception Handling Patterns

```csharp
throw new ConfManException("Domain error");
throw new InvalidDataStateException("Data consistency issue");
throw new DomainInvariantException("Business rule violation");

try
{
    var result = await _mediator.Send(query);
    return Ok(result);
}
catch (ConfManException ex)
{
    _logger.LogError(ex, "Domain error occurred");
    return Problem(statusCode: 400, detail: ex.Message);
}
```

## Logging & Telemetry Patterns

```csharp
_logger.LogDebug("Processing request for {ServiceNamespace}", serviceNamespace);

Activity.Current?.SetTag("namespace", serviceNamespace);
Activity.Current?.AddBaggage("namespace", serviceNamespace);

_meter.CreateCounter<int>("requests_processed").Add(1);
```
