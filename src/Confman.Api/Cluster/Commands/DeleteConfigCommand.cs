using Confman.Api.Models;
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

    public async Task ApplyAsync(IConfigStore store, CancellationToken ct = default)
    {
        // Get existing entry to capture old value for audit
        var existing = await store.GetAsync(Namespace, Key, ct);

        if (existing is not null)
        {
            await store.DeleteAsync(Namespace, Key, ct);

            await store.AppendAuditAsync(new AuditEvent
            {
                Timestamp = Timestamp,
                Action = "config.deleted",
                Actor = Author,
                Namespace = Namespace,
                Key = Key,
                OldValue = existing.Value,
                NewValue = null
            }, ct);
        }
    }
}
