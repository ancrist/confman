using Confman.Api.Models;
using Confman.Api.Storage;
using MessagePack;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to set (create or update) a namespace.
/// </summary>
[MessagePackObject]
public sealed record SetNamespaceCommand : ICommand
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public string? Description { get; init; }
    [Key(2)] public required string Owner { get; init; }
    [Key(3)] public required string Author { get; init; }
    [Key(4)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default)
    {
        var ns = new Namespace
        {
            Path = Path,
            Description = Description,
            Owner = Owner,
            CreatedAt = Timestamp
        };

        if (auditEnabled)
        {
            // Single call — store handles CreatedAt preservation, audit, and old-value capture
            await store.SetNamespaceWithAuditAsync(ns, Author, Timestamp, ct);
        }
        else
        {
            await store.SetNamespaceAsync(ns, ct);
        }
    }
}