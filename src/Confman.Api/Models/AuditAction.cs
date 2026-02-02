using System.Text.Json;
using System.Text.Json.Serialization;

namespace Confman.Api.Models;

/// <summary>
/// Strongly-typed audit action value object.
/// Serializes as a flat string (e.g., "config.created") for backward compatibility.
/// </summary>
[JsonConverter(typeof(AuditActionJsonConverter))]
public sealed record AuditAction
{
    public string ResourceType { get; }
    public string Verb { get; }
    public string Value { get; }

    private AuditAction(string resourceType, string verb)
    {
        ResourceType = resourceType;
        Verb = verb;
        Value = $"{resourceType}.{verb}";
    }

    // Config actions
    public static readonly AuditAction ConfigCreated = new("config", "created");
    public static readonly AuditAction ConfigUpdated = new("config", "updated");
    public static readonly AuditAction ConfigDeleted = new("config", "deleted");

    // Namespace actions
    public static readonly AuditAction NamespaceCreated = new("namespace", "created");
    public static readonly AuditAction NamespaceUpdated = new("namespace", "updated");
    public static readonly AuditAction NamespaceDeleted = new("namespace", "deleted");

    /// <summary>
    /// Parse a dotted string (e.g., "config.created") into an AuditAction.
    /// </summary>
    public static AuditAction Parse(string value)
    {
        var dot = value.IndexOf('.');
        if (dot < 0)
            throw new FormatException($"Invalid audit action format: '{value}'. Expected 'resourceType.verb'.");

        var resourceType = value[..dot];
        var verb = value[(dot + 1)..];
        return new AuditAction(resourceType, verb);
    }

    public override string ToString() => Value;
}

/// <summary>
/// Serializes AuditAction as a flat string for JSON compatibility.
/// </summary>
public sealed class AuditActionJsonConverter : JsonConverter<AuditAction>
{
    public override AuditAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => AuditAction.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, AuditAction value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
