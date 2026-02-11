using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Confman.Api.Cluster.Commands;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using MessagePack;
using Microsoft.AspNetCore.Connections;

namespace Confman.Api.Cluster;

/// <summary>
/// IRaftService implementation that batches multiple commands into a single Raft log entry.
/// One consensus round-trip commits N operations, amortizing quorum cost under concurrent load.
///
/// Callers (controllers) see the same IRaftService interface. The batching is transparent.
///
/// Safe: DotNext guarantees sequential ApplyAsync on the state machine, so BatchCommand's
/// inner loop cannot race with other applies.
/// </summary>
public sealed class BatchingRaftService : IRaftService, IAsyncDisposable
{
    private readonly IRaftCluster _cluster;
    private readonly ILogger<BatchingRaftService> _logger;
    private readonly Channel<PendingCommand> _queue;
    private readonly Task _flushLoop;
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly int _maxBatchSize;
    private readonly int _maxBatchWaitMs;
    private readonly int _maxBatchBytes;

    // Reusable buffer for MessagePack serialization — avoids LOH byte[] allocations.
    // Safe: only accessed from the single-reader flush loop.
    private readonly ArrayBufferWriter<byte> _serializeBuffer = new(1024);

    public BatchingRaftService(IRaftCluster cluster, ILogger<BatchingRaftService> logger, IConfiguration configuration)
    {
        _cluster = cluster;
        _logger = logger;

        _maxBatchSize = configuration.GetValue("Raft:BatchMaxSize", 50);
        _maxBatchWaitMs = configuration.GetValue("Raft:BatchMaxWaitMs", 1);
        _maxBatchBytes = configuration.GetValue("Raft:BatchMaxBytes", 4 * 1024 * 1024);

        _logger.LogInformation(
            "BatchingRaftService initialized: maxSize={MaxSize}, maxWaitMs={MaxWaitMs}, maxBytes={MaxBytes}",
            _maxBatchSize, _maxBatchWaitMs, _maxBatchBytes);

        // Bounded channel provides back-pressure if flush loop can't keep up
        _queue = Channel.CreateBounded<PendingCommand>(new BoundedChannelOptions(_maxBatchSize * 10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        _flushLoop = Task.Run(() => FlushLoopAsync(_shutdownCts.Token));
    }

    public bool IsLeader => !_cluster.LeadershipToken.IsCancellationRequested &&
                            _cluster.Leader?.EndPoint?.Equals(_cluster.Members
                                .FirstOrDefault(m => !m.IsRemote)?.EndPoint) == true;

    public Uri? LeaderUri
    {
        get
        {
            var leader = _cluster.Leader;
            if (leader is null)
                return null;

            return leader.EndPoint switch
            {
                UriEndPoint uriEndPoint => uriEndPoint.Uri,
                System.Net.IPEndPoint ipEndPoint => new Uri($"http://{ipEndPoint.Address}:{ipEndPoint.Port}"),
                System.Net.DnsEndPoint dnsEndPoint => new Uri($"http://{dnsEndPoint.Host}:{dnsEndPoint.Port}"),
                _ => null
            };
        }
    }

    public async Task<bool> ReplicateAsync(ICommand command, CancellationToken ct = default)
    {
        if (!IsLeader)
        {
            _logger.LogWarning("Attempted to replicate on non-leader node");
            return false;
        }

        var pending = new PendingCommand(command, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        try
        {
            await _queue.Writer.WriteAsync(pending, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Replication cancelled by client before enqueue");
            throw;
        }

        // Caller suspends here until the flush loop processes the batch
        return await pending.Completion.Task.WaitAsync(ct);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Batch flush loop started");

        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                var batch = CollectBatch();
                if (batch.Count == 0)
                    continue;

                var success = await ReplicateBatchAsync(batch, ct);

                foreach (var pending in batch)
                    pending.Completion.TrySetResult(success);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Batch flush loop stopping (shutdown)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch flush loop crashed");
        }
        finally
        {
            // Drain any remaining commands and fail them
            DrainAndFail();
            _logger.LogInformation("Batch flush loop stopped");
        }
    }

    private List<PendingCommand> CollectBatch()
    {
        var batch = new List<PendingCommand>(_maxBatchSize);
        var batchBytes = 0;

        // Drain available commands up to limits
        // First item is guaranteed available since WaitToReadAsync returned true
        while (batch.Count < _maxBatchSize
               && batchBytes < _maxBatchBytes
               && _queue.Reader.TryRead(out var pending))
        {
            batch.Add(pending);
            batchBytes += pending.Command.EstimatedBytes;
        }

        // If we got items but haven't hit limits, wait briefly for more to arrive
        if (batch.Count < _maxBatchSize && batchBytes < _maxBatchBytes && _maxBatchWaitMs > 0)
        {
            var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * _maxBatchWaitMs / 1000);
            while (batch.Count < _maxBatchSize
                   && batchBytes < _maxBatchBytes
                   && Stopwatch.GetTimestamp() < deadline)
            {
                if (_queue.Reader.TryRead(out var extra))
                {
                    batch.Add(extra);
                    batchBytes += extra.Command.EstimatedBytes;
                }
                else
                {
                    Thread.SpinWait(100);
                }
            }
        }

        return batch;
    }

    private async Task<bool> ReplicateBatchAsync(List<PendingCommand> batch, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Wrap in BatchCommand if multiple, or send directly if single
            // Explicit type required — ternary returns different concrete types
#pragma warning disable IDE0007
            ICommand command = batch.Count == 1
                ? batch[0].Command
                : new BatchCommand { Commands = batch.Select(p => p.Command).ToList() };
#pragma warning restore IDE0007

            // Serialize into reusable buffer to avoid LOH byte[] allocations.
            // ArrayBufferWriter grows as needed but reuses its internal array across calls.
            _serializeBuffer.Clear();
            MessagePackSerializer.Serialize<ICommand>(_serializeBuffer, command, ConfmanSerializerOptions.Instance, ct);

            _logger.LogDebug(
                "Replicating batch: {Count} commands, {Size} bytes",
                batch.Count, _serializeBuffer.WrittenCount);

            var entry = new BinaryLogEntry(_serializeBuffer.WrittenMemory, _cluster.Term);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var result = await _cluster.ReplicateAsync(entry, timeoutCts.Token);

            _logger.LogDebug(
                "Batch replicated: {Count} commands, result: {Result} ({ElapsedMs} ms)",
                batch.Count, result, sw.ElapsedMilliseconds);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Batch replication cancelled (shutdown)");
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Batch replication timed out after 10 seconds ({Count} commands)", batch.Count);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replicating batch ({Count} commands)", batch.Count);
            return false;
        }
    }

    private void DrainAndFail()
    {
        while (_queue.Reader.TryRead(out var pending))
            pending.Completion.TrySetResult(false);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _shutdownCts.Cancel();

        try
        {
            await _flushLoop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Flush loop did not stop within 5 seconds");
        }

        _shutdownCts.Dispose();
    }

    private sealed record PendingCommand(ICommand Command, TaskCompletionSource<bool> Completion);

    /// <summary>
    /// Binary log entry for command replication.
    /// </summary>
    private sealed class BinaryLogEntry : IRaftLogEntry
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly long _term;

        public BinaryLogEntry(ReadOnlyMemory<byte> data, long term)
        {
            _data = data;
            _term = term;
            Timestamp = DateTimeOffset.UtcNow;
        }

        public long Term => _term;
        public DateTimeOffset Timestamp { get; }
        public bool IsSnapshot => false;
        public int? CommandId => null;

        public long? Length => _data.Length;

        public bool IsReusable => true;

        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken ct)
        {
            await writer.WriteAsync(_data, null, ct).ConfigureAwait(false);
        }
    }
}