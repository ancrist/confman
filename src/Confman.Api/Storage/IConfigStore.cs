using Confman.Api.Cluster;
using Confman.Api.Models;

namespace Confman.Api.Storage;

/// <summary>
/// Interface for configuration storage operations.
/// Decoupled from the Raft state machine to allow independent testing.
/// </summary>
public interface IConfigStore
{
    // Config operations
    Task<ConfigEntry?> GetAsync(string ns, string key, CancellationToken ct = default);
    Task<IReadOnlyList<ConfigEntry>> ListAsync(string ns, CancellationToken ct = default);
    Task<IReadOnlyList<ConfigEntry>> ListAllAsync(CancellationToken ct = default);
    Task SetAsync(ConfigEntry entry, CancellationToken ct = default);
    Task DeleteAsync(string ns, string key, CancellationToken ct = default);

    // Namespace operations
    Task<Namespace?> GetNamespaceAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<Namespace>> ListNamespacesAsync(CancellationToken ct = default);
    Task SetNamespaceAsync(Namespace ns, CancellationToken ct = default);
    Task DeleteNamespaceAsync(string path, CancellationToken ct = default);

    // Audit operations (uses upsert for idempotency during log replay)
    Task AppendAuditAsync(AuditEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(string ns, int limit = 50, CancellationToken ct = default);

    // Bulk operations for snapshots
    Task<List<ConfigEntry>> GetAllConfigsAsync(CancellationToken ct = default);
    Task<List<AuditEvent>> GetAllAuditEventsAsync(CancellationToken ct = default);
    Task RestoreFromSnapshotAsync(SnapshotData snapshot, CancellationToken ct = default);
}