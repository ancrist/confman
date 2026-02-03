using System.Diagnostics;
using System.Text.Json;
using Confman.Api.Cluster.Commands;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Connections;

namespace Confman.Api.Cluster;

/// <summary>
/// Service that handles Raft consensus operations for command replication.
/// </summary>
public interface IRaftService
{
    /// <summary>
    /// Gets whether this node is the cluster leader.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// Gets the URI of the current cluster leader, if known.
    /// </summary>
    Uri? LeaderUri { get; }

    /// <summary>
    /// Replicates a command through the Raft cluster.
    /// Must be called on the leader node.
    /// </summary>
    Task<bool> ReplicateAsync(ICommand command, CancellationToken ct = default);
}

/// <summary>
/// Implementation of IRaftService using DotNext.
/// </summary>
public class RaftService : IRaftService
{
    private readonly IRaftCluster _cluster;
    private readonly ILogger<RaftService> _logger;

    public RaftService(IRaftCluster cluster, ILogger<RaftService> logger)
    {
        _cluster = cluster;
        _logger = logger;
    }

    public bool IsLeader => _cluster.LeadershipToken.IsCancellationRequested == false &&
                            _cluster.Leader?.EndPoint?.Equals(_cluster.Members
                                .FirstOrDefault(m => m.IsRemote == false)?.EndPoint) == true;

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

        var sw = Stopwatch.StartNew();
        try
        {
            // Serialize the command to bytes
            var json = JsonSerializer.Serialize(command);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);

            _logger.LogDebug("Replicating command: {CommandType}, size: {Size} bytes",
                command.GetType().Name, bytes.Length);

            // Create a log entry with the current term
            var entry = new BinaryLogEntry(bytes, _cluster.Term);

            // Add timeout to avoid hanging forever
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var result = await _cluster.ReplicateAsync(entry, timeoutCts.Token);

            _logger.LogDebug("Command replicated: {CommandType}, result: {Result} ({ElapsedMs} ms)",
                command.GetType().Name, result, sw.ElapsedMilliseconds);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // HTTP request was cancelled (client disconnect, benchmark teardown, etc.)
            _logger.LogDebug("Replication cancelled by client for {CommandType}", command.GetType().Name);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal timeout (10 seconds)
            _logger.LogWarning("Replication timed out after 10 seconds for {CommandType}", command.GetType().Name);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replicating command: {CommandType}", command.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Binary log entry for command replication.
    /// </summary>
    private sealed class BinaryLogEntry : IRaftLogEntry
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly long _term;

        public BinaryLogEntry(byte[] data, long term)
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