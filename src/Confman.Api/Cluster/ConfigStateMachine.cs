using System.Text.Json;
using Confman.Api.Cluster.Commands;
using Confman.Api.Storage;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;

namespace Confman.Api.Cluster;

/// <summary>
/// Raft state machine that applies committed log entries to the config store.
/// Uses MemoryBasedStateMachine - LiteDB handles persistence of the actual state.
/// </summary>
public sealed class ConfigStateMachine : MemoryBasedStateMachine
{
    private readonly IConfigStore _store;
    private readonly ILogger<ConfigStateMachine> _logger;

    public ConfigStateMachine(
        IConfigStore store,
        ILogger<ConfigStateMachine> logger,
        string path)
        : base(path, 50, new Options
        {
            CompactionMode = CompactionMode.Background,
            UseCaching = true
        })
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Called when a log entry is committed and needs to be applied to the state machine.
    /// </summary>
    protected override async ValueTask ApplyAsync(LogEntry entry)
    {
        if (entry.IsSnapshot)
        {
            // For snapshot entries, we don't need to do anything special
            // since LiteDB maintains its own persistence
            _logger.LogDebug("Processing snapshot at index {Index}", entry.Index);
            return;
        }

        if (entry.Length == 0)
        {
            _logger.LogDebug("Skipping empty log entry at index {Index}", entry.Index);
            return;
        }

        try
        {
            var command = await DeserializeCommandAsync(entry);
            await command.ApplyAsync(_store);
            _logger.LogDebug("Applied {CommandType} at index {Index}, term {Term}",
                command.GetType().Name, entry.Index, entry.Term);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply log entry at index {Index}", entry.Index);
            throw;
        }
    }

    /// <summary>
    /// Creates a snapshot builder for log compaction.
    /// Uses InlineSnapshotBuilder - LiteDB handles the actual state persistence.
    /// </summary>
    protected override SnapshotBuilder CreateSnapshotBuilder(in SnapshotBuilderContext context)
    {
        _logger.LogDebug("Creating snapshot builder");
        return new ConfigSnapshotBuilder(context, _logger);
    }

    private static async ValueTask<ICommand> DeserializeCommandAsync(LogEntry entry)
    {
        // Use DotNext's IDataTransferObject extension to read data
        using var stream = new MemoryStream();
        await ((IDataTransferObject)entry).WriteToAsync(stream);
        stream.Position = 0;

        var command = await JsonSerializer.DeserializeAsync<ICommand>(stream)
            ?? throw new InvalidOperationException("Failed to deserialize command from log entry");

        return command;
    }

    /// <summary>
    /// Minimal snapshot builder using InlineSnapshotBuilder.
    /// LiteDB maintains actual state persistence; this just enables log compaction.
    /// </summary>
    private sealed class ConfigSnapshotBuilder : InlineSnapshotBuilder
    {
        private readonly ILogger _logger;

        public ConfigSnapshotBuilder(in SnapshotBuilderContext context, ILogger logger)
            : base(context)
        {
            _logger = logger;
        }

        protected override ValueTask ApplyAsync(LogEntry entry)
        {
            // No-op: LiteDB maintains the actual state
            // This just processes entries for log compaction
            _logger.LogDebug("SnapshotBuilder: Processing entry at index {Index}", entry.Index);
            return ValueTask.CompletedTask;
        }
    }
}
