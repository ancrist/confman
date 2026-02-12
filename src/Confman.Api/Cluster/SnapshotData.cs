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
    /// Version 2: all configs have inline values.
    /// Version 3: configs may have BlobId (blob-backed entries with Value=null).
    /// </summary>
    [Key(0)] public int Version { get; set; } = 3;

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

    /// <summary>
    /// List of all BlobIds referenced by blob-backed config entries.
    /// Used for post-restore diagnostics (detect missing blobs).
    /// </summary>
    [Key(6)] public List<string> BlobManifest { get; set; } = [];
}