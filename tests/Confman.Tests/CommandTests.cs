using System.Text.Json;
using Confman.Api.Cluster.Commands;
using Confman.Api.Models;
using Confman.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confman.Tests;

/// <summary>
/// Unit tests for Raft commands.
/// Verifies that commands apply correctly to the config store.
/// </summary>
public class CommandTests : IDisposable
{
    private readonly string _tempPath;
    private readonly LiteDbConfigStore _store;

    public CommandTests()
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

    #region SetConfigCommand

    [Fact]
    public async Task SetConfigCommand_CreatesNewConfig_WithAudit()
    {
        var command = new SetConfigCommand
        {
            Namespace = "test-ns",
            Key = "test-key",
            Value = "test-value",
            Author = "test-author"
        };

        await command.ApplyAsync(_store);

        var entry = await _store.GetAsync("test-ns", "test-key");
        Assert.NotNull(entry);
        Assert.Equal("test-value", entry.Value);

        var audit = await _store.GetAuditEventsAsync("test-ns");
        Assert.Single(audit);
        Assert.Equal("config.created", audit[0].Action);
        Assert.Equal("test-author", audit[0].Actor);
    }

    [Fact]
    public async Task SetConfigCommand_UpdatesExistingConfig_WithAudit()
    {
        // Create initial entry
        var createCmd = new SetConfigCommand
        {
            Namespace = "test-ns",
            Key = "test-key",
            Value = "value-v1",
            Author = "author1"
        };
        await createCmd.ApplyAsync(_store);

        // Update entry
        var updateCmd = new SetConfigCommand
        {
            Namespace = "test-ns",
            Key = "test-key",
            Value = "value-v2",
            Author = "author2"
        };
        await updateCmd.ApplyAsync(_store);

        var entry = await _store.GetAsync("test-ns", "test-key");
        Assert.Equal("value-v2", entry!.Value);

        var audit = await _store.GetAuditEventsAsync("test-ns");
        Assert.Equal(2, audit.Count);
        Assert.Equal("config.updated", audit[0].Action);
        Assert.Equal("value-v1", audit[0].OldValue);
        Assert.Equal("value-v2", audit[0].NewValue);
    }

    #endregion

    #region DeleteConfigCommand

    [Fact]
    public async Task DeleteConfigCommand_DeletesExistingConfig_WithAudit()
    {
        // Create entry first
        await new SetConfigCommand
        {
            Namespace = "test-ns",
            Key = "test-key",
            Value = "test-value",
            Author = "creator"
        }.ApplyAsync(_store);

        // Delete it
        var deleteCmd = new DeleteConfigCommand
        {
            Namespace = "test-ns",
            Key = "test-key",
            Author = "deleter"
        };
        await deleteCmd.ApplyAsync(_store);

        var entry = await _store.GetAsync("test-ns", "test-key");
        Assert.Null(entry);

        var audit = await _store.GetAuditEventsAsync("test-ns");
        Assert.Equal(2, audit.Count);
        Assert.Equal("config.deleted", audit[0].Action);
        Assert.Equal("test-value", audit[0].OldValue);
        Assert.Null(audit[0].NewValue);
    }

    [Fact]
    public async Task DeleteConfigCommand_NonExistent_NoOp()
    {
        var deleteCmd = new DeleteConfigCommand
        {
            Namespace = "nonexistent",
            Key = "key",
            Author = "user"
        };

        await deleteCmd.ApplyAsync(_store);

        var audit = await _store.GetAuditEventsAsync("nonexistent");
        Assert.Empty(audit); // No audit event for non-existent delete
    }

    #endregion

    #region SetNamespaceCommand

    [Fact]
    public async Task SetNamespaceCommand_CreatesNamespace_WithAudit()
    {
        var command = new SetNamespaceCommand
        {
            Path = "my-namespace",
            Description = "Test namespace",
            Owner = "owner",
            Author = "creator"
        };

        await command.ApplyAsync(_store);

        var ns = await _store.GetNamespaceAsync("my-namespace");
        Assert.NotNull(ns);
        Assert.Equal("Test namespace", ns.Description);
        Assert.Equal("owner", ns.Owner);

        var audit = await _store.GetAuditEventsAsync("my-namespace");
        Assert.Single(audit);
        Assert.Equal("namespace.created", audit[0].Action);
    }

    [Fact]
    public async Task SetNamespaceCommand_UpdatesNamespace_WithAudit()
    {
        // Create
        await new SetNamespaceCommand
        {
            Path = "my-namespace",
            Description = "Original",
            Owner = "owner",
            Author = "creator"
        }.ApplyAsync(_store);

        // Update
        await new SetNamespaceCommand
        {
            Path = "my-namespace",
            Description = "Updated",
            Owner = "owner",
            Author = "updater"
        }.ApplyAsync(_store);

        var ns = await _store.GetNamespaceAsync("my-namespace");
        Assert.Equal("Updated", ns!.Description);

        var audit = await _store.GetAuditEventsAsync("my-namespace");
        Assert.Equal(2, audit.Count);
        Assert.Equal("namespace.updated", audit[0].Action);
        Assert.Equal("Original", audit[0].OldValue);
        Assert.Equal("Updated", audit[0].NewValue);
    }

    #endregion

    #region DeleteNamespaceCommand

    [Fact]
    public async Task DeleteNamespaceCommand_DeletesNamespace_WithAudit()
    {
        // Create first
        await new SetNamespaceCommand
        {
            Path = "my-namespace",
            Description = "To delete",
            Owner = "owner",
            Author = "creator"
        }.ApplyAsync(_store);

        // Delete
        await new DeleteNamespaceCommand
        {
            Path = "my-namespace",
            Author = "deleter"
        }.ApplyAsync(_store);

        var ns = await _store.GetNamespaceAsync("my-namespace");
        Assert.Null(ns);

        var audit = await _store.GetAuditEventsAsync("my-namespace");
        Assert.Equal(2, audit.Count);
        Assert.Equal("namespace.deleted", audit[0].Action);
    }

    #endregion

    #region JSON Serialization (Command Pattern)

    [Fact]
    public void Commands_SerializeWithPolymorphism()
    {
        // Test that ICommand polymorphism works with JSON
        ICommand command = new SetConfigCommand
        {
            Namespace = "ns",
            Key = "key",
            Value = "value",
            Author = "author"
        };

        var json = JsonSerializer.Serialize(command);
        var deserialized = JsonSerializer.Deserialize<ICommand>(json);

        Assert.IsType<SetConfigCommand>(deserialized);
        var setCmd = (SetConfigCommand)deserialized!;
        Assert.Equal("ns", setCmd.Namespace);
        Assert.Equal("key", setCmd.Key);
        Assert.Equal("value", setCmd.Value);
    }

    [Fact]
    public void AllCommandTypes_SerializeCorrectly()
    {
        var commands = new ICommand[]
        {
            new SetConfigCommand { Namespace = "n", Key = "k", Value = "v", Author = "a" },
            new DeleteConfigCommand { Namespace = "n", Key = "k", Author = "a" },
            new SetNamespaceCommand { Path = "p", Owner = "o", Author = "a" },
            new DeleteNamespaceCommand { Path = "p", Author = "a" }
        };

        foreach (var command in commands)
        {
            var json = JsonSerializer.Serialize(command);
            var deserialized = JsonSerializer.Deserialize<ICommand>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(command.GetType(), deserialized.GetType());
        }
    }

    #endregion
}
