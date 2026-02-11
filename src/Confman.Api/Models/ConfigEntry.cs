using Confman.Api.Storage;
using LiteDB;
using MessagePack;

namespace Confman.Api.Models;

/// <summary>
/// Represents a configuration entry in the store.
/// Value is stored compressed (MessagePack+LZ4) in LiteDB via CompressedValue,
/// but exposed as a plain string for Raft serialization and API responses.
/// </summary>
[MessagePackObject]
public class ConfigEntry
{
    [IgnoreMember] public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    [Key(0)] public required string Namespace { get; set; }
    [Key(1)] public required string Key { get; set; }
    [Key(3)] public string Type { get; set; } = "string";
    [Key(4)] public int Version { get; set; } = 1;
    [Key(5)] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Key(6)] public required string UpdatedBy { get; set; }

    // --- Lazy compress/decompress pair for Value ---
    private string? _value;
    private byte[]? _compressedValue;

    /// <summary>
    /// The config value as a string. MessagePack serializes this ([Key(2)]).
    /// Hidden from LiteDB via [BsonIgnore] — LiteDB uses CompressedValue instead.
    /// </summary>
    [Key(2)]
    [BsonIgnore]
    public required string Value
    {
        get
        {
            if (_value is null && _compressedValue is not null)
                _value = ValueCompression.Decompress(_compressedValue);
            return _value!;
        }
        set
        {
            _value = value;
            _compressedValue = null;
        }
    }

    /// <summary>
    /// LZ4-compressed value stored in LiteDB. Hidden from MessagePack via [IgnoreMember].
    /// </summary>
    [IgnoreMember]
    public byte[]? CompressedValue
    {
        get
        {
            if (_compressedValue is null && _value is not null)
                _compressedValue = ValueCompression.Compress(_value);
            return _compressedValue;
        }
        set
        {
            _compressedValue = value;
            _value = null;
        }
    }
}