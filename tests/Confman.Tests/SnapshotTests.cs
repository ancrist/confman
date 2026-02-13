using Confman.Api.Cluster;
using Confman.Api.Models;
using MessagePack;

namespace Confman.Tests;

/// <summary>
/// Tests for snapshot serialization/deserialization.
/// Verifies backward compatibility (V2) and new V3 format with blob manifest.
/// </summary>
public class SnapshotTests
{
    [Fact]
    public void SnapshotData_DefaultVersion_Is3()
    {
        var snapshot = new SnapshotData();
        Assert.Equal(3, snapshot.Version);
    }

    [Fact]
    public void SnapshotData_SerializesWithBlobManifest()
    {
        var blobId = new string('a', 64);
        var snapshot = new SnapshotData
        {
            Version = 3,
            Configs =
            [
                new ConfigEntry
                {
                    Namespace = "ns",
                    Key = "inline-key",
                    Value = "inline-value",
                    UpdatedBy = "author",
                },
                new ConfigEntry
                {
                    Namespace = "ns",
                    Key = "blob-key",
                    BlobId = blobId,
                    UpdatedBy = "author",
                },
            ],
            Namespaces = [],
            AuditEvents = [],
            BlobManifest = [blobId],
            Timestamp = DateTimeOffset.UtcNow,
        };

        var bytes = MessagePackSerializer.Serialize(snapshot, ConfmanSerializerOptions.Instance);
        var restored = MessagePackSerializer.Deserialize<SnapshotData>(bytes, ConfmanSerializerOptions.Instance);

        Assert.Equal(3, restored.Version);
        Assert.Equal(2, restored.Configs.Count);

        // Inline entry
        var inlineEntry = restored.Configs[0];
        Assert.False(inlineEntry.IsBlobBacked);
        Assert.Equal("inline-value", inlineEntry.Value);
        Assert.Null(inlineEntry.BlobId);

        // Blob-backed entry
        var blobEntry = restored.Configs[1];
        Assert.True(blobEntry.IsBlobBacked);
        Assert.Equal(blobId, blobEntry.BlobId);
        Assert.Null(blobEntry.Value);

        // BlobManifest
        Assert.Single(restored.BlobManifest);
        Assert.Equal(blobId, restored.BlobManifest[0]);
    }

    [Fact]
    public void SnapshotData_V2Snapshot_DeserializesWithNullBlobId()
    {
        // Simulate a V2 snapshot (no BlobId field, no BlobManifest)
        var v2Snapshot = new SnapshotData
        {
            Version = 2,
            Configs =
            [
                new ConfigEntry
                {
                    Namespace = "ns",
                    Key = "key",
                    Value = "value",
                    UpdatedBy = "author",
                },
            ],
            Namespaces = [],
            AuditEvents = [],
            Timestamp = DateTimeOffset.UtcNow,
        };

        // Serialize as V2 (BlobManifest defaults to empty, BlobId defaults to null)
        var bytes = MessagePackSerializer.Serialize(v2Snapshot, ConfmanSerializerOptions.Instance);
        var restored = MessagePackSerializer.Deserialize<SnapshotData>(bytes, ConfmanSerializerOptions.Instance);

        Assert.Equal(2, restored.Version);
        Assert.Single(restored.Configs);
        Assert.False(restored.Configs[0].IsBlobBacked);
        Assert.Null(restored.Configs[0].BlobId);
        Assert.Equal("value", restored.Configs[0].Value);
        Assert.Empty(restored.BlobManifest);
    }

    [Fact]
    public void SnapshotData_BlobBackedEntry_HasNullCompressedValue()
    {
        // Blob-backed entries should not have CompressedValue (they store no inline data)
        var entry = new ConfigEntry
        {
            Namespace = "ns",
            Key = "key",
            BlobId = new string('f', 64),
            UpdatedBy = "author",
        };

        Assert.Null(entry.CompressedValue);
        Assert.Null(entry.Value);
        Assert.True(entry.IsBlobBacked);
    }

    [Fact]
    public void SnapshotData_MixedEntries_CorrectBlobManifest()
    {
        var blobId1 = new string('a', 64);
        var blobId2 = new string('b', 64);

        var configs = new List<ConfigEntry>
        {
            new() { Namespace = "ns", Key = "k1", Value = "v1", UpdatedBy = "a" },
            new() { Namespace = "ns", Key = "k2", BlobId = blobId1, UpdatedBy = "a" },
            new() { Namespace = "ns", Key = "k3", Value = "v3", UpdatedBy = "a" },
            new() { Namespace = "ns", Key = "k4", BlobId = blobId2, UpdatedBy = "a" },
            new() { Namespace = "ns", Key = "k5", BlobId = blobId1, UpdatedBy = "a" }, // duplicate blob
        };

        var manifest = configs
            .Where(c => c.IsBlobBacked)
            .Select(c => c.BlobId!)
            .Distinct()
            .ToList();

        Assert.Equal(2, manifest.Count);
        Assert.Contains(blobId1, manifest);
        Assert.Contains(blobId2, manifest);
    }
}
