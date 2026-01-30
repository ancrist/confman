using LiteDB;

namespace Confman.Api.Models;

/// <summary>
/// Represents a configuration entry in the store.
/// </summary>
public class ConfigEntry
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public required string Namespace { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public string Type { get; set; } = "string";
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string UpdatedBy { get; set; }
}