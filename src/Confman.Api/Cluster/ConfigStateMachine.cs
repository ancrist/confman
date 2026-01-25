using System.Buffers;
using System.Text.Json;
using Confman.Api.Cluster.Commands;
using Confman.Api.Models;
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
    /// Audit events are now created on ALL nodes - storage handles idempotency via upsert.
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

            // Resolve store from DI
            var store = _serviceProvider.GetRequiredService<IConfigStore>();

            // Apply command on all nodes - audit events created everywhere
            // Storage uses upsert for idempotency during log replay
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
    /// Deserializes and restores all configs, namespaces, and audit events.
    /// </summary>
    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        _logger.LogInformation("Restoring from snapshot: {SnapshotFile}", snapshotFile.FullName);

        try
        {
            var json = await File.ReadAllBytesAsync(snapshotFile.FullName, token);
            var snapshot = JsonSerializer.Deserialize<SnapshotData>(json);

            if (snapshot is null)
            {
                _logger.LogError("Failed to deserialize snapshot - null result");
                return;
            }

            if (snapshot.Version != 1)
            {
                _logger.LogError("Unsupported snapshot version: {Version}", snapshot.Version);
                return;
            }

            var store = _serviceProvider.GetRequiredService<IConfigStore>();
            await store.RestoreFromSnapshotAsync(snapshot, token);

            _logger.LogInformation("Snapshot restored: {ConfigCount} configs, {NamespaceCount} namespaces, {AuditCount} audit events",
                snapshot.Configs.Count, snapshot.Namespaces.Count, snapshot.AuditEvents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore from snapshot");
            throw;
        }
    }

    /// <summary>
    /// Persists current state to a snapshot.
    /// Serializes all configs, namespaces, and audit events to JSON.
    /// </summary>
    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        _logger.LogInformation("Creating snapshot");

        try
        {
            var store = _serviceProvider.GetRequiredService<IConfigStore>();

            var snapshot = new SnapshotData
            {
                Version = 1,
                Configs = await store.GetAllConfigsAsync(token),
                Namespaces = (await store.ListNamespacesAsync(token)).ToList(),
                AuditEvents = await store.GetAllAuditEventsAsync(token),
                Timestamp = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            await writer.Invoke(json, token);

            _logger.LogInformation("Snapshot created: {ConfigCount} configs, {NamespaceCount} namespaces, {AuditCount} audit events, {Size} bytes",
                snapshot.Configs.Count, snapshot.Namespaces.Count, snapshot.AuditEvents.Count, json.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create snapshot");
            throw;
        }
    }

    private static ICommand DeserializeCommand(in ReadOnlySequence<byte> payload)
    {
        var reader = new Utf8JsonReader(payload);
        var command = JsonSerializer.Deserialize<ICommand>(ref reader)
            ?? throw new InvalidOperationException("Failed to deserialize command from log entry");
        return command;
    }
}
