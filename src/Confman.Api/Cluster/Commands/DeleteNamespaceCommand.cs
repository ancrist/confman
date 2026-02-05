using Confman.Api.Storage;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to delete a namespace.
/// </summary>
public sealed record DeleteNamespaceCommand : ICommand
{
    public required string Path { get; init; }
    public required string Author { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

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