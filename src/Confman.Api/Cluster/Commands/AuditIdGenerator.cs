using LiteDB;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Generates deterministic audit event IDs for idempotent storage.
/// The same command replayed will produce the same ID, preventing duplicates.
/// </summary>
public static class AuditIdGenerator
{
    /// <summary>
    /// Generates a deterministic ObjectId from audit event properties.
    /// Uses timestamp, namespace, and key to create a unique, reproducible ID.
    /// Action is NOT included because it may change on replay (created vs updated).
    /// </summary>
    public static ObjectId Generate(DateTimeOffset timestamp, string ns, string? key, string action)
    {
        // Create a deterministic hash from the composite key
        // Note: action is NOT included - on log replay, the action might differ
        // (e.g., "created" becomes "updated" if entry already exists)
        var compositeKey = $"{timestamp:O}:{ns}:{key ?? ""}";
        var hashBytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(compositeKey));

        // ObjectId is 12 bytes - take first 12 bytes of hash
        return new ObjectId(hashBytes[..12]);
    }
}
