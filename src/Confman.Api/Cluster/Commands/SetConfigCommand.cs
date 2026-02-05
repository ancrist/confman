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