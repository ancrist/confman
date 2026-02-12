using System.Text.RegularExpressions;
using Confman.Api.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Confman.Api.Controllers;

/// <summary>
/// Internal endpoints for inter-node blob replication.
/// Secured with cluster token authentication — not exposed to external clients.
/// Hidden from Swagger documentation.
/// </summary>
[ApiController]
[Route("internal/blobs")]
[Authorize(Policy = "ClusterInternal")]
[ApiExplorerSettings(IgnoreApi = true)]
public partial class InternalBlobController : ControllerBase
{
    private readonly IBlobStore _blobStore;
    private readonly IOptions<BlobStoreOptions> _options;
    private readonly ILogger<InternalBlobController> _logger;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex BlobIdPattern();

    public InternalBlobController(
        IBlobStore blobStore,
        IOptions<BlobStoreOptions> options,
        ILogger<InternalBlobController> logger)
    {
        _blobStore = blobStore;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Receive compressed blob from leader during replication.
    /// Streams request body directly to disk — no in-memory buffering.
    /// </summary>
    [HttpPut("{blobId}")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Put(string blobId, CancellationToken ct)
    {
        if (!BlobIdPattern().IsMatch(blobId))
        {
            return BadRequest($"Invalid blob ID: must be 64 lowercase hex characters");
        }

        // Check max blob size
        if (Request.ContentLength > _options.Value.MaxBlobSizeBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Blob exceeds maximum size of {_options.Value.MaxBlobSizeBytes} bytes");
        }

        // Idempotent: if blob already exists, return 204
        if (await _blobStore.ExistsAsync(blobId, ct))
        {
            _logger.LogDebug("Blob {BlobId} already exists, skipping", blobId);
            return NoContent();
        }

        try
        {
            await _blobStore.PutCompressedAsync(blobId, Request.Body, Request.ContentLength ?? 0, ct);
            _logger.LogDebug("Received blob {BlobId} from leader", blobId);
            return Ok();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("hash validation failed"))
        {
            _logger.LogWarning("Blob {BlobId} hash validation failed", blobId);
            return BadRequest("Blob hash validation failed");
        }
    }

    /// <summary>
    /// Fetch blob for missing blob repair.
    /// Returns the compressed blob file as an octet-stream.
    /// On Linux, Kestrel uses sendfile(2) for zero-copy transfer.
    /// </summary>
    [HttpGet("{blobId}")]
    public async Task<IActionResult> Get(string blobId, CancellationToken ct)
    {
        if (!BlobIdPattern().IsMatch(blobId))
        {
            return BadRequest($"Invalid blob ID: must be 64 lowercase hex characters");
        }

        var stream = await _blobStore.OpenReadAsync(blobId, ct);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "application/octet-stream");
    }
}
