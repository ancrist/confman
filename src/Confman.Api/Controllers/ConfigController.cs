using System.Diagnostics;
using System.Security.Claims;
using Confman.Api.Cluster;
using Confman.Api.Cluster.Commands;
using Confman.Api.Models;
using Confman.Api.Services;
using Confman.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Confman.Api.Controllers;

/// <summary>
/// API controller for configuration entries.
/// </summary>
[ApiController]
[Route("api/v1/namespaces/{namespace}/config")]
[Authorize]
public class ConfigController : ControllerBase
{
    private readonly IConfigStore _store;
    private readonly IRaftService _raft;
    private readonly IConfigWriteService _writeService;
    private readonly IBlobValueResolver _blobResolver;
    private readonly ILogger<ConfigController> _logger;
    private readonly bool _logConfigChanges;

    public ConfigController(
        IConfigStore store,
        IRaftService raft,
        IConfigWriteService writeService,
        IBlobValueResolver blobResolver,
        ILogger<ConfigController> logger,
        IConfiguration configuration)
    {
        _store = store;
        _raft = raft;
        _writeService = writeService;
        _blobResolver = blobResolver;
        _logger = logger;
        _logConfigChanges = configuration.GetValue<bool>("Api:LogConfigChanges", false);
    }

    /// <summary>
    /// List all configuration entries in a namespace.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "ReadOnly")]
    [ProducesResponseType(typeof(IEnumerable<ConfigEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromRoute(Name = "namespace")] string ns,
        CancellationToken ct)
    {
        _logger.LogDebug("Listing configs in namespace: {Namespace}", ns);

        var entries = await _store.ListAsync(ns, ct);
        var dtos = entries.Select(e => ConfigEntryDto.FromModel(e));

        return Ok(dtos);
    }

    /// <summary>
    /// Get a specific configuration entry.
    /// </summary>
    [HttpGet("{key}")]
    [Authorize(Policy = "ReadOnly")]
    [ProducesResponseType(typeof(ConfigEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "namespace")] string ns,
        string key,
        CancellationToken ct)
    {
        _logger.LogDebug("Getting config {Namespace}/{Key}", ns, key);

        var entry = await _store.GetAsync(ns, key, ct);
        if (entry is null)
        {
            return Problem(
                title: "Configuration not found",
                detail: $"No configuration entry found with key '{key}' in namespace '{ns}'",
                statusCode: StatusCodes.Status404NotFound);
        }

        var resolvedValue = await _blobResolver.ResolveAsync(entry, ct);
        if (resolvedValue is null)
        {
            return Problem(
                title: "Blob temporarily unavailable",
                detail: $"The value for '{key}' in namespace '{ns}' is stored as a blob that is currently unavailable",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Ok(ConfigEntryDto.FromModel(entry, resolvedValue));
    }

    /// <summary>
    /// Create or update a configuration entry.
    /// </summary>
    [HttpPut("{key}")]
    [Authorize(Policy = "Write")]
    [ProducesResponseType(typeof(ConfigEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Set(
        [FromRoute(Name = "namespace")] string ns,
        string key,
        [FromBody] SetConfigRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Forward to leader if we're not the leader
        if (!_raft.IsLeader)
        {
            return await ForwardToLeaderAsync(ct);
        }

        var author = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        if (_logConfigChanges)
        {
            _logger.LogInformation("Setting config {Namespace}/{Key} by {Author}", ns, key, author);
        }

        var result = await _writeService.WriteAsync(ns, key, request.Value, request.Type ?? "string", author, ct);
        if (!result.Success)
        {
            _logger.LogWarning("Set config {Namespace}/{Key} failed ({ElapsedMs} ms): {Detail}",
                ns, key, sw.ElapsedMilliseconds, result.ErrorDetail);
            return Problem(
                title: "Replication failed",
                detail: result.ErrorDetail ?? "The configuration change could not be replicated to the cluster",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (_logConfigChanges)
        {
            _logger.LogInformation("Set config {Namespace}/{Key} committed ({ElapsedMs} ms)", ns, key, sw.ElapsedMilliseconds);
        }

        // Return command data directly — Raft commit guarantees durability.
        // No poll loop needed: ReadBarrier on subsequent GETs ensures visibility.
        return Ok(new ConfigEntryDto
        {
            Namespace = ns,
            Key = key,
            Value = result.Value,
            Type = result.Type,
            UpdatedAt = result.Timestamp,
            UpdatedBy = result.Author,
            Version = 0  // Version assigned by state machine on apply
        });
    }

    /// <summary>
    /// Delete a configuration entry.
    /// </summary>
    [HttpDelete("{key}")]
    [Authorize(Policy = "Write")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "namespace")] string ns,
        string key,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Forward to leader if we're not the leader
        if (!_raft.IsLeader)
        {
            return await ForwardToLeaderAsync(ct);
        }

        var author = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        if (_logConfigChanges)
        {
            _logger.LogInformation("Deleting config {Namespace}/{Key} by {Author}", ns, key, author);
        }

        // Check if entry exists
        var existing = await _store.GetAsync(ns, key, ct);
        if (existing is null)
        {
            return Problem(
                title: "Configuration not found",
                detail: $"No configuration entry found with key '{key}' in namespace '{ns}'",
                statusCode: StatusCodes.Status404NotFound);
        }

        var command = new DeleteConfigCommand
        {
            Namespace = ns,
            Key = key,
            Author = author
        };

        var replicated = await _raft.ReplicateAsync(command, ct);
        if (!replicated)
        {
            _logger.LogWarning("Delete config {Namespace}/{Key} failed replication ({ElapsedMs} ms)", ns, key, sw.ElapsedMilliseconds);
            return Problem(
                title: "Replication failed",
                detail: "The configuration change could not be replicated to the cluster",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (_logConfigChanges)
        {
            _logger.LogInformation("Delete config {Namespace}/{Key} complete ({ElapsedMs} ms)", ns, key, sw.ElapsedMilliseconds);
        }
        return NoContent();
    }

    private Task<IActionResult> ForwardToLeaderAsync(CancellationToken ct)
    {
        var leaderUri = _raft.LeaderUri;
        if (leaderUri is null)
        {
            return Task.FromResult<IActionResult>(Problem(
                title: "No leader available",
                detail: "The cluster does not have a leader. Please try again later.",
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        // Return redirect to leader
        var redirectUri = new Uri(leaderUri, Request.Path + Request.QueryString);
        _logger.LogDebug("Forwarding write request to leader: {LeaderUri}", redirectUri);

        return Task.FromResult<IActionResult>(
            RedirectPreserveMethod(redirectUri.ToString()));
    }
}

/// <summary>
/// DTO for configuration entries in API responses.
/// </summary>
public record ConfigEntryDto
{
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public required string Type { get; init; }
    public required int Version { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    public static ConfigEntryDto FromModel(ConfigEntry entry, string? resolvedValue = null) => new()
    {
        Namespace = entry.Namespace,
        Key = entry.Key,
        Value = resolvedValue ?? entry.Value ?? "",
        Type = entry.Type,
        Version = entry.Version,
        UpdatedAt = entry.UpdatedAt,
        UpdatedBy = entry.UpdatedBy
    };
}

/// <summary>
/// Request body for setting a configuration entry.
/// </summary>
public record SetConfigRequest
{
    public required string Value { get; init; }
    public string? Type { get; init; }
}