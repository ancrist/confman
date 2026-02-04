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
        var existing = auditEnabled ? await store.GetNamespaceAsync(Path, ct) : null;

        var ns = new Namespace
        {
            Path = Path,
            Description = Description,
            Owner = Owner,
            CreatedAt = existing?.CreatedAt ?? Timestamp
        };

        await store.SetNamespaceAsync(ns, ct);

        if (auditEnabled)
        {
            // Create audit on all nodes - storage handles idempotency via upsert
            var action = existing is null ? AuditAction.NamespaceCreated : AuditAction.NamespaceUpdated;
            await store.AppendAuditAsync(new AuditEvent
            {
                Id = AuditIdGenerator.Generate(Timestamp, Path, null, action),
                Timestamp = Timestamp,
                Action = action,
                Actor = Author,
                Namespace = Path,
                Key = null,
                OldValue = existing?.Description,
                NewValue = Description
            }, ct);
        }
    }
}