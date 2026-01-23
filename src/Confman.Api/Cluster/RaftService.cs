using System.Text.Json;
using Confman.Api.Cluster.Commands;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;

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

            // The leader endpoint is an IPEndPoint, we need to construct a URI
            return leader.EndPoint switch
            {
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

        try
        {
            // Serialize the command to bytes
            var json = JsonSerializer.Serialize(command);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);

            _logger.LogDebug("Replicating command: {CommandType}, size: {Size} bytes",
                command.GetType().Name, bytes.Length);

            // Create a log entry and replicate it
            var entry = new BinaryLogEntry(bytes);

            var result = await _cluster.ReplicateAsync(entry, ct);

            if (result)
            {
                _logger.LogDebug("Command replicated successfully: {CommandType}", command.GetType().Name);
            }
            else
            {
                _logger.LogWarning("Command replication failed: {CommandType}", command.GetType().Name);
            }

            return result;
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

        public BinaryLogEntry(byte[] data)
        {
            _data = data;
            Timestamp = DateTimeOffset.UtcNow;
        }

        public long Term => 0; // Will be set by the cluster
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
