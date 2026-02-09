using System.Text.Json;
using Confman.Api.Cluster.Commands;
using Confman.Api.Models;
using Confman.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confman.Tests;

public class BatchCommandTests : IDisposable
{
    private readonly string _tempPath;
    private readonly LiteDbConfigStore _store;

    public BatchCommandTests()
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
            Directory.Delete(_tempPath, recursive: true);
    }

    [Fact]
    public async Task BatchCommand_AppliesAllCommands_Sequentially()
    {
        var batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "key1", Value = "val1", Author = "author" },
                new SetConfigCommand { Namespace = "ns", Key = "key2", Value = "val2", Author = "author" },
                new SetConfigCommand { Namespace = "ns", Key = "key3", Value = "val3", Author = "author" }
            ]
        };

        await batch.ApplyAsync(_store);

        var entry1 = await _store.GetAsync("ns", "key1");
        var entry2 = await _store.GetAsync("ns", "key2");
        var entry3 = await _store.GetAsync("ns", "key3");

        Assert.NotNull(entry1);
        Assert.NotNull(entry2);
        Assert.NotNull(entry3);
        Assert.Equal("val1", entry1.Value);
        Assert.Equal("val2", entry2.Value);
        Assert.Equal("val3", entry3.Value);
    }

    [Fact]
    public async Task BatchCommand_PreservesOrdering_LastWriterWins()
    {
        var batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "key", Value = "first", Author = "author" },
                new SetConfigCommand { Namespace = "ns", Key = "key", Value = "second", Author = "author" },
                new SetConfigCommand { Namespace = "ns", Key = "key", Value = "third", Author = "author" }
            ]
        };

        await batch.ApplyAsync(_store);

        var entry = await _store.GetAsync("ns", "key");
        Assert.NotNull(entry);
        Assert.Equal("third", entry.Value);
        Assert.Equal(3, entry.Version);
    }

    [Fact]
    public async Task BatchCommand_MixedCommandTypes()
    {
        // Pre-create a namespace
        await new SetNamespaceCommand { Path = "ns", Owner = "owner", Author = "author" }
            .ApplyAsync(_store, auditEnabled: false);

        var batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "keep", Value = "val", Author = "author" },
                new SetConfigCommand { Namespace = "ns", Key = "delete-me", Value = "temp", Author = "author" },
                new DeleteConfigCommand { Namespace = "ns", Key = "delete-me", Author = "author" }
            ]
        };

        await batch.ApplyAsync(_store, auditEnabled: false);

        var kept = await _store.GetAsync("ns", "keep");
        var deleted = await _store.GetAsync("ns", "delete-me");

        Assert.NotNull(kept);
        Assert.Equal("val", kept.Value);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task BatchCommand_PerCommandErrorIsolation_OtherCommandsSucceed()
    {
        // Command 2 deletes a non-existent key (no-op, not an error).
        // We test that the batch continues even if inner commands have no effect.
        var batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "key1", Value = "val1", Author = "author" },
                new DeleteConfigCommand { Namespace = "ns", Key = "nonexistent", Author = "author" },
                new SetConfigCommand { Namespace = "ns", Key = "key2", Value = "val2", Author = "author" }
            ]
        };

        await batch.ApplyAsync(_store, auditEnabled: false);

        var entry1 = await _store.GetAsync("ns", "key1");
        var entry2 = await _store.GetAsync("ns", "key2");

        Assert.NotNull(entry1);
        Assert.NotNull(entry2);
        Assert.Equal("val1", entry1.Value);
        Assert.Equal("val2", entry2.Value);
    }

    [Fact]
    public async Task BatchCommand_WithAudit_CreatesAuditEventsForAllCommands()
    {
        await new SetNamespaceCommand { Path = "ns", Owner = "owner", Author = "author" }
            .ApplyAsync(_store, auditEnabled: false);

        var batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "key1", Value = "val1", Author = "author" },
                new SetConfigCommand { Namespace = "ns", Key = "key2", Value = "val2", Author = "author" }
            ]
        };

        await batch.ApplyAsync(_store, auditEnabled: true);

        var audits = await _store.GetAuditEventsAsync("ns");
        Assert.Equal(2, audits.Count);
    }

    [Fact]
    public async Task BatchCommand_EmptyBatch_NoOp()
    {
        var batch = new BatchCommand { Commands = [] };
        await batch.ApplyAsync(_store);
        // No exception, no side effects
    }

    [Fact]
    public async Task BatchCommand_SingleCommand_BehavesLikeDirectApply()
    {
        var batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "key", Value = "val", Author = "author" }
            ]
        };

        await batch.ApplyAsync(_store, auditEnabled: false);

        var entry = await _store.GetAsync("ns", "key");
        Assert.NotNull(entry);
        Assert.Equal("val", entry.Value);
        Assert.Equal(1, entry.Version);
    }

    #region Serialization Round-trip

    [Fact]
    public void BatchCommand_JsonRoundTrip_PreservesAllCommands()
    {
        ICommand batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "key1", Value = "val1", Type = "string", Author = "author" },
                new DeleteConfigCommand { Namespace = "ns", Key = "key2", Author = "author" },
                new SetNamespaceCommand { Path = "ns2", Owner = "owner", Author = "author" },
                new DeleteNamespaceCommand { Path = "ns3", Author = "author" }
            ]
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch);
        var deserialized = JsonSerializer.Deserialize<ICommand>(bytes);

        Assert.NotNull(deserialized);
        var result = Assert.IsType<BatchCommand>(deserialized);
        Assert.Equal(4, result.Commands.Count);
        Assert.IsType<SetConfigCommand>(result.Commands[0]);
        Assert.IsType<DeleteConfigCommand>(result.Commands[1]);
        Assert.IsType<SetNamespaceCommand>(result.Commands[2]);
        Assert.IsType<DeleteNamespaceCommand>(result.Commands[3]);

        var setCmd = (SetConfigCommand)result.Commands[0];
        Assert.Equal("ns", setCmd.Namespace);
        Assert.Equal("key1", setCmd.Key);
        Assert.Equal("val1", setCmd.Value);
    }

    [Fact]
    public void BatchCommand_EstimatedBytes_SumsInnerCommands()
    {
        var batch = new BatchCommand
        {
            Commands =
            [
                new SetConfigCommand { Namespace = "ns", Key = "k", Value = new string('x', 1000), Author = "a" },
                new SetConfigCommand { Namespace = "ns", Key = "k", Value = new string('y', 500), Author = "a" },
                new DeleteConfigCommand { Namespace = "ns", Key = "k", Author = "a" }
            ]
        };

        // SetConfig: 64 + value.Length, DeleteConfig: default 64
        Assert.Equal(64 + 1000 + 64 + 500 + 64, batch.EstimatedBytes);
    }

    #endregion
}
