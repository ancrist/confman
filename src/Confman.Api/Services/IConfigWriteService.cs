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
/// Only carries data the caller doesn't already have (Success, Timestamp, ErrorDetail).
/// The caller supplies Namespace, Key, Value, Type, and Author — no need to echo them back.
/// </summary>
public sealed record ConfigWriteResult(
    bool Success,
    DateTimeOffset Timestamp,
    string? ErrorDetail = null);