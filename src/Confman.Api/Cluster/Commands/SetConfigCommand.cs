using Confman.Api.Models;
using Confman.Api.Storage;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to set (create or update) a configuration entry.
/// </summary>
public sealed record SetConfigCommand : ICommand
{
    public required string Namespace { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string Type { get; init; } = "string";
    public required string Author { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public async Task ApplyAsync(IConfigStore store, CancellationToken ct = default)
    {
        // Get existing entry to capture old value for audit
        var existing = await store.GetAsync(Namespace, Key, ct);

        var entry = new ConfigEntry
        {
            Namespace = Namespace,
            Key = Key,
            Value = Value,
            Type = Type,
            UpdatedAt = Timestamp,
            UpdatedBy = Author
        };

        await store.SetAsync(entry, ct);

        // Create audit on all nodes - storage handles idempotency via upsert
        var action = existing is null ? "config.created" : "config.updated";
        await store.AppendAuditAsync(new AuditEvent
        {
            Id = AuditIdGenerator.Generate(Timestamp, Namespace, Key, action),
            Timestamp = Timestamp,
            Action = action,
            Actor = Author,
            Namespace = Namespace,
            Key = Key,
            OldValue = existing?.Value,
            NewValue = Value
        }, ct);
    }
}
