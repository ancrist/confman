using Confman.Api.Models;
using MessagePack;

namespace Confman.Api.Cluster;

/// <summary>
/// Data model for serializing Raft state machine snapshots.
/// Contains all state needed to fully restore a node.
/// </summary>
[MessagePackObject]
public class SnapshotData
{
    /// <summary>
    /// Snapshot format version for forward compatibility.
    /// </summary>
    [Key(0)] public int Version { get; set; } = 2;

    /// <summary>
    /// All configuration entries in the store.
    /// </summary>
    [Key(1)] public List<ConfigEntry> Configs { get; set; } = [];

    /// <summary>
    /// All namespaces in the store.
    /// </summary>
    [Key(2)] public List<Namespace> Namespaces { get; set; } = [];

    /// <summary>
    /// All audit events in the store.
    /// </summary>
    [Key(3)] public List<AuditEvent> AuditEvents { get; set; } = [];

    /// <summary>
    /// The Raft log index at which this snapshot was taken.
    /// </summary>
    [Key(4)] public long SnapshotIndex { get; set; }

    /// <summary>
    /// When this snapshot was created.
    /// </summary>
    [Key(5)] public DateTimeOffset Timestamp { get; set; }
}