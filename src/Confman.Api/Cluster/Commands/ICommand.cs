using System.Text.Json.Serialization;
using Confman.Api.Storage;
using MessagePack;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Base interface for all commands that can be replicated through Raft.
/// Commands are applied to the IConfigStore after being committed.
/// Audit events are created on ALL nodes during apply - storage handles idempotency.
/// </summary>
[JsonDerivedType(typeof(SetConfigCommand), "set_config")]
[JsonDerivedType(typeof(DeleteConfigCommand), "delete_config")]
[JsonDerivedType(typeof(SetNamespaceCommand), "set_namespace")]
[JsonDerivedType(typeof(DeleteNamespaceCommand), "delete_namespace")]
[JsonDerivedType(typeof(BatchCommand), "batch")]
[JsonDerivedType(typeof(SetConfigBlobRefCommand), "set_config_blob_ref")]
[Union(0, typeof(SetConfigCommand))]
[Union(1, typeof(DeleteConfigCommand))]
[Union(2, typeof(SetNamespaceCommand))]
[Union(3, typeof(DeleteNamespaceCommand))]
[Union(4, typeof(BatchCommand))]
[Union(5, typeof(SetConfigBlobRefCommand))]
public interface ICommand
{
    /// <summary>
    /// Applies the command to the store. Called after the command is committed to the Raft log.
    /// Creates audit events on all nodes - the store handles idempotency via upsert.
    /// </summary>
    /// <param name="store">The config store to apply changes to.</param>
    /// <param name="auditEnabled">Whether to create audit events.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default);

    /// <summary>
    /// Estimated serialized size in bytes. Used by BatchingRaftService to enforce payload limits.
    /// </summary>
    int EstimatedBytes => 64;
}