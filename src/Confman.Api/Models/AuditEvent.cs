using LiteDB;

namespace Confman.Api.Models;

/// <summary>
/// Represents an audit trail event for configuration changes.
/// </summary>
public class AuditEvent
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public required DateTimeOffset Timestamp { get; set; }
    public required string Action { get; set; }
    public required string Actor { get; set; }
    public required string Namespace { get; set; }
    public string? Key { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public long? RaftTerm { get; set; }
    public long? RaftIndex { get; set; }
}
