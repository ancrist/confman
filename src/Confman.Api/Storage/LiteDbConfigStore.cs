using System.Diagnostics;
using Confman.Api.Cluster;
using Confman.Api.Models;
using LiteDB;

namespace Confman.Api.Storage;

/// <summary>
/// LiteDB implementation of the configuration store.
/// Thread-safe and supports concurrent access.
/// </summary>
public sealed class LiteDbConfigStore : IConfigStore, IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ConfigEntry> _configs;
    private readonly ILiteCollection<Namespace> _namespaces;
    private readonly ILiteCollection<AuditEvent> _audit;
    private readonly ILogger<LiteDbConfigStore> _logger;
    private readonly bool _logApplies;
    private readonly bool _auditEnabled;
    private readonly SemaphoreSlim _dbSemaphore;

    public LiteDbConfigStore(IConfiguration config, ILogger<LiteDbConfigStore> logger)
    {
        _logger = logger;
        _logApplies = config.GetValue<bool>("Storage:LogApplies", false);
        _auditEnabled = config.GetValue<bool>("Audit:Enabled", true);

        // Cap concurrent LiteDB access to prevent "Maximum number of transactions reached" errors.
        // LiteDB direct mode has a hard transaction limit (~100). Under concurrent load (e.g., 10+ Locust users),
        // requests can exceed this. The semaphore queues excess requests instead of failing them.
        var maxConcurrency = config.GetValue<int>("Storage:MaxConcurrency", 10);
        _dbSemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _logger.LogInformation("LiteDB max concurrency: {MaxConcurrency}", maxConcurrency);

        if (!_auditEnabled)
            _logger.LogWarning("Audit logging is DISABLED - no audit events will be persisted");

        var dataPath = config["Storage:DataPath"] ?? "./data";
        Directory.CreateDirectory(dataPath);

        var dbPath = Path.Combine(dataPath, "confman.db");
        _logger.LogInformation("Opening LiteDB at: {Path}", dbPath);

        // Register AuditAction as a flat string in BSON (backward compatible)
        BsonMapper.Global.RegisterType<AuditAction>(
            serialize: action => action.Value,
            deserialize: bson => AuditAction.Parse(bson.AsString));

        // Direct mode: in-process locking only (no cross-process mutex).
        // Safe because each Confman node is a single process with its own database file.
        // WARNING: Do not open confman.db with external tools while Confman is running.
        // For backups, stop the node or use the snapshot API.
        _db = new LiteDatabase($"Filename={dbPath};Connection=direct");

        _configs = _db.GetCollection<ConfigEntry>("configs");
        _namespaces = _db.GetCollection<Namespace>("namespaces");
        _audit = _db.GetCollection<AuditEvent>("audit");

        // Create indexes for efficient queries
        _configs.EnsureIndex(x => x.Namespace);
        _configs.EnsureIndex(x => x.Key);
        _configs.EnsureIndex(x => new { x.Namespace, x.Key }, unique: true);

        _namespaces.EnsureIndex(x => x.Path, unique: true);

        _audit.EnsureIndex(x => x.Namespace);
        _audit.EnsureIndex(x => x.Timestamp);
    }

    #region Config Operations

    public async Task<ConfigEntry?> GetAsync(string ns, string key, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            var entry = _configs.FindOne(x => x.Namespace == ns && x.Key == key);
            return entry;
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task<IReadOnlyList<ConfigEntry>> ListAsync(string ns, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            return _configs.Find(x => x.Namespace == ns).ToList();
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task<IReadOnlyList<ConfigEntry>> ListAllAsync(CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            return _configs.FindAll().ToList();
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task SetAsync(ConfigEntry entry, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var existing = _configs.FindOne(x => x.Namespace == entry.Namespace && x.Key == entry.Key);
            var findMs = sw.ElapsedMilliseconds;

            _db.BeginTrans();
            try
            {
                if (existing is not null)
                {
                    entry.Id = existing.Id;
                    entry.Version = existing.Version + 1;
                    _configs.Update(entry);
                }
                else
                {
                    entry.Version = 1;
                    _configs.Insert(entry);
                }

                _db.Commit();

                if (_logApplies)
                    _logger.LogInformation("Set config {Ns}/{Key} v{Version} (find: {FindMs} ms, total: {ElapsedMs} ms)",
                        entry.Namespace, entry.Key, entry.Version, findMs, sw.ElapsedMilliseconds);
                else
                    _logger.LogDebug("Set config {Ns}/{Key} v{Version} (find: {FindMs} ms, total: {ElapsedMs} ms)",
                        entry.Namespace, entry.Key, entry.Version, findMs, sw.ElapsedMilliseconds);
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task DeleteAsync(string ns, string key, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var deleted = _configs.DeleteMany(x => x.Namespace == ns && x.Key == key);
            if (_logApplies)
                _logger.LogInformation("Deleted {Count} configs for {Ns}/{Key} ({ElapsedMs} ms)", deleted, ns, key, sw.ElapsedMilliseconds);
            else
                _logger.LogDebug("Deleted {Count} configs for {Ns}/{Key} ({ElapsedMs} ms)", deleted, ns, key, sw.ElapsedMilliseconds);
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    #endregion

    #region Namespace Operations

    public async Task<Namespace?> GetNamespaceAsync(string path, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            return _namespaces.FindOne(x => x.Path == path);
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task<IReadOnlyList<Namespace>> ListNamespacesAsync(CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            return _namespaces.FindAll().ToList();
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task SetNamespaceAsync(Namespace ns, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var existing = _namespaces.FindOne(x => x.Path == ns.Path);

            if (existing is not null)
            {
                ns.Id = existing.Id;
                ns.CreatedAt = existing.CreatedAt;
                _namespaces.Update(ns);
                if (_logApplies)
                    _logger.LogInformation("Updated namespace {Path} ({ElapsedMs} ms)", ns.Path, sw.ElapsedMilliseconds);
                else
                    _logger.LogDebug("Updated namespace {Path} ({ElapsedMs} ms)", ns.Path, sw.ElapsedMilliseconds);
            }
            else
            {
                _namespaces.Insert(ns);
                if (_logApplies)
                    _logger.LogInformation("Created namespace {Path} ({ElapsedMs} ms)", ns.Path, sw.ElapsedMilliseconds);
                else
                    _logger.LogDebug("Created namespace {Path} ({ElapsedMs} ms)", ns.Path, sw.ElapsedMilliseconds);
            }
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task DeleteNamespaceAsync(string path, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            // Cascade delete: remove all configs in this namespace first
            var deletedConfigs = _configs.DeleteMany(x => x.Namespace == path);
            if (deletedConfigs > 0)
            {
                if (_logApplies)
                    _logger.LogInformation("Cascade deleted {Count} configs in namespace {Path}", deletedConfigs, path);
                else
                    _logger.LogDebug("Cascade deleted {Count} configs in namespace {Path}", deletedConfigs, path);
            }

            var deleted = _namespaces.DeleteMany(x => x.Path == path);
            if (_logApplies)
                _logger.LogInformation("Deleted {Count} namespaces for {Path}", deleted, path);
            else
                _logger.LogDebug("Deleted {Count} namespaces for {Path}", deleted, path);
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    #endregion

    #region Audit Operations

    public async Task AppendAuditAsync(AuditEvent evt, CancellationToken ct = default)
    {
        if (!_auditEnabled)
            return;

        await _dbSemaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            // Use upsert for idempotency during log replay on all nodes
            _audit.Upsert(evt);
            if (_logApplies)
                _logger.LogInformation("Upserted audit event: {Action} on {Ns}/{Key} ({ElapsedMs} ms)",
                    evt.Action, evt.Namespace, evt.Key, sw.ElapsedMilliseconds);
            else
                _logger.LogDebug("Upserted audit event: {Action} on {Ns}/{Key} ({ElapsedMs} ms)",
                    evt.Action, evt.Namespace, evt.Key, sw.ElapsedMilliseconds);
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(string ns, int limit = 50, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            return _audit
                .Find(x => x.Namespace == ns)
                .OrderByDescending(x => x.Timestamp)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    #endregion

    #region Batched Operations

    public async Task SetWithAuditAsync(ConfigEntry entry, AuditEvent audit, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var existing = _configs.FindOne(x => x.Namespace == entry.Namespace && x.Key == entry.Key);
            var findMs = sw.ElapsedMilliseconds;

            _db.BeginTrans();
            try
            {
                if (existing is not null)
                {
                    entry.Id = existing.Id;
                    entry.Version = existing.Version + 1;
                    _configs.Update(entry);
                }
                else
                {
                    entry.Version = 1;
                    _configs.Insert(entry);
                }

                if (_auditEnabled)
                    _audit.Upsert(audit);

                _db.Commit();

                if (_logApplies)
                {
                    _logger.LogInformation("Set config {Ns}/{Key} v{Version}{AuditStatus} (find: {FindMs} ms, total: {ElapsedMs} ms)",
                        entry.Namespace, entry.Key, entry.Version, _auditEnabled ? " + audit" : "", findMs, sw.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogDebug("Set config {Ns}/{Key} v{Version}{AuditStatus} (find: {FindMs} ms, total: {ElapsedMs} ms)",
                        entry.Namespace, entry.Key, entry.Version, _auditEnabled ? " + audit" : "", findMs, sw.ElapsedMilliseconds);
                }
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    #endregion

    #region Bulk Operations for Snapshots

    public async Task<List<ConfigEntry>> GetAllConfigsAsync(CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            return _configs.FindAll().ToList();
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task<List<AuditEvent>> GetAllAuditEventsAsync(CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            return _audit.FindAll().ToList();
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    public async Task RestoreFromSnapshotAsync(SnapshotData snapshot, CancellationToken ct = default)
    {
        await _dbSemaphore.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Restoring from snapshot: {ConfigCount} configs, {NamespaceCount} namespaces, {AuditCount} audit events",
                snapshot.Configs.Count, snapshot.Namespaces.Count, snapshot.AuditEvents.Count);

            // Use transaction to ensure atomicity - if crash occurs, data remains intact
            _db.BeginTrans();
            try
            {
                // Clear existing data
                _configs.DeleteAll();
                _namespaces.DeleteAll();
                _audit.DeleteAll();

                // Restore from snapshot
                if (snapshot.Configs.Count > 0)
                    _configs.InsertBulk(snapshot.Configs);

                if (snapshot.Namespaces.Count > 0)
                    _namespaces.InsertBulk(snapshot.Namespaces);

                if (snapshot.AuditEvents.Count > 0)
                    _audit.InsertBulk(snapshot.AuditEvents);

                _db.Commit();
                _logger.LogInformation("Snapshot restore complete");
            }
            catch (Exception ex)
            {
                _db.Rollback();
                _logger.LogError(ex, "Snapshot restore failed, rolled back transaction");
                throw;
            }
        }
        finally
        {
            _dbSemaphore.Release();
        }
    }

    #endregion

    public void Dispose()
    {
        _dbSemaphore.Dispose();
        _db.Dispose();
        _logger.LogInformation("LiteDB connection closed");
    }
}