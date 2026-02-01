using System.Security.Claims;
using Confman.Api.Cluster;
using Confman.Api.Cluster.Commands;
using Confman.Api.Models;
using Confman.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Confman.Api.Controllers;

/// <summary>
/// API controller for namespace management.
/// </summary>
[ApiController]
[Route("api/v1/namespaces")]
[Authorize]
public class NamespacesController : ControllerBase
{
    private readonly IConfigStore _store;
    private readonly IRaftService _raft;
    private readonly ILogger<NamespacesController> _logger;

    public NamespacesController(
        IConfigStore store,
        IRaftService raft,
        ILogger<NamespacesController> logger)
    {
        _store = store;
        _raft = raft;
        _logger = logger;
    }

    /// <summary>
    /// List all namespaces.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "ReadOnly")]
    [ProducesResponseType(typeof(IEnumerable<NamespaceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        _logger.LogDebug("Listing all namespaces");

        var namespaces = await _store.ListNamespacesAsync(ct);
        var dtos = namespaces.Select(NamespaceDto.FromModel);

        return Ok(dtos);
    }

    /// <summary>
    /// Get a specific namespace.
    /// </summary>
    [HttpGet("{namespace}")]
    [Authorize(Policy = "ReadOnly")]
    [ProducesResponseType(typeof(NamespaceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromRoute(Name = "namespace")] string ns,
        CancellationToken ct)
    {
        _logger.LogDebug("Getting namespace: {Namespace}", ns);

        var result = await _store.GetNamespaceAsync(ns, ct);
        if (result is null)
        {
            return Problem(
                title: "Namespace not found",
                detail: $"No namespace found with path '{ns}'",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(NamespaceDto.FromModel(result));
    }

    /// <summary>
    /// Create or update a namespace.
    /// </summary>
    [HttpPut("{namespace}")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(typeof(NamespaceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NamespaceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Set(
        [FromRoute(Name = "namespace")] string ns,
        [FromBody] SetNamespaceRequest request,
        CancellationToken ct)
    {
        // Forward to leader if we're not the leader
        if (!_raft.IsLeader)
        {
            return await ForwardToLeaderAsync(ct);
        }

        var author = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        _logger.LogInformation("Setting namespace {Namespace} by {Author}", ns, author);

        // Check if namespace exists for response code
        var existing = await _store.GetNamespaceAsync(ns, ct);

        var command = new SetNamespaceCommand
        {
            Path = ns,
            Description = request.Description,
            Owner = request.Owner ?? author,
            Author = author
        };

        var replicated = await _raft.ReplicateAsync(command, ct);
        if (!replicated)
        {
            return Problem(
                title: "Replication failed",
                detail: "The namespace change could not be replicated to the cluster",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Fetch the updated namespace
        var result = await _store.GetNamespaceAsync(ns, ct);
        var dto = NamespaceDto.FromModel(result!);

        if (existing is null)
        {
            return CreatedAtAction(nameof(Get), new { @namespace = ns }, dto);
        }

        return Ok(dto);
    }

    /// <summary>
    /// Delete a namespace.
    /// </summary>
    [HttpDelete("{namespace}")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(
        [FromRoute(Name = "namespace")] string ns,
        CancellationToken ct)
    {
        // Forward to leader if we're not the leader
        if (!_raft.IsLeader)
        {
            return await ForwardToLeaderAsync(ct);
        }

        var author = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        _logger.LogInformation("Deleting namespace {Namespace} by {Author}", ns, author);

        // Check if namespace exists
        var existing = await _store.GetNamespaceAsync(ns, ct);
        if (existing is null)
        {
            return Problem(
                title: "Namespace not found",
                detail: $"No namespace found with path '{ns}'",
                statusCode: StatusCodes.Status404NotFound);
        }

        var command = new DeleteNamespaceCommand
        {
            Path = ns,
            Author = author
        };

        var replicated = await _raft.ReplicateAsync(command, ct);
        if (!replicated)
        {
            return Problem(
                title: "Replication failed",
                detail: "The namespace change could not be replicated to the cluster",
                statusCode: StatusCodes.Status503ServiceUnavailable);
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
/// DTO for namespaces in API responses.
/// </summary>
public record NamespaceDto
{
    public required string Path { get; init; }
    public string? Description { get; init; }
    public required string Owner { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static NamespaceDto FromModel(Namespace ns) => new()
    {
        Path = ns.Path,
        Description = ns.Description,
        Owner = ns.Owner,
        CreatedAt = ns.CreatedAt
    };
}

/// <summary>
/// Request body for creating/updating a namespace.
/// </summary>
public record SetNamespaceRequest
{
    public string? Description { get; init; }
    public string? Owner { get; init; }
}