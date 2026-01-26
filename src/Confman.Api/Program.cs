using System.Diagnostics.CodeAnalysis;
using Confman.Api.Auth;
using Confman.Api.Cluster;
using Confman.Api.Middleware;
using Confman.Api.Storage;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Serilog;

[assembly: Experimental("DOTNEXT001")]

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Get the URL this instance will listen on (for cluster identity)
    // Only set publicEndPoint if not already configured (allows command-line override)
    if (string.IsNullOrEmpty(builder.Configuration["publicEndPoint"]))
    {
        var urls = builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000";
        builder.Configuration["publicEndPoint"] = urls.Split(';')[0];
    }

    // Use port-specific data directory to isolate cluster nodes
    var publicEndPoint = builder.Configuration["publicEndPoint"]!;
    var port = new Uri(publicEndPoint).Port;
    builder.Configuration["Storage:DataPath"] = $"./data-{port}";

    // Configure Serilog
    builder.Host.UseSerilog((context, config) => config
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "Confman")
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

    // Register storage
    builder.Services.AddSingleton<IConfigStore, LiteDbConfigStore>();

    // Register cluster services
    builder.Services.AddSingleton<IClusterMemberLifetime, ClusterLifetime>();
    builder.Services.AddScoped<IRaftService, RaftService>();

    // Configure WriteAheadLog options for the state machine
    var dataPath = builder.Configuration["Storage:DataPath"] ?? "./data";
    var walPath = Path.Combine(dataPath, "raft-log");
    builder.Services.AddSingleton(new DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog.Options
    {
        Location = walPath
    });

    // Register state machine for Raft log replication
    builder.Services.UseStateMachine<ConfigStateMachine>();

    // Configure cluster membership from configuration
    var membersConfig = builder.Configuration.GetSection("members").Get<string[]>() ?? [];
    if (membersConfig.Length > 0)
    {
        builder.Services.UseInMemoryConfigurationStorage(members =>
        {
            foreach (var member in membersConfig)
            {
                if (Uri.TryCreate(member, UriKind.Absolute, out var uri))
                {
                    members.Add(new UriEndPoint(uri));
                }
            }
        });
    }

    // Configure Raft cluster with HTTP transport
    builder.JoinCluster();

    // Configure authentication and authorization
    builder.Services.AddConfmanAuth(builder.Configuration);

    // Configure controllers with JSON options
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.WriteIndented = false;
        });

    // Add API documentation
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "Confman API",
            Version = "v1",
            Description = "Distributed configuration management service"
        });

        options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            Name = "X-Api-Key",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "API key for authentication"
        });

        options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "ApiKey"
                    }
                },
                Array.Empty<string>()
            }
        });
    });

    // Configure RFC 7807 Problem Details
    builder.Services.AddProblemDetails();

    // Add CORS for dashboard
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Dashboard", policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    var app = builder.Build();

    // IMPORTANT: Consensus protocol handler must come BEFORE authentication
    app.UseConsensusProtocolHandler();

    // Correlation ID for request tracing
    app.UseCorrelationId();

    // CORS for dashboard (running on separate port)
    app.UseCors("Dashboard");

    // Swagger UI (enabled for all environments - internal service)
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Confman API v1"));

    app.UseHttpsRedirection();

    // Authentication and Authorization
    app.UseAuthentication();
    app.UseAuthorization();

    // Health endpoints (no auth required)
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
        .AllowAnonymous();

    // List all configs (for dashboard)
    app.MapGet("/api/v1/configs", async (IConfigStore store, CancellationToken ct) =>
    {
        var configs = await store.ListAllAsync(ct);
        var result = configs.Select(c => new
        {
            ns = c.Namespace,
            key = c.Key,
            value = c.Value,
            type = c.Type,
            version = c.Version,
            updatedAt = c.UpdatedAt,
            updatedBy = c.UpdatedBy
        });

        return Results.Ok(result);
    }).AllowAnonymous();

    app.MapGet("/health/ready", (IRaftCluster cluster) =>
    {
        var leader = cluster.Leader;
        var isReady = leader is not null;
        var isLeader = !cluster.LeadershipToken.IsCancellationRequested;

        var response = new
        {
            status = isReady ? "ready" : "not_ready",
            timestamp = DateTimeOffset.UtcNow,
            cluster = new
            {
                role = isLeader ? "leader" : "follower",
                leaderKnown = leader is not null,
                leader = leader?.EndPoint?.ToString(),
                term = cluster.Term
            }
        };

        return isReady
            ? Results.Ok(response)
            : Results.Json(response, statusCode: 503);
    }).AllowAnonymous();

    app.MapControllers();

    // Restore state machine from WAL before starting
    await app.RestoreStateAsync<ConfigStateMachine>(CancellationToken.None);

    Log.Information("Starting Confman on {Urls}", string.Join(", ", app.Urls));
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Required for WebApplicationFactory testing
public partial class Program { }