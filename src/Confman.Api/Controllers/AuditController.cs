using Confman.Api.Models;
using Confman.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Confman.Api.Controllers;

/// <summary>
/// API controller for audit trail access.
/// </summary>
[ApiController]
[Route("api/v1/namespaces/{namespace}/audit")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly IConfigStore _store;
    private readonly ILogger<AuditController> _logger;

    public AuditController(
        IConfigStore store,
        ILogger<AuditController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Get audit events for a namespace.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "ReadOnly")]
    [ProducesResponseType(typeof(IEnumerable<AuditEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromRoute(Name = "namespace")] string ns,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Listing audit events for namespace: {Namespace}, limit: {Limit}", ns, limit);

        // Enforce reasonable limits
        limit = Math.Clamp(limit, 1, 1000);

        var events = await _store.GetAuditEventsAsync(ns, limit, ct);
        var dtos = events.Select(AuditEventDto.FromModel);

        return Ok(dtos);
    }
}

/// <summary>
/// DTO for audit events in API responses.
/// </summary>
public record AuditEventDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Action { get; init; }
    public required string Actor { get; init; }
    public required string Namespace { get; init; }
    public string? Key { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }

    public static AuditEventDto FromModel(AuditEvent evt) => new()
    {
        Timestamp = evt.Timestamp,
        Action = evt.Action,
        Actor = evt.Actor,
        Namespace = evt.Namespace,
        Key = evt.Key,
        OldValue = evt.OldValue,
        NewValue = evt.NewValue
    };
}
