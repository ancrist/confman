using Confman.Api.Models;

namespace Confman.Api.Cluster;

/// <summary>
/// Data model for serializing Raft state machine snapshots.
/// Contains all state needed to fully restore a node.
/// </summary>
public class SnapshotData
{
    /// <summary>
    /// Snapshot format version for forward compatibility.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// All configuration entries in the store.
    /// </summary>
    public List<ConfigEntry> Configs { get; set; } = [];

    /// <summary>
    /// All namespaces in the store.
    /// </summary>
    public List<Namespace> Namespaces { get; set; } = [];

    /// <summary>
    /// All audit events in the store.
    /// </summary>
    public List<AuditEvent> AuditEvents { get; set; } = [];

    /// <summary>
    /// The Raft log index at which this snapshot was taken.
    /// </summary>
    public long SnapshotIndex { get; set; }

    /// <summary>
    /// When this snapshot was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }
}
