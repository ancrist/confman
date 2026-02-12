namespace Confman.Api.Storage.Blobs;

/// <summary>
/// Content-addressed blob store for large config values.
/// BlobId = SHA256(uncompressed value). Blobs are LZ4-compressed on disk.
/// All methods are thread-safe — concurrent writes for the same BlobId are
/// handled by atomic rename (content-addressed = identical content).
/// </summary>
public interface IBlobStore
{
    /// <summary>Checks if a blob exists in the local store.</summary>
    Task<bool> ExistsAsync(string blobId, CancellationToken ct = default);

    /// <summary>
    /// Stores a blob from a stream. Computes SHA256 hash + LZ4 compression in a single pass.
    /// Returns the computed BlobId (hex-encoded SHA256 of uncompressed source).
    /// Idempotent: if the blob already exists, returns immediately.
    /// </summary>
    Task<string> PutFromStreamAsync(Stream source, long contentLength, CancellationToken ct = default);

    /// <summary>
    /// Stores pre-compressed blob bytes (received from leader during replication).
    /// Validates that SHA256 of decompressed content matches the given blobId.
    /// </summary>
    Task PutCompressedAsync(string blobId, Stream source, long contentLength, CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream over the compressed blob file. Caller owns the stream.
    /// Returns null if blob does not exist.
    /// </summary>
    Task<Stream?> OpenReadAsync(string blobId, CancellationToken ct = default);

    /// <summary>Deletes a blob from the local store. No-op if not found.</summary>
    Task DeleteAsync(string blobId, CancellationToken ct = default);

    /// <summary>Enumerates all blob IDs in the local store.</summary>
    IAsyncEnumerable<string> ListBlobIdsAsync(CancellationToken ct = default);
}
