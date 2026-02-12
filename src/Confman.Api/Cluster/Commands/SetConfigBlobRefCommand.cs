using Confman.Api.Models;
using Confman.Api.Storage;
using MessagePack;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to set a configuration entry whose value is stored in the external blob store.
/// Only the BlobId (SHA256 of uncompressed value) is replicated through Raft.
/// The actual blob bytes are replicated out-of-band via PeerBlobReplicator before this command is committed.
/// </summary>
[MessagePackObject]
public sealed record SetConfigBlobRefCommand : ICommand
{
    [Key(0)] public required string Namespace { get; init; }
    [Key(1)] public required string Key { get; init; }
    [Key(2)] public required string BlobId { get; init; }
    [Key(3)] public string Type { get; init; } = "string";
    [Key(4)] public required string Author { get; init; }
    [Key(5)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [IgnoreMember] public int EstimatedBytes => 64 + (BlobId?.Length ?? 0);

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default)
    {
        var entry = new ConfigEntry
        {
            Namespace = Namespace,
            Key = Key,
            Value = null,
            BlobId = BlobId,
            Type = Type,
            UpdatedBy = Author,
            UpdatedAt = Timestamp,
        };

        if (auditEnabled)
        {
            await store.SetWithAuditAsync(entry, Author, Timestamp, ct);
        }
        else
        {
            await store.SetAsync(entry, ct);
        }
    }
}
