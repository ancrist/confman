using Confman.Api.Cluster;
using MessagePack;

namespace Confman.Api.Storage;

/// <summary>
/// Compresses/decompresses large string values for LiteDB storage
/// using MessagePack + LZ4 (same options as Raft serialization).
/// </summary>
internal static class ValueCompression
{
    public static byte[] Compress(string value)
        => MessagePackSerializer.Serialize(value, ConfmanSerializerOptions.Instance);

    public static string Decompress(byte[] data)
        => MessagePackSerializer.Deserialize<string>(data, ConfmanSerializerOptions.Instance);
}