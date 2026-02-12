using System.Buffers;
using System.Diagnostics;
using System.Runtime;
using Confman.Api.Cluster.Commands;
using Confman.Api.Storage;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using MessagePack;

namespace Confman.Api.Cluster;

/// <summary>
/// Raft state machine that applies committed log entries to the config store.
/// Uses SimpleStateMachine for WAL-based persistence. LiteDB handles actual config state.
/// </summary>
public sealed class ConfigStateMachine : SimpleStateMachine
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConfigStateMachine> _logger;
    private readonly bool _logDataStoreApplies;
    private readonly bool _auditEnabled;
    private readonly int _snapshotInterval;
    private long _entriesSinceSnapshot;

    /// <summary>
    /// Constructor for DI registration via UseStateMachine&lt;T&gt;().
    /// </summary>
    public ConfigStateMachine(IConfiguration configuration, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        : base(GetLogDirectory(configuration))
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<ConfigStateMachine>();
        _logDataStoreApplies = configuration.GetValue<bool>("Api:LogDataStoreApplies", false);
        _auditEnabled = configuration.GetValue<bool>("Audit:Enabled", true);
        _snapshotInterval = configuration.GetValue<int>("Raft:SnapshotInterval", 1000);
        _logger.LogInformation("ConfigStateMachine initialized: snapshot interval={Interval}, audit={AuditEnabled}",
            _snapshotInterval, _auditEnabled);
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
            await command.ApplyAsync(store, _auditEnabled, token);

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

            // Trigger snapshot after every N command entries to compact WAL.
            // We await command.ApplyAsync above, so LiteDB state is guaranteed persisted.
            // BatchCommand counts by inner command count — WAL/LiteDB growth is proportional to commands, not log entries.
            // Safe: DotNext guarantees sequential ApplyAsync — no concurrent access to _entriesSinceSnapshot.
            var commandCount = command is BatchCommand batch ? batch.Commands.Count : 1;
            _entriesSinceSnapshot += commandCount;
            if (_entriesSinceSnapshot >= _snapshotInterval)
            {
                _logger.LogInformation("Triggering snapshot after {Count} entries (index {Index})",
                    _entriesSinceSnapshot, entry.Index);
                _entriesSinceSnapshot = 0;
                return true;
            }

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
    /// Streams from disk to avoid loading the entire snapshot into a single byte[].
    /// </summary>
    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        _logger.LogInformation("Restoring from snapshot: {SnapshotFile} ({Size} bytes)", snapshotFile.FullName, snapshotFile.Length);

        try
        {
            await using var stream = snapshotFile.Open(new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81920,
            });
            var snapshot = await MessagePackSerializer.DeserializeAsync<SnapshotData>(stream, ConfmanSerializerOptions.Instance, token);

            if (snapshot is null)
            {
                _logger.LogError("Failed to deserialize snapshot - null result");
                return;
            }

            if (snapshot.Version is not (2 or 3))
            {
                throw new InvalidOperationException(
                    $"Unsupported snapshot version: {snapshot.Version}. This node supports versions 2 and 3. " +
                    "Please upgrade this node or use a compatible snapshot.");
            }

            var store = _serviceProvider.GetRequiredService<IConfigStore>();
            await store.RestoreFromSnapshotAsync(snapshot, token);

            // Log blob manifest diagnostics for V3 snapshots
            var blobCount = snapshot.BlobManifest.Count;
            if (blobCount > 0)
            {
                _logger.LogInformation(
                    "Snapshot restored with {BlobCount} blob-backed entries. Missing blobs will be fetched on first access",
                    blobCount);
            }

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
    /// Streams JSON through a temp file to avoid allocating the entire serialized payload in memory.
    /// With large datasets (e.g. 500 × 1MB configs), the JSON can exceed 1GB — too large for a single byte[].
    /// </summary>
    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        _logger.LogInformation("Creating snapshot");

        try
        {
            // Snapshot creation in a separate scope ensures the SnapshotData (which can hold GBs
            // of config values) is out of scope before we trigger GC. Without this, the JIT may
            // keep the local alive through the GC.Collect call, preventing collection.
            await CreateAndStreamSnapshotAsync(writer, token);

            // Compact LOH to reclaim the fragmented address space from large byte[] and string
            // allocations created during snapshot serialization.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            _logger.LogDebug("Post-snapshot GC completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create snapshot");
            throw;
        }
    }

    private async ValueTask CreateAndStreamSnapshotAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        var store = _serviceProvider.GetRequiredService<IConfigStore>();

        var configs = await store.GetAllConfigsAsync(token);
        var blobManifest = configs
            .Where(c => c.IsBlobBacked)
            .Select(c => c.BlobId!)
            .Distinct()
            .ToList();

        var snapshot = new SnapshotData
        {
            Version = 3,
            Configs = configs,
            Namespaces = [.. await store.ListNamespacesAsync(token)],
            AuditEvents = await store.GetAllAuditEventsAsync(token),
            Timestamp = DateTimeOffset.UtcNow,
            BlobManifest = blobManifest,
        };

        // Serialize to a temp file, then stream to the writer.
        // MessagePack streams in chunks, avoiding the 1GB+ byte[] allocation.
        var tempFile = Path.GetTempFileName();
        try
        {
            long fileSize;
            await using (var writeStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920))
            {
                await MessagePackSerializer.SerializeAsync(writeStream, snapshot, ConfmanSerializerOptions.Instance, token);
                fileSize = writeStream.Length;
            }

            await using var readStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920);
            await writer.CopyFromAsync(readStream, token: token);

            _logger.LogInformation("Snapshot created: {ConfigCount} configs, {NamespaceCount} namespaces, {AuditCount} audit events, {Size} bytes",
                snapshot.Configs.Count, snapshot.Namespaces.Count, snapshot.AuditEvents.Count, fileSize);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private ICommand? DeserializeCommand(in ReadOnlySequence<byte> payload)
    {
        // Skip empty payloads
        if (payload.IsEmpty)
            return null;

        // DotNext WAL may prepend null bytes (0x00) as padding under concurrent load.
        // Skip them to find the actual MessagePack payload start.
        // Use SequenceReader to avoid allocating a byte[] copy on LOH for large entries.
        var reader = new SequenceReader<byte>(payload);
        while (!reader.End && reader.TryPeek(out var b) && b == 0x00)
            reader.Advance(1);

        if (reader.End)
        {
            _logger.LogDebug("Skipping null-only entry ({Length} bytes)", payload.Length);
            return null;
        }

        var trimmed = payload.Slice(reader.Position);

        try
        {
            return MessagePackSerializer.Deserialize<ICommand>(trimmed, ConfmanSerializerOptions.Instance);
        }
        catch (MessagePackSerializationException ex)
        {
            _logger.LogWarning(ex, "MessagePack deserialization failed ({Length} bytes, offset {Offset})",
                payload.Length, reader.Consumed);
            return null;
        }
    }
}