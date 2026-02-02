using Confman.Api.Models;
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

    public async Task ApplyAsync(IConfigStore store, CancellationToken ct = default)
    {
        var existing = await store.GetNamespaceAsync(Path, ct);

        if (existing is not null)
        {
            await store.DeleteNamespaceAsync(Path, ct);

            // Create audit on all nodes - storage handles idempotency via upsert
            var action = AuditAction.NamespaceDeleted;
            await store.AppendAuditAsync(new AuditEvent
            {
                Id = AuditIdGenerator.Generate(Timestamp, Path, null, action),
                Timestamp = Timestamp,
                Action = action,
                Actor = Author,
                Namespace = Path,
                Key = null,
                OldValue = existing.Description,
                NewValue = null
            }, ct);
        }
    }
}