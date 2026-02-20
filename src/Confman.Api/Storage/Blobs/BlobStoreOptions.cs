namespace Confman.Api.Storage.Blobs;

/// <summary>
/// Configuration options for the blob store subsystem.
/// Bound to the "BlobStore" configuration section.
/// </summary>
public sealed class BlobStoreOptions
{
    public const string SectionName = "BlobStore";
    public const int DefaultThreshold = 65_536;

    /// <summary>Whether the blob store is enabled. When false, all values are stored inline.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Values at or above this size (in bytes) use the blob path. Default: 64KB.</summary>
    public int InlineThresholdBytes { get; set; } = DefaultThreshold;

    /// <summary>Shared secret for inter-node blob replication endpoints.</summary>
    public string ClusterToken { get; set; } = string.Empty;

    /// <summary>Maximum allowed blob size in bytes (compressed on disk). Default: 50MB.</summary>
    public int MaxBlobSizeBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Maximum allowed decompressed size in bytes. Prevents LZ4 decompression bombs. Default: 200MB.</summary>
    public long MaxDecompressedSizeBytes { get; set; } = 200 * 1024 * 1024L;
}