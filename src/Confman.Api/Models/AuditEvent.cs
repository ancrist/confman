using LiteDB;
using MessagePack;

namespace Confman.Api.Models;

/// <summary>
/// Represents an audit trail event for configuration changes.
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
    [Key(5)] public string? OldValue { get; set; }
    [Key(6)] public string? NewValue { get; set; }
    [Key(7)] public long? RaftTerm { get; set; }
    [Key(8)] public long? RaftIndex { get; set; }
}