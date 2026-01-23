using LiteDB;

namespace Confman.Api.Models;

/// <summary>
/// Represents a configuration namespace.
/// </summary>
public class Namespace
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public required string Path { get; set; }
    public string? Description { get; set; }
    public required string Owner { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
