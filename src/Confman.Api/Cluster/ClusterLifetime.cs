using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;

namespace Confman.Api.Cluster;

/// <summary>
/// Handles cluster lifecycle events and leader change notifications.
/// </summary>
public sealed class ClusterLifetime : IClusterMemberLifetime
{
    private readonly ILogger<ClusterLifetime> _logger;

    public ClusterLifetime(ILogger<ClusterLifetime> logger)
    {
        _logger = logger;
    }

    public void OnStart(IRaftCluster cluster, IDictionary<string, string> metadata)
    {
        cluster.LeaderChanged += OnLeaderChanged;
        _logger.LogInformation("Cluster member started");
    }

    public void OnStop(IRaftCluster cluster)
    {
        cluster.LeaderChanged -= OnLeaderChanged;
        _logger.LogInformation("Cluster member stopped");
    }

    private void OnLeaderChanged(ICluster sender, IClusterMember? leader)
    {
        if (leader is not null)
        {
            _logger.LogInformation("Leader changed to: {Leader}", leader.EndPoint);
        }
        else
        {
            _logger.LogWarning("No leader in cluster");
        }
    }
}
