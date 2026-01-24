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

    public async Task ApplyAsync(IConfigStore store, bool isLeader, CancellationToken ct = default)
    {
        var existing = await store.GetNamespaceAsync(Path, ct);

        var ns = new Namespace
        {
            Path = Path,
            Description = Description,
            Owner = Owner,
            CreatedAt = existing?.CreatedAt ?? Timestamp
        };

        await store.SetNamespaceAsync(ns, ct);

        // Only create audit events on the leader to avoid duplicates during log replay
        if (isLeader)
        {
            await store.AppendAuditAsync(new AuditEvent
            {
                Timestamp = Timestamp,
                Action = existing is null ? "namespace.created" : "namespace.updated",
                Actor = Author,
                Namespace = Path,
                Key = null,
                OldValue = existing?.Description,
                NewValue = Description
            }, ct);
        }
    }
}
