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

    public async Task ApplyAsync(IConfigStore store, bool isLeader, CancellationToken ct = default)
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

        // Only create audit events on the leader to avoid duplicates during log replay
        if (isLeader)
        {
            await store.AppendAuditAsync(new AuditEvent
            {
                Timestamp = Timestamp,
                Action = existing is null ? "config.created" : "config.updated",
                Actor = Author,
                Namespace = Namespace,
                Key = Key,
                OldValue = existing?.Value,
                NewValue = Value
            }, ct);
        }
    }
}
