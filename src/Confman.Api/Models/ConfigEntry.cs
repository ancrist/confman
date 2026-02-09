using LiteDB;
using MessagePack;

namespace Confman.Api.Models;

/// <summary>
/// Represents a configuration entry in the store.
/// </summary>
[MessagePackObject]
public class ConfigEntry
{
    [IgnoreMember] public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    [Key(0)] public required string Namespace { get; set; }
    [Key(1)] public required string Key { get; set; }
    [Key(2)] public required string Value { get; set; }
    [Key(3)] public string Type { get; set; } = "string";
    [Key(4)] public int Version { get; set; } = 1;
    [Key(5)] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Key(6)] public required string UpdatedBy { get; set; }
}