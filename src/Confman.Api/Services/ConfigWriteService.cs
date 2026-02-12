using System.Text;
using Confman.Api.Cluster;
using Confman.Api.Cluster.Commands;
using Confman.Api.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace Confman.Api.Services;

/// <summary>
/// Orchestrates config writes through the inline or blob path.
/// Blob path: PutFromStreamAsync → ReplicateAsync (quorum) → SetConfigBlobRefCommand → Raft.
/// Inline path: SetConfigCommand → Raft.
/// </summary>
public sealed class ConfigWriteService : IConfigWriteService
{
    private readonly IBlobStore _blobStore;
    private readonly IBlobReplicator _blobReplicator;
    private readonly IRaftService _raft;
    private readonly IOptions<BlobStoreOptions> _options;
    private readonly ILogger<ConfigWriteService> _logger;

    public ConfigWriteService(
        IBlobStore blobStore,
        IBlobReplicator blobReplicator,
        IRaftService raft,
        IOptions<BlobStoreOptions> options,
        ILogger<ConfigWriteService> logger)
    {
        _blobStore = blobStore;
        _blobReplicator = blobReplicator;
        _raft = raft;
        _options = options;
        _logger = logger;
    }

    public async Task<ConfigWriteResult> WriteAsync(
        string ns, string key, string value, string type,
        string author, CancellationToken ct = default)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var useBlobPath = _options.Value.Enabled
            && Encoding.UTF8.GetByteCount(value) >= _options.Value.InlineThresholdBytes;

        if (useBlobPath)
        {
            return await WriteBlobPathAsync(ns, key, value, type, author, timestamp, ct);
        }

        return await WriteInlinePathAsync(ns, key, value, type, author, timestamp, ct);
    }

    private async Task<ConfigWriteResult> WriteInlinePathAsync(
        string ns, string key, string value, string type,
        string author, DateTimeOffset timestamp, CancellationToken ct)
    {
        var command = new SetConfigCommand
        {
            Namespace = ns,
            Key = key,
            Value = value,
            Type = type,
            Author = author,
            Timestamp = timestamp,
        };

        var replicated = await _raft.ReplicateAsync(command, ct);
        if (!replicated)
        {
            return new ConfigWriteResult(false, ns, key, value, type, timestamp, author,
                "Replication failed");
        }

        return new ConfigWriteResult(true, ns, key, value, type, timestamp, author);
    }

    private async Task<ConfigWriteResult> WriteBlobPathAsync(
        string ns, string key, string value, string type,
        string author, DateTimeOffset timestamp, CancellationToken ct)
    {
        // Step 1: Store blob locally
        string blobId;
        var valueBytes = Encoding.UTF8.GetBytes(value);
        using (var valueStream = new MemoryStream(valueBytes))
        {
            blobId = await _blobStore.PutFromStreamAsync(valueStream, valueBytes.Length, ct);
        }

        _logger.LogDebug("Blob stored locally: {BlobId} for {Namespace}/{Key}", blobId, ns, key);

        // Step 2: Replicate blob to quorum
        try
        {
            await _blobReplicator.ReplicateAsync(blobId, ct);
        }
        catch (BlobReplicationException ex)
        {
            _logger.LogWarning(ex, "Blob quorum failed for {BlobId} ({Namespace}/{Key})", blobId, ns, key);
            return new ConfigWriteResult(false, ns, key, value, type, timestamp, author,
                $"Blob replication failed: {ex.Message}");
        }

        // Step 3: Raft commit the pointer
        var command = new SetConfigBlobRefCommand
        {
            Namespace = ns,
            Key = key,
            BlobId = blobId,
            Type = type,
            Author = author,
            Timestamp = timestamp,
        };

        var replicated = await _raft.ReplicateAsync(command, ct);
        if (!replicated)
        {
            // Ghost blob: durable on quorum but never referenced by Raft.
            // Harmless (content-addressed, immutable) but wastes disk. Future GC can clean up.
            _logger.LogWarning(
                "Raft commit failed after blob quorum for {BlobId} ({Namespace}/{Key}). Ghost blob created",
                blobId, ns, key);
            return new ConfigWriteResult(false, ns, key, value, type, timestamp, author,
                "Raft replication failed after blob quorum");
        }

        _logger.LogDebug("Blob-backed write committed: {BlobId} for {Namespace}/{Key}", blobId, ns, key);
        return new ConfigWriteResult(true, ns, key, value, type, timestamp, author);
    }
}
