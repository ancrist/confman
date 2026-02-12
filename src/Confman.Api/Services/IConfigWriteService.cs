namespace Confman.Api.Services;

/// <summary>
/// Orchestrates config writes through either the inline (Raft) or blob (store + replicate + Raft pointer) path.
/// </summary>
public interface IConfigWriteService
{
    /// <summary>
    /// Writes a config entry through the appropriate path based on value size.
    /// For values >= InlineThresholdBytes: stores in blob store, replicates to quorum, then Raft commits pointer.
    /// For values below threshold: Raft commits the full value directly.
    /// </summary>
    Task<ConfigWriteResult> WriteAsync(
        string ns, string key, string value, string type,
        string author, CancellationToken ct = default);
}

/// <summary>
/// Result of a config write operation.
/// </summary>
public sealed record ConfigWriteResult(
    bool Success,
    string Namespace,
    string Key,
    string Value,
    string Type,
    DateTimeOffset Timestamp,
    string Author,
    string? ErrorDetail = null);
