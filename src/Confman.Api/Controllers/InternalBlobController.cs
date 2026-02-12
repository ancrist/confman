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

        // Check max blob size (Content-Length is optional/spoofable — also enforce via stream limit below)
        var maxSize = _options.Value.MaxBlobSizeBytes;
        if (Request.ContentLength > maxSize)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Blob exceeds maximum size of {maxSize} bytes");
        }

        // Idempotent: if blob already exists, return 204
        if (await _blobStore.ExistsAsync(blobId, ct))
        {
            _logger.LogDebug("Blob {BlobId} already exists, skipping", blobId);
            return NoContent();
        }

        try
        {
            // Wrap body in size-limiting stream to prevent disk exhaustion even without Content-Length
            var limitedBody = new SizeLimitingStream(Request.Body, maxSize);
            await _blobStore.PutCompressedAsync(blobId, limitedBody, Request.ContentLength ?? 0, ct);
            _logger.LogDebug("Received blob {BlobId} from leader", blobId);
            return Ok();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("hash validation failed"))
        {
            _logger.LogWarning("Blob {BlobId} hash validation failed", blobId);
            return BadRequest("Blob hash validation failed");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds maximum"))
        {
            _logger.LogWarning("Blob {BlobId} exceeds size limit", blobId);
            return StatusCode(StatusCodes.Status413PayloadTooLarge, "Blob exceeds maximum allowed size");
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

    /// <summary>
    /// Read-only stream wrapper that throws after maxBytes have been read.
    /// Prevents disk exhaustion from missing or spoofed Content-Length headers.
    /// </summary>
    private sealed class SizeLimitingStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
            _totalRead += read;
            if (_totalRead > maxBytes)
                throw new InvalidOperationException($"Stream exceeds maximum allowed size of {maxBytes} bytes");
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            _totalRead += read;
            if (_totalRead > maxBytes)
                throw new InvalidOperationException($"Stream exceeds maximum allowed size of {maxBytes} bytes");
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}