using Confman.Api.Storage;
using MessagePack;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to delete a namespace.
/// </summary>
[MessagePackObject]
public sealed record DeleteNamespaceCommand : ICommand
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public required string Author { get; init; }
    [Key(2)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default)
    {
        if (auditEnabled)
        {
            // Single call — store handles existence check, cascade delete, audit, and old-value capture
            await store.DeleteNamespaceWithAuditAsync(Path, Author, Timestamp, ct);
        }
        else
        {
            await store.DeleteNamespaceAsync(Path, ct);
        }
    }
}