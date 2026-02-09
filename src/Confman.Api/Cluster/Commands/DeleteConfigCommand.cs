using Confman.Api.Storage;
using MessagePack;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to delete a configuration entry.
/// </summary>
[MessagePackObject]
public sealed record DeleteConfigCommand : ICommand
{
    [Key(0)] public required string Namespace { get; init; }
    [Key(1)] public required string Key { get; init; }
    [Key(2)] public required string Author { get; init; }
    [Key(3)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default)
    {
        if (auditEnabled)
        {
            // Single call — store handles existence check, delete, audit, and old-value capture
            await store.DeleteWithAuditAsync(Namespace, Key, Author, Timestamp, ct);
        }
        else
        {
            await store.DeleteAsync(Namespace, Key, ct);
        }
    }
}