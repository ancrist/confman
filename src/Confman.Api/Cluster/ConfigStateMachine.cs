using System.Buffers;
using System.Text.Json;
using Confman.Api.Cluster.Commands;
using Confman.Api.Storage;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.DependencyInjection;

namespace Confman.Api.Cluster;

/// <summary>
/// Raft state machine that applies committed log entries to the config store.
/// Uses SimpleStateMachine for WAL-based persistence. LiteDB handles actual config state.
/// </summary>
public sealed class ConfigStateMachine : SimpleStateMachine
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConfigStateMachine> _logger;

    /// <summary>
    /// Constructor for DI registration via UseStateMachine&lt;T&gt;().
    /// </summary>
    public ConfigStateMachine(IConfiguration configuration, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        : base(GetLogDirectory(configuration))
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<ConfigStateMachine>();
    }

    private static DirectoryInfo GetLogDirectory(IConfiguration configuration)
    {
        var basePath = configuration["Storage:DataPath"] ?? "./data";
        var logPath = Path.Combine(basePath, "raft-log");
        return new DirectoryInfo(Path.GetFullPath(logPath));
    }

    /// <summary>
    /// Called when a log entry is committed and needs to be applied to the state machine.
    /// </summary>
    /// <returns>True if a snapshot should be created after this entry.</returns>
    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (!entry.TryGetPayload(out var payload))
        {
            _logger.LogDebug("Skipping entry at index {Index} - no payload", entry.Index);
            return false;
        }

        try
        {
            var command = DeserializeCommand(payload);

            // Resolve the config store from DI - it's a singleton
            var store = _serviceProvider.GetRequiredService<IConfigStore>();
            await command.ApplyAsync(store, token);

            _logger.LogDebug("Applied {CommandType} at index {Index}, term {Term}",
                command.GetType().Name, entry.Index, entry.Term);

            // Create snapshot every 100 entries for compaction
            return entry.Index % 100 == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply log entry at index {Index}", entry.Index);
            throw;
        }
    }

    /// <summary>
    /// Restores state from a snapshot file.
    /// Since LiteDB handles actual state persistence, we just log the restoration.
    /// </summary>
    protected override ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        _logger.LogInformation("Restoring from snapshot: {SnapshotFile}", snapshotFile.FullName);
        // LiteDB maintains the actual config state - nothing to restore here
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Persists current state to a snapshot.
    /// Since LiteDB handles actual state persistence, we write a minimal marker.
    /// </summary>
    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        _logger.LogDebug("Creating snapshot");
        // Write a simple marker - LiteDB has the actual state
        var marker = System.Text.Encoding.UTF8.GetBytes("CONFMAN_SNAPSHOT");
        await writer.Invoke(marker, token);
    }

    private static ICommand DeserializeCommand(in ReadOnlySequence<byte> payload)
    {
        var reader = new Utf8JsonReader(payload);
        var command = JsonSerializer.Deserialize<ICommand>(ref reader)
            ?? throw new InvalidOperationException("Failed to deserialize command from log entry");
        return command;
    }
}
