using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;

namespace Confman.Api.Storage.Blobs;

/// <summary>
/// Replicates blobs to cluster peers via HTTP PUT /internal/blobs/{blobId}.
/// Uses quorum-first signaling: returns as soon as enough peers ACK, not when all finish.
/// Remaining pushes continue in background (fire-and-forget).
/// </summary>
public sealed class PeerBlobReplicator : IBlobReplicator
{
    private readonly IRaftCluster _cluster;
    private readonly IBlobStore _blobStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<BlobStoreOptions> _options;
    private readonly ILogger<PeerBlobReplicator> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public PeerBlobReplicator(
        IRaftCluster cluster,
        IBlobStore blobStore,
        IHttpClientFactory httpClientFactory,
        IOptions<BlobStoreOptions> options,
        ILogger<PeerBlobReplicator> logger,
        IHostApplicationLifetime lifetime)
    {
        _cluster = cluster;
        _blobStore = blobStore;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _lifetime = lifetime;
    }

    public async Task ReplicateAsync(string blobId, CancellationToken ct = default)
    {
        var followers = GetFollowerUris();

        // Single-node cluster: no replication needed
        if (followers.Count == 0)
        {
            _logger.LogDebug("Single-node cluster, skipping blob replication for {BlobId}", blobId);
            return;
        }

        // Required ACKs: quorum - 1 (leader counts as one)
        // For 3-node cluster: quorum = 2, required ACKs from followers = 1
        var clusterSize = followers.Count + 1; // +1 for leader (us)
        var quorum = clusterSize / 2 + 1;
        var requiredAcks = quorum - 1; // leader already has the blob

        var quorumTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackCount = 0;
        var failCount = 0;

        // Use app shutdown token for background pushes (not the HTTP request token)
        var bgToken = _lifetime.ApplicationStopping;

        var pushTasks = followers.Select(async followerUri =>
        {
            try
            {
                await PushBlobToFollowerAsync(blobId, followerUri, ct);

                var currentAcks = Interlocked.Increment(ref ackCount);
                _logger.LogDebug("Blob {BlobId} ACK from {Follower} ({Acks}/{Required})",
                    blobId, followerUri, currentAcks, requiredAcks);

                if (currentAcks >= requiredAcks)
                {
                    quorumTcs.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var currentFails = Interlocked.Increment(ref failCount);
                _logger.LogWarning(ex, "Blob {BlobId} push to {Follower} failed ({Fails} failures)",
                    blobId, followerUri, currentFails);

                // Fail-fast: if remaining can't reach quorum
                var currentAcks = Volatile.Read(ref ackCount);
                var remaining = followers.Count - currentAcks - currentFails;
                if (currentAcks + remaining < requiredAcks)
                {
                    quorumTcs.TrySetException(new BlobReplicationException(
                        $"Blob {blobId} quorum unreachable: {currentAcks} ACKs, {currentFails} failures, {remaining} remaining, need {requiredAcks}"));
                }
            }
        }).ToArray();

        // Wait for quorum or timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        var completed = await Task.WhenAny(quorumTcs.Task, timeoutTask);

        if (completed == timeoutTask)
        {
            throw new BlobReplicationException(
                $"Blob {blobId} replication timed out: {Volatile.Read(ref ackCount)}/{requiredAcks} ACKs after 10s");
        }

        // This will throw if fail-fast triggered
        await quorumTcs.Task;

        _logger.LogDebug("Blob {BlobId} quorum achieved ({Acks} ACKs)", blobId, Volatile.Read(ref ackCount));

        // Fire-and-forget: remaining pushes continue in background
        _ = Task.WhenAll(pushTasks).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogDebug("Background blob push completed with some failures for {BlobId}", blobId);
            }
        }, bgToken, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private async Task PushBlobToFollowerAsync(string blobId, Uri followerUri, CancellationToken ct)
    {
        await using var blobStream = await _blobStore.OpenReadAsync(blobId, ct);
        if (blobStream is null)
        {
            throw new InvalidOperationException($"Blob {blobId} not found locally");
        }

        var client = _httpClientFactory.CreateClient("BlobReplication");
        var requestUri = new Uri(followerUri, $"internal/blobs/{blobId}");

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.Add(Auth.ClusterTokenAuthOptions.HeaderName, _options.Value.ClusterToken);
        request.Content = new StreamContent(blobStream, bufferSize: 81_920);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Blob push to {followerUri} failed with {response.StatusCode}");
        }
    }

    private List<Uri> GetFollowerUris()
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