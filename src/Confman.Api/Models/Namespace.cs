using LiteDB;
using MessagePack;

namespace Confman.Api.Models;

/// <summary>
/// Represents a configuration namespace.
/// </summary>
[MessagePackObject]
public class Namespace
{
    [IgnoreMember] public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    [Key(0)] public required string Path { get; set; }
    [Key(1)] public string? Description { get; set; }
    [Key(2)] public required string Owner { get; set; }
    [Key(3)] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}