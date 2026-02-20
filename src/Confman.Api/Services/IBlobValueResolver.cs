using Confman.Api.Models;

namespace Confman.Api.Services;

/// <summary>
/// Resolves the string value for a ConfigEntry.
/// Inline entries: returns entry.Value directly.
/// Blob-backed entries: decompresses from local blob store, or fetches from a peer if missing locally.
/// </summary>
public interface IBlobValueResolver
{
    /// <summary>
    /// Resolves the config value. Returns null if the blob is unavailable from all sources.
    /// </summary>
    Task<string?> ResolveAsync(ConfigEntry entry, CancellationToken ct = default);
}