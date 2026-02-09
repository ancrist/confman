using Confman.Api.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Confman.Api.Cluster;

/// <summary>
/// Shared MessagePack serializer options with LZ4 compression and custom formatters.
/// Used for both Raft command serialization and snapshot persistence.
/// </summary>
public static class ConfmanSerializerOptions
{
    public static readonly MessagePackSerializerOptions Instance =
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                new IMessagePackFormatter[] { new AuditActionFormatter() },
                new IFormatterResolver[] { StandardResolver.Instance }))
            .WithCompression(MessagePackCompression.Lz4BlockArray);
}

/// <summary>
/// Serializes AuditAction as a flat string for MessagePack compatibility.
/// AuditAction has a private constructor, so MessagePack can't construct it directly.
/// </summary>
public sealed class AuditActionFormatter : IMessagePackFormatter<AuditAction>
{
    public void Serialize(ref MessagePackWriter writer, AuditAction? value,
        MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.Write(value.Value);
    }

    public AuditAction Deserialize(ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null!;

        var str = reader.ReadString()!;
        return AuditAction.Parse(str);
    }
}
