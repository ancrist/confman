using Confman.Api.Storage;
using LiteDB;
using MessagePack;

namespace Confman.Api.Models;

/// <summary>
/// Represents an audit trail event for configuration changes.
/// OldValue/NewValue are stored compressed (MessagePack+LZ4) in LiteDB,
/// but exposed as plain strings for Raft serialization and API responses.
/// </summary>
[MessagePackObject]
public class AuditEvent
{
    [IgnoreMember] public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    [Key(0)] public required DateTimeOffset Timestamp { get; set; }
    [Key(1)] public required AuditAction Action { get; set; }
    [Key(2)] public required string Actor { get; set; }
    [Key(3)] public required string Namespace { get; set; }
    [Key(4)] public string? Key { get; set; }
    [Key(7)] public long? RaftTerm { get; set; }
    [Key(8)] public long? RaftIndex { get; set; }

    // --- Lazy compress/decompress pair for OldValue ---
    private string? _oldValue;
    private byte[]? _compressedOldValue;
    private bool _oldValueSet;

    [Key(5)]
    [BsonIgnore]
    public string? OldValue
    {
        get
        {
            if (!_oldValueSet && _compressedOldValue is not null)
            {
                _oldValue = ValueCompression.Decompress(_compressedOldValue);
                _oldValueSet = true;
            }
            return _oldValue;
        }
        set
        {
            _oldValue = value;
            _oldValueSet = true;
            _compressedOldValue = null;
        }
    }

    [IgnoreMember]
    public byte[]? CompressedOldValue
    {
        get
        {
            if (_compressedOldValue is null && _oldValue is not null)
                _compressedOldValue = ValueCompression.Compress(_oldValue);
            return _compressedOldValue;
        }
        set
        {
            _compressedOldValue = value;
            _oldValue = null;
            _oldValueSet = false;
        }
    }

    // --- Lazy compress/decompress pair for NewValue ---
    private string? _newValue;
    private byte[]? _compressedNewValue;
    private bool _newValueSet;

    [Key(6)]
    [BsonIgnore]
    public string? NewValue
    {
        get
        {
            if (!_newValueSet && _compressedNewValue is not null)
            {
                _newValue = ValueCompression.Decompress(_compressedNewValue);
                _newValueSet = true;
            }
            return _newValue;
        }
        set
        {
            _newValue = value;
            _newValueSet = true;
            _compressedNewValue = null;
        }
    }

    [IgnoreMember]
    public byte[]? CompressedNewValue
    {
        get
        {
            if (_compressedNewValue is null && _newValue is not null)
                _compressedNewValue = ValueCompression.Compress(_newValue);
            return _compressedNewValue;
        }
        set
        {
            _compressedNewValue = value;
            _newValue = null;
            _newValueSet = false;
        }
    }
}
