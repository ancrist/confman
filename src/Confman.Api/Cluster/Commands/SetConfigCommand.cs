using Confman.Api.Models;
using Confman.Api.Storage;
using MessagePack;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command to set (create or update) a configuration entry.
/// </summary>
[MessagePackObject]
public sealed record SetConfigCommand : ICommand
{
    [Key(0)] public required string Namespace { get; init; }
    [Key(1)] public required string Key { get; init; }
    [Key(2)] public required string Value { get; init; }
    [Key(3)] public string Type { get; init; } = "string";
    [Key(4)] public required string Author { get; init; }
    [Key(5)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [IgnoreMember] public int EstimatedBytes => 64 + (Value?.Length ?? 0);

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default)
    {
        var entry = new ConfigEntry
        {
            Namespace = Namespace,
            Key = Key,
            Value = Value,
            Type = Type,
            UpdatedAt = Timestamp,
            UpdatedBy = Author
        };

        if (auditEnabled)
        {
            // Single call — store handles versioning, audit, and old-value capture in one read
            await store.SetWithAuditAsync(entry, Author, Timestamp, ct);
        }
        else
        {
            await store.SetAsync(entry, ct);
        }
    }
}