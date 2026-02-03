using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Confman.Api.Cluster.Commands;
using Confman.Api.Storage;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace Confman.Api.Cluster;

/// <summary>
/// Raft state machine that applies committed log entries to the config store.
/// Uses SimpleStateMachine for WAL-based persistence. LiteDB handles actual config state.
/// </summary>
public sealed class ConfigStateMachine : SimpleStateMachine
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConfigStateMachine> _logger;
    private readonly bool _logDataStoreApplies;

    /// <summary>
    /// Constructor for DI registration via UseStateMachine&lt;T&gt;().
    /// </summary>
    public ConfigStateMachine(IConfiguration configuration, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        : base(GetLogDirectory(configuration))
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<ConfigStateMachine>();
        _logDataStoreApplies = configuration.GetValue<bool>("Api:LogDataStoreApplies", false);
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
            _logger.LogDebug("No payload for log entry at index {Index}", entry.Index);
            return false;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // DEBUG: Log raw payload details
            var payloadBytes = payload.ToArray();
            var firstBytes = payloadBytes.Length > 10 ? payloadBytes[..10] : payloadBytes;
            _logger.LogDebug("Entry {Index}: payload length={Length}, first bytes: {Bytes}",
                entry.Index, payloadBytes.Length, BitConverter.ToString(firstBytes));

            var command = DeserializeCommand(payload);

            // Skip Raft internal entries (no-ops, config changes)
            if (command is null)
            {
                _logger.LogDebug("Skipping non-command entry at index {Index} (Raft internal)", entry.Index);
                return false;
            }

            // Resolve store from DI
            var store = _serviceProvider.GetRequiredService<IConfigStore>();

            // Apply command on all nodes - audit events created everywhere
            // Storage uses upsert for idempotency during log replay
            await command.ApplyAsync(store, token);

            if (_logDataStoreApplies)
            {
                _logger.LogInformation("Applied {CommandType} at index {Index}, term {Term} ({ElapsedMs} ms)",
                    command.GetType().Name, entry.Index, entry.Term, sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogDebug("Applied {CommandType} at index {Index}, term {Term} ({ElapsedMs} ms)",
                    command.GetType().Name, entry.Index, entry.Term, sw.ElapsedMilliseconds);
            }

            // DISABLED: Snapshot creation has a race condition where entries can be
            // committed to WAL but not yet applied to LiteDB when snapshot is taken.
            // This causes data loss when WAL is compacted after snapshot.
            // TODO: Fix by tracking actual applied state vs relying on entry index.
            return false;
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
                throw new InvalidOperationException(
                    $"Unsupported snapshot version: {snapshot.Version}. This node cannot restore from a snapshot " +
                    "created by a newer version. Please upgrade this node or use a compatible snapshot.");
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
                Namespaces = [.. (await store.ListNamespacesAsync(token))],
                AuditEvents = await store.GetAllAuditEventsAsync(token),
                Timestamp = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, SerializerOptions);

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

    private ICommand? DeserializeCommand(in ReadOnlySequence<byte> payload)
    {
        // Skip empty payloads
        if (payload.IsEmpty)
            return null;

        var bytes = payload.ToArray();

        // Find the start of JSON - skip any leading null bytes
        // DotNext's SimpleStateMachine WAL sometimes prepends null bytes to payloads
        var jsonStart = 0;
        while (jsonStart < bytes.Length && bytes[jsonStart] == 0)
        {
            jsonStart++;
        }

        // If all null bytes or empty, skip
        if (jsonStart >= bytes.Length)
            return null;

        // Check if the remaining content starts with '{' (JSON object)
        if (bytes[jsonStart] != (byte)'{')
        {
            // Genuine Raft internal entry (config change, etc.)
            _logger.LogDebug("Skipping non-JSON entry, firstByte=0x{FirstByte:X2} at offset {Offset}",
                bytes[jsonStart], jsonStart);
            return null;
        }

        // If we had to skip null bytes, log it (this is a known DotNext WAL behavior)
        if (jsonStart > 0)
        {
            _logger.LogWarning("Skipped {NullBytes} null bytes before JSON in log entry (DotNext WAL padding)",
                jsonStart);
        }

        try
        {
            // Deserialize from the actual JSON start
            var jsonSpan = bytes.AsSpan(jsonStart);
            return JsonSerializer.Deserialize<ICommand>(jsonSpan);
        }
        catch (JsonException ex)
        {
            var jsonString = System.Text.Encoding.UTF8.GetString(bytes, jsonStart, bytes.Length - jsonStart);
            _logger.LogWarning(ex, "JSON deserialization failed. Payload ({Length} bytes): {Payload}",
                bytes.Length - jsonStart, jsonString.Length > 500 ? jsonString[..500] + "..." : jsonString);
            return null;
        }
    }
}