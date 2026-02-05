using Confman.Api.Models;
using Confman.Api.Storage;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to set (create or update) a namespace.
/// </summary>
public sealed record SetNamespaceCommand : ICommand
{
    public required string Path { get; init; }
    public string? Description { get; init; }
    public required string Owner { get; init; }
    public required string Author { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

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