using System.Text.Json;
using Confman.Api.Cluster;
using Confman.Api.Cluster.Commands;
using Confman.Api.Models;
using Confman.Api.Storage;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Confman.Tests;

/// <summary>
/// Unit tests for LiteDbConfigStore.
/// Uses temporary directories for each test to ensure isolation.
/// </summary>
public class LiteDbConfigStoreTests : IDisposable
{
    private readonly string _tempPath;
    private readonly LiteDbConfigStore _store;

    public LiteDbConfigStoreTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"confman-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempPath);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataPath"] = _tempPath
            })
            .Build();

        _store = new LiteDbConfigStore(config, NullLogger<LiteDbConfigStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }

    #region Config Operations

    [Fact]
    public async Task SetAsync_CreatesNewEntry()
    {
        var entry = new ConfigEntry
        {
            Namespace = "test-ns",
            Key = "test-key",
            Value = "test-value",
            UpdatedBy = "test-user"
        };

        await _store.SetAsync(entry);

        var result = await _store.GetAsync("test-ns", "test-key");
        Assert.NotNull(result);
        Assert.Equal("test-value", result.Value);
        Assert.Equal(1, result.Version);
    }

    [Fact]
    public async Task SetAsync_UpdatesExistingEntry_IncrementsVersion()
    {
        var entry = new ConfigEntry
        {
            Namespace = "test-ns",
            Key = "test-key",
            Value = "value-v1",
            UpdatedBy = "test-user"
        };

        await _store.SetAsync(entry);

        entry.Value = "value-v2";
        await _store.SetAsync(entry);

        var result = await _store.GetAsync("test-ns", "test-key");
        Assert.NotNull(result);
        Assert.Equal("value-v2", result.Value);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _store.GetAsync("nonexistent", "key");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsEntriesInNamespace()
    {
        await _store.SetAsync(new ConfigEntry { Namespace = "ns1", Key = "key1", Value = "v1", UpdatedBy = "u" });
        await _store.SetAsync(new ConfigEntry { Namespace = "ns1", Key = "key2", Value = "v2", UpdatedBy = "u" });
        await _store.SetAsync(new ConfigEntry { Namespace = "ns2", Key = "key3", Value = "v3", UpdatedBy = "u" });

        var ns1Entries = await _store.ListAsync("ns1");
        var ns2Entries = await _store.ListAsync("ns2");

        Assert.Equal(2, ns1Entries.Count);
        Assert.Single(ns2Entries);
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry()
    {
        await _store.SetAsync(new ConfigEntry { Namespace = "ns", Key = "key", Value = "value", UpdatedBy = "u" });

        await _store.DeleteAsync("ns", "key");

        var result = await _store.GetAsync("ns", "key");
        Assert.Null(result);
    }

    #endregion

    #region Namespace Operations

    [Fact]
    public async Task SetNamespaceAsync_CreatesNewNamespace()
    {
        var ns = new Namespace
        {
            Path = "my-namespace",
            Description = "Test namespace",
            Owner = "test-user",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _store.SetNamespaceAsync(ns);

        var result = await _store.GetNamespaceAsync("my-namespace");
        Assert.NotNull(result);
        Assert.Equal("Test namespace", result.Description);
        Assert.Equal("test-user", result.Owner);
    }

    [Fact]
    public async Task SetNamespaceAsync_UpdatesExisting_PreservesCreatedAt()
    {
        var originalTime = DateTimeOffset.UtcNow.AddDays(-1);
        var ns = new Namespace
        {
            Path = "my-namespace",
            Description = "Original",
            Owner = "user1",
            CreatedAt = originalTime
        };

        await _store.SetNamespaceAsync(ns);

        ns.Description = "Updated";
        ns.CreatedAt = DateTimeOffset.UtcNow; // Try to change it
        await _store.SetNamespaceAsync(ns);

        var result = await _store.GetNamespaceAsync("my-namespace");
        Assert.NotNull(result);
        Assert.Equal("Updated", result.Description);
        // LiteDB loses sub-millisecond precision, so compare within 1 second tolerance
        Assert.True(Math.Abs((result.CreatedAt - originalTime).TotalSeconds) < 1,
            $"CreatedAt should be preserved. Expected: {originalTime}, Actual: {result.CreatedAt}");
    }

    [Fact]
    public async Task ListNamespacesAsync_ReturnsAllNamespaces()
    {
        await _store.SetNamespaceAsync(new Namespace { Path = "ns1", Owner = "u1" });
        await _store.SetNamespaceAsync(new Namespace { Path = "ns2", Owner = "u2" });

        var namespaces = await _store.ListNamespacesAsync();

        Assert.Equal(2, namespaces.Count);
    }

    [Fact]
    public async Task DeleteNamespaceAsync_RemovesNamespace()
    {
        await _store.SetNamespaceAsync(new Namespace { Path = "ns", Owner = "user" });

        await _store.DeleteNamespaceAsync("ns");

        var result = await _store.GetNamespaceAsync("ns");
        Assert.Null(result);
    }

    #endregion

    #region Audit Operations

    [Fact]
    public async Task AppendAuditAsync_StoresEvent()
    {
        var evt = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = AuditAction.ConfigCreated,
            Actor = "test-user",
            Namespace = "test-ns",
            Key = "test-key",
            NewValue = "new-value"
        };

        await _store.AppendAuditAsync(evt);

        var events = await _store.GetAuditEventsAsync("test-ns");
        Assert.Single(events);
        Assert.Equal(AuditAction.ConfigCreated, events[0].Action);
    }

    [Fact]
    public async Task GetAuditEventsAsync_ReturnsOrderedByTimestamp()
    {
        // Truncate to milliseconds — LiteDB stores BSON DateTime at ms precision
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));

        await _store.AppendAuditAsync(new AuditEvent
        {
            Timestamp = now.AddMinutes(-2),
            Action = AuditAction.ConfigCreated,
            Actor = "user",
            Namespace = "ns"
        });
        await _store.AppendAuditAsync(new AuditEvent
        {
            Timestamp = now,
            Action = AuditAction.ConfigDeleted,
            Actor = "user",
            Namespace = "ns"
        });
        await _store.AppendAuditAsync(new AuditEvent
        {
            Timestamp = now.AddMinutes(-1),
            Action = AuditAction.ConfigUpdated,
            Actor = "user",
            Namespace = "ns"
        });

        var events = await _store.GetAuditEventsAsync("ns");

        Assert.Equal(3, events.Count);
        // Ordered by timestamp descending: newest first
        Assert.Equal(now, events[0].Timestamp);
        Assert.Equal(now.AddMinutes(-1), events[1].Timestamp);
        Assert.Equal(now.AddMinutes(-2), events[2].Timestamp);
    }

    [Fact]
    public async Task GetAuditEventsAsync_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await _store.AppendAuditAsync(new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i),
                Action = AuditAction.ConfigUpdated,
                Actor = "user",
                Namespace = "ns"
            });
        }

        var events = await _store.GetAuditEventsAsync("ns", limit: 3);

        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task AppendAuditAsync_UpsertPreventsduplicates()
    {
        // Same ID should result in upsert (no duplicates)
        var id = ObjectId.NewObjectId();
        var evt1 = new AuditEvent
        {
            Id = id,
            Timestamp = DateTimeOffset.UtcNow,
            Action = AuditAction.ConfigCreated,
            Actor = "user",
            Namespace = "ns",
            Key = "key",
            NewValue = "value1"
        };

        var evt2 = new AuditEvent
        {
            Id = id, // Same ID
            Timestamp = DateTimeOffset.UtcNow,
            Action = AuditAction.ConfigCreated,
            Actor = "user",
            Namespace = "ns",
            Key = "key",
            NewValue = "value2" // Different value
        };

        await _store.AppendAuditAsync(evt1);
        await _store.AppendAuditAsync(evt2);

        var events = await _store.GetAuditEventsAsync("ns");
        Assert.Single(events); // Only one event due to upsert
        Assert.Equal("value2", events[0].NewValue); // Second value wins
    }

    #endregion

    #region Snapshot Operations

    [Fact]
    public async Task GetAllConfigsAsync_ReturnsAllConfigs()
    {
        await _store.SetAsync(new ConfigEntry { Namespace = "ns1", Key = "key1", Value = "v1", UpdatedBy = "u" });
        await _store.SetAsync(new ConfigEntry { Namespace = "ns2", Key = "key2", Value = "v2", UpdatedBy = "u" });

        var configs = await _store.GetAllConfigsAsync();

        Assert.Equal(2, configs.Count);
    }

    [Fact]
    public async Task GetAllAuditEventsAsync_ReturnsAllAuditEvents()
    {
        await _store.AppendAuditAsync(new AuditEvent { Timestamp = DateTimeOffset.UtcNow, Action = AuditAction.ConfigCreated, Actor = "u", Namespace = "ns1" });
        await _store.AppendAuditAsync(new AuditEvent { Timestamp = DateTimeOffset.UtcNow, Action = AuditAction.ConfigUpdated, Actor = "u", Namespace = "ns2" });

        var events = await _store.GetAllAuditEventsAsync();

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task RestoreFromSnapshotAsync_ClearsAndRestoresData()
    {
        // Create existing data
        await _store.SetAsync(new ConfigEntry { Namespace = "old", Key = "key", Value = "old-value", UpdatedBy = "u" });
        await _store.SetNamespaceAsync(new Namespace { Path = "old-ns", Owner = "u" });
        await _store.AppendAuditAsync(new AuditEvent { Timestamp = DateTimeOffset.UtcNow, Action = AuditAction.ConfigCreated, Actor = "u", Namespace = "old" });

        // Create snapshot data
        var snapshot = new SnapshotData
        {
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow,
            Configs =
            [
                new ConfigEntry { Namespace = "new", Key = "key1", Value = "v1", Version = 1, UpdatedBy = "u" },
                new ConfigEntry { Namespace = "new", Key = "key2", Value = "v2", Version = 1, UpdatedBy = "u" }
            ],
            Namespaces =
            [
                new Namespace { Path = "new-ns", Owner = "owner" }
            ],
            AuditEvents =
            [
                new AuditEvent { Timestamp = DateTimeOffset.UtcNow, Action = AuditAction.NamespaceCreated, Actor = "u", Namespace = "new" }
            ]
        };

        // Restore
        await _store.RestoreFromSnapshotAsync(snapshot);

        // Verify old data is gone
        Assert.Null(await _store.GetAsync("old", "key"));
        Assert.Null(await _store.GetNamespaceAsync("old-ns"));
        Assert.Empty(await _store.GetAuditEventsAsync("old"));

        // Verify new data exists
        var configs = await _store.GetAllConfigsAsync();
        Assert.Equal(2, configs.Count);

        var namespaces = await _store.ListNamespacesAsync();
        Assert.Single(namespaces);
        Assert.Equal("new-ns", namespaces[0].Path);

        var events = await _store.GetAuditEventsAsync("new");
        Assert.Single(events);
        Assert.Equal(AuditAction.NamespaceCreated, events[0].Action);
    }

    #endregion

    #region AuditIdGenerator

    [Fact]
    public void AuditIdGenerator_ProducesDeterministicIds()
    {
        var timestamp = DateTimeOffset.Parse("2026-01-26T10:00:00Z");

        var id1 = AuditIdGenerator.Generate(timestamp, "ns", "key", AuditAction.ConfigCreated);
        var id2 = AuditIdGenerator.Generate(timestamp, "ns", "key", AuditAction.ConfigCreated);

        Assert.Equal(id1, id2); // Same inputs = same ID
    }

    [Fact]
    public void AuditIdGenerator_IgnoresActionForIdempotency()
    {
        // Action is NOT included in the ID - this ensures replays are idempotent
        // even when action changes from "created" to "updated"
        var timestamp = DateTimeOffset.Parse("2026-01-26T10:00:00Z");

        var id1 = AuditIdGenerator.Generate(timestamp, "ns", "key", AuditAction.ConfigCreated);
        var id2 = AuditIdGenerator.Generate(timestamp, "ns", "key", AuditAction.ConfigUpdated);

        Assert.Equal(id1, id2); // Same ID despite different action
    }

    [Fact]
    public void AuditIdGenerator_ProducesDifferentIdsForDifferentInputs()
    {
        var timestamp = DateTimeOffset.Parse("2026-01-26T10:00:00Z");

        var id1 = AuditIdGenerator.Generate(timestamp, "ns", "key1", AuditAction.ConfigCreated);
        var id2 = AuditIdGenerator.Generate(timestamp, "ns", "key2", AuditAction.ConfigCreated);
        var id3 = AuditIdGenerator.Generate(timestamp.AddSeconds(1), "ns", "key1", AuditAction.ConfigCreated);

        Assert.NotEqual(id1, id2); // Different key
        Assert.NotEqual(id1, id3); // Different timestamp
    }

    #endregion

    #region AuditAction Serialization

    [Fact]
    public void AuditAction_JsonRoundTrip_SerializesAsFlatString()
    {
        var evt = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = AuditAction.ConfigCreated,
            Actor = "user",
            Namespace = "ns"
        };

        var json = JsonSerializer.Serialize(evt);
        Assert.Contains("\"config.created\"", json);

        var deserialized = JsonSerializer.Deserialize<AuditEvent>(json)!;
        Assert.Equal(AuditAction.ConfigCreated, deserialized.Action);
        Assert.Equal("config", deserialized.Action.ResourceType);
        Assert.Equal("created", deserialized.Action.Verb);
    }

    [Fact]
    public void AuditAction_JsonBackwardCompat_DeserializesPlainString()
    {
        // Simulate a JSON snapshot from the old format where Action was a string
        var json = """{"Timestamp":"2026-01-26T10:00:00+00:00","Action":"namespace.deleted","Actor":"user","Namespace":"ns","Key":null,"OldValue":null,"NewValue":null}""";

        var evt = JsonSerializer.Deserialize<AuditEvent>(json)!;
        Assert.Equal(AuditAction.NamespaceDeleted, evt.Action);
        Assert.Equal("namespace", evt.Action.ResourceType);
        Assert.Equal("deleted", evt.Action.Verb);
    }

    [Fact]
    public async Task AuditAction_BsonRoundTrip_SerializesAsFlatString()
    {
        var evt = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = AuditAction.NamespaceUpdated,
            Actor = "user",
            Namespace = "test-bson"
        };

        await _store.AppendAuditAsync(evt);

        var events = await _store.GetAuditEventsAsync("test-bson");
        Assert.Single(events);
        Assert.Equal(AuditAction.NamespaceUpdated, events[0].Action);
        Assert.Equal("namespace", events[0].Action.ResourceType);
        Assert.Equal("updated", events[0].Action.Verb);
    }

    [Fact]
    public void AuditAction_Parse_RoundTripsAllKnownActions()
    {
        var actions = new[]
        {
            AuditAction.ConfigCreated, AuditAction.ConfigUpdated, AuditAction.ConfigDeleted,
            AuditAction.NamespaceCreated, AuditAction.NamespaceUpdated, AuditAction.NamespaceDeleted
        };

        foreach (var action in actions)
        {
            var parsed = AuditAction.Parse(action.ToString());
            Assert.Equal(action, parsed);
        }
    }

    [Fact]
    public void AuditAction_Parse_ThrowsOnInvalidFormat()
    {
        Assert.Throws<FormatException>(() => AuditAction.Parse("noDotHere"));
    }

    [Fact]
    public void AuditAction_ValueEquality_Works()
    {
        var a = AuditAction.ConfigCreated;
        var b = AuditAction.Parse("config.created");
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    #endregion
}
