using System.Diagnostics;
using System.Security.Claims;
using Confman.Api.Cluster;
using Confman.Api.Cluster.Commands;
using Confman.Api.Models;
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
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(
        IConfigStore store,
        IRaftService raft,
        ILogger<ConfigController> logger)
    {
        _store = store;
        _raft = raft;
        _logger = logger;
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
        var dtos = entries.Select(ConfigEntryDto.FromModel);

        return Ok(dtos);
    }

    /// <summary>
    /// Get a specific configuration entry.
    /// </summary>
    [HttpGet("{key}")]
    [Authorize(Policy = "ReadOnly")]
    [ProducesResponseType(typeof(ConfigEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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

        return Ok(ConfigEntryDto.FromModel(entry));
    }

    /// <summary>
    /// Create or update a configuration entry.
    /// </summary>
    [HttpPut("{key}")]
    [Authorize(Policy = "Write")]
    [ProducesResponseType(typeof(ConfigEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConfigEntryDto), StatusCodes.Status201Created)]
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
        _logger.LogInformation("Setting config {Namespace}/{Key} by {Author}", ns, key, author);

        // Check if entry exists for response code
        var existing = await _store.GetAsync(ns, key, ct);

        var command = new SetConfigCommand
        {
            Namespace = ns,
            Key = key,
            Value = request.Value,
            Type = request.Type ?? "string",
            Author = author
        };

        var replicated = await _raft.ReplicateAsync(command, ct);
        if (!replicated)
        {
            _logger.LogWarning("Set config {Namespace}/{Key} failed replication ({ElapsedMs} ms)", ns, key, sw.ElapsedMilliseconds);
            return Problem(
                title: "Replication failed",
                detail: "The configuration change could not be replicated to the cluster",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Fetch the updated entry - may need brief retry due to apply lag
        ConfigEntry? entry = null;
        for (var i = 0; i < 5 && entry is null; i++)
        {
            entry = await _store.GetAsync(ns, key, ct);
            if (entry is null)
                await Task.Delay(10, ct);
        }

        if (entry is null)
        {
            _logger.LogWarning("Set config {Namespace}/{Key} apply pending ({ElapsedMs} ms)", ns, key, sw.ElapsedMilliseconds);
            return Problem(
                title: "Apply pending",
                detail: "The change was replicated but not yet applied. Retry the read.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        _logger.LogInformation("Set config {Namespace}/{Key} complete ({ElapsedMs} ms)", ns, key, sw.ElapsedMilliseconds);

        var dto = ConfigEntryDto.FromModel(entry);

        if (existing is null)
        {
            return CreatedAtAction(nameof(Get), new { @namespace = ns, key }, dto);
        }

        return Ok(dto);
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
        _logger.LogInformation("Deleting config {Namespace}/{Key} by {Author}", ns, key, author);

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

        _logger.LogInformation("Delete config {Namespace}/{Key} complete ({ElapsedMs} ms)", ns, key, sw.ElapsedMilliseconds);
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

    public static ConfigEntryDto FromModel(ConfigEntry entry) => new()
    {
        Namespace = entry.Namespace,
        Key = entry.Key,
        Value = entry.Value,
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