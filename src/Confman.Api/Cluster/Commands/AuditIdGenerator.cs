using Confman.Api.Models;
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
    /// Uses timestamp, namespace, key, and action to create a unique, reproducible ID.
    /// Action IS included to prevent collisions when multiple actions (e.g., create
    /// then delete) occur on the same key at the same timestamp. The create-vs-update
    /// ambiguity on replay is acceptable — upsert ensures idempotency regardless.
    /// </summary>
    public static ObjectId Generate(DateTimeOffset timestamp, string ns, string? key, AuditAction action)
    {
        var compositeKey = $"{timestamp:O}:{ns}:{key ?? ""}:{action}";
        var hashBytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(compositeKey));

        // ObjectId is 12 bytes - take first 12 bytes of hash
        return new ObjectId(hashBytes[..12]);
    }
}