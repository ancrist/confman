using Confman.Api.Auth;
using Confman.Api.Cluster;
using Confman.Api.Middleware;
using Confman.Api.Storage;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

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

    var app = builder.Build();

    // IMPORTANT: Consensus protocol handler must come BEFORE authentication
    app.UseConsensusProtocolHandler();

    // Correlation ID for request tracing
    app.UseCorrelationId();

    // Swagger UI in development
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Confman API v1"));
    }

    app.UseHttpsRedirection();

    // Authentication and Authorization
    app.UseAuthentication();
    app.UseAuthorization();

    // Health endpoints (no auth required)
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
        .AllowAnonymous();

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

    Log.Information("Starting Confman on {Urls}", string.Join(", ", app.Urls));
    app.Run();
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
