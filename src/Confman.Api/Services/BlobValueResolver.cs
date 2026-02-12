using System.Collections.Concurrent;
using Confman.Api.Models;
using Confman.Api.Storage.Blobs;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;

namespace Confman.Api.Services;

/// <summary>
/// Resolves blob-backed config values by reading from the local blob store.
/// If the blob is missing locally (e.g. after snapshot restore), fetches from a peer.
/// Uses per-BlobId deduplication to prevent thundering herd on missing blobs.
/// </summary>
public sealed class BlobValueResolver : IBlobValueResolver
{
    private readonly IBlobStore _blobStore;
    private readonly IRaftCluster _cluster;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<BlobStoreOptions> _options;
    private readonly ILogger<BlobValueResolver> _logger;

    // Per-BlobId deduplication gate: prevents thundering herd when many concurrent readers
    // request the same missing blob. Only one network fetch per BlobId at a time.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchGates = new();

    public BlobValueResolver(
        IBlobStore blobStore,
        IRaftCluster cluster,
        IHttpClientFactory httpClientFactory,
        IOptions<BlobStoreOptions> options,
        ILogger<BlobValueResolver> logger)
    {
        _blobStore = blobStore;
        _cluster = cluster;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<string?> ResolveAsync(ConfigEntry entry, CancellationToken ct = default)
    {
        // Inline entries: return directly
        if (!entry.IsBlobBacked)
        {
            return entry.Value;
        }

        var blobId = entry.BlobId!;

        // Fast path: blob exists locally
        var value = await TryReadLocalAsync(blobId, ct);
        if (value is not null)
        {
            return value;
        }

        // Slow path: fetch from peer with deduplication gate
        var gate = _fetchGates.GetOrAdd(blobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Double-check after acquiring gate (another thread may have fetched it)
            value = await TryReadLocalAsync(blobId, ct);
            if (value is not null)
            {
                return value;
            }

            // Fetch from peers
            value = await FetchFromPeerAsync(blobId, ct);
            return value;
        }
        finally
        {
            gate.Release();
            // Do NOT remove the gate: other threads may be queued on this semaphore.
            // Removing it would let a new GetOrAdd create a second SemaphoreSlim for the same
            // blobId, breaking the deduplication guarantee. SemaphoreSlim(1,1) is lightweight;
            // one per unique missing blobId is acceptable for a config service.
        }
    }

    private async Task<string?> TryReadLocalAsync(string blobId, CancellationToken ct)
    {
        await using var stream = await _blobStore.OpenReadAsync(blobId, ct);
        if (stream is null)
        {
            return null;
        }

        return await BlobCompression.DecompressToStringAsync(stream, _options.Value.MaxDecompressedSizeBytes, ct);
    }

    private async Task<string?> FetchFromPeerAsync(string blobId, CancellationToken ct)
    {
        var peerUris = GetPeerUris();
        if (peerUris.Count == 0)
        {
            _logger.LogWarning("Blob {BlobId} missing locally and no peers available", blobId);
            return null;
        }

        foreach (var peerUri in peerUris)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("BlobReplication");
                var requestUri = new Uri(peerUri, $"internal/blobs/{blobId}");

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Add(Auth.ClusterTokenAuthOptions.HeaderName, _options.Value.ClusterToken);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Peer {Peer} returned {Status} for blob {BlobId}",
                        peerUri, response.StatusCode, blobId);
                    continue;
                }

                // Cache locally for future reads
                await using var peerStream = await response.Content.ReadAsStreamAsync(ct);
                var contentLength = response.Content.Headers.ContentLength ?? 0;
                await _blobStore.PutCompressedAsync(blobId, peerStream, contentLength, ct);

                _logger.LogDebug("Fetched and cached blob {BlobId} from {Peer}", blobId, peerUri);

                // Read back the cached value
                return await TryReadLocalAsync(blobId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to fetch blob {BlobId} from {Peer}", blobId, peerUri);
            }
        }

        _logger.LogWarning("Blob {BlobId} unavailable from all {PeerCount} peers", blobId, peerUris.Count);
        return null;
    }

    private List<Uri> GetPeerUris()
    {
        var uris = new List<Uri>();
        foreach (var member in _cluster.Members)
        {
            if (member.IsRemote && member.EndPoint is UriEndPoint uriEndPoint)
            {
                uris.Add(uriEndPoint.Uri);
            }
        }

        return uris;
    }
}