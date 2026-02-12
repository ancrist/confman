using System.Buffers;
using System.Security.Cryptography;
using K4os.Compression.LZ4.Streams;

namespace Confman.Api.Storage.Blobs;

/// <summary>
/// Streaming hash + LZ4 compression/decompression for blob storage.
/// Uses direct K4os.Compression.LZ4 (NOT MessagePack LZ4) to avoid double compression
/// and enable streaming decompress without full materialization.
/// </summary>
public static class BlobCompression
{
    // 80KB buffer — stays under the 85,000-byte LOH threshold
    private const int BufferSize = 81_920;

    /// <summary>
    /// Single-pass pipeline: source → IncrementalHash(SHA256) + LZ4Stream.Encode → destination.
    /// Returns hex-encoded SHA256 hash of the uncompressed data.
    /// Zero LOH allocations — uses ArrayPool rented buffer.
    /// </summary>
    public static async Task<string> HashAndCompressAsync(
        Stream source, Stream destination, CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var lz4 = LZ4Stream.Encode(destination, leaveOpen: true);

            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, bytesRead));
                await lz4.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }

            await lz4.FlushAsync(ct);

            var hashBytes = hash.GetHashAndReset();
            return Convert.ToHexStringLower(hashBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Decompresses an LZ4 stream to destination. Streaming, no full materialization.
    /// </summary>
    public static async Task DecompressAsync(
        Stream source, Stream destination, CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var lz4 = LZ4Stream.Decode(source, leaveOpen: true);

            int bytesRead;
            while ((bytesRead = await lz4.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Decompresses an LZ4 stream to a string. Materializes the full value in memory.
    /// This is unavoidable for the read path — the API returns JSON with the value inline.
    /// </summary>
    public static async Task<string> DecompressToStringAsync(
        Stream compressedSource, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await DecompressAsync(compressedSource, ms, ct);
        ms.Position = 0;
        using var reader = new StreamReader(ms);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Validates that SHA256 of decompressed content matches the expected blobId.
    /// </summary>
    public static async Task<bool> ValidateAsync(
        string expectedBlobId, Stream compressedSource, CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var lz4 = LZ4Stream.Decode(compressedSource, leaveOpen: true);

            int bytesRead;
            while ((bytesRead = await lz4.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, bytesRead));
            }

            var hashBytes = hash.GetHashAndReset();
            var computedId = Convert.ToHexStringLower(hashBytes);
            return string.Equals(computedId, expectedBlobId, StringComparison.Ordinal);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
