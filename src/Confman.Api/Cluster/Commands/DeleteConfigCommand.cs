using Confman.Api.Storage;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to delete a configuration entry.
/// </summary>
public sealed record DeleteConfigCommand : ICommand
{
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Author { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

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