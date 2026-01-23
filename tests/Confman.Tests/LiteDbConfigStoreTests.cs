using Confman.Api.Models;
using Confman.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
            Action = "config.created",
            Actor = "test-user",
            Namespace = "test-ns",
            Key = "test-key",
            NewValue = "new-value"
        };

        await _store.AppendAuditAsync(evt);

        var events = await _store.GetAuditEventsAsync("test-ns");
        Assert.Single(events);
        Assert.Equal("config.created", events[0].Action);
    }

    [Fact]
    public async Task GetAuditEventsAsync_ReturnsOrderedByTimestamp()
    {
        var now = DateTimeOffset.UtcNow;

        await _store.AppendAuditAsync(new AuditEvent
        {
            Timestamp = now.AddMinutes(-2),
            Action = "oldest",
            Actor = "user",
            Namespace = "ns"
        });
        await _store.AppendAuditAsync(new AuditEvent
        {
            Timestamp = now,
            Action = "newest",
            Actor = "user",
            Namespace = "ns"
        });
        await _store.AppendAuditAsync(new AuditEvent
        {
            Timestamp = now.AddMinutes(-1),
            Action = "middle",
            Actor = "user",
            Namespace = "ns"
        });

        var events = await _store.GetAuditEventsAsync("ns");

        Assert.Equal(3, events.Count);
        Assert.Equal("newest", events[0].Action);
        Assert.Equal("middle", events[1].Action);
        Assert.Equal("oldest", events[2].Action);
    }

    [Fact]
    public async Task GetAuditEventsAsync_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await _store.AppendAuditAsync(new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i),
                Action = $"action-{i}",
                Actor = "user",
                Namespace = "ns"
            });
        }

        var events = await _store.GetAuditEventsAsync("ns", limit: 3);

        Assert.Equal(3, events.Count);
    }

    #endregion
}
