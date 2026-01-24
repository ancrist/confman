using System.Text.Json.Serialization;
using Confman.Api.Storage;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Base interface for all commands that can be replicated through Raft.
/// Commands are applied to the IConfigStore after being committed.
/// </summary>
[JsonDerivedType(typeof(SetConfigCommand), "set_config")]
[JsonDerivedType(typeof(DeleteConfigCommand), "delete_config")]
[JsonDerivedType(typeof(SetNamespaceCommand), "set_namespace")]
[JsonDerivedType(typeof(DeleteNamespaceCommand), "delete_namespace")]
public interface ICommand
{
    /// <summary>
    /// Applies the command to the store. Called after the command is committed to the Raft log.
    /// </summary>
    /// <param name="store">The config store to apply changes to.</param>
    /// <param name="isLeader">True if this node is the current leader. Use for leader-only side effects like audit logging.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyAsync(IConfigStore store, bool isLeader, CancellationToken ct = default);
}
