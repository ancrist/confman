namespace Confman.Api.Storage.Blobs;

/// <summary>
/// Pushes compressed blobs to cluster peers and waits for durability quorum.
/// </summary>
public interface IBlobReplicator
{
    /// <summary>
    /// Pushes compressed blob to cluster peers and waits for durability quorum.
    /// Returns when (clusterSize / 2 + 1) - 1 peers have confirmed durable storage.
    /// Throws BlobReplicationException if quorum cannot be achieved within timeout.
    /// Remaining pushes continue in background after quorum.
    /// </summary>
    Task ReplicateAsync(string blobId, CancellationToken ct = default);
}

/// <summary>
/// Thrown when blob replication fails to achieve durability quorum.
/// </summary>
public class BlobReplicationException : Exception
{
    public BlobReplicationException(string message) : base(message) { }
    public BlobReplicationException(string message, Exception inner) : base(message, inner) { }
}
