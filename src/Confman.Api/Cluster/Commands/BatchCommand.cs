using Confman.Api.Storage;
using MessagePack;

namespace Confman.Api.Cluster.Commands;

/// <summary>
/// Command that wraps multiple commands into a single Raft log entry.
/// One consensus round-trip commits all inner commands, amortizing quorum cost.
/// Inner commands are applied sequentially to preserve ordering (last writer wins).
/// </summary>
[MessagePackObject]
public sealed record BatchCommand : ICommand
{
    [Key(0)] public required List<ICommand> Commands { get; init; }

    /// <summary>
    /// Estimated serialized size in bytes, used for batch payload limits.
    /// </summary>
    [IgnoreMember] public int EstimatedBytes => Commands.Sum(c => c.EstimatedBytes);

    public async Task ApplyAsync(IConfigStore store, bool auditEnabled = true, CancellationToken ct = default)
    {
        for (var i = 0; i < Commands.Count; i++)
        {
            try
            {
                await Commands[i].ApplyAsync(store, auditEnabled, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation is legitimate — rethrow to stop applying
                throw;
            }
            catch (Exception)
            {
                // Log but don't throw — the entry is committed in the Raft log,
                // all nodes must apply it. A single bad command must not poison the batch.
                // Consistent across nodes: same bytes → same failure on every node.
                // Callers cannot act on per-command failures after Raft commit anyway.
            }
        }
    }
}
