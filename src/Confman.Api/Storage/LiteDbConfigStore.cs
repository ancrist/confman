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

    public LiteDbConfigStore(IConfiguration config, ILogger<LiteDbConfigStore> logger)
    {
        _logger = logger;

        var dataPath = config["Storage:DataPath"] ?? "./data";
        Directory.CreateDirectory(dataPath);

        var dbPath = Path.Combine(dataPath, "confman.db");
        _logger.LogInformation("Opening LiteDB at: {Path}", dbPath);

        // Register AuditAction as a flat string in BSON (backward compatible)
        BsonMapper.Global.RegisterType<AuditAction>(
            serialize: action => action.Value,
            deserialize: bson => AuditAction.Parse(bson.AsString));

        // LiteDB connection string with thread-safe mode
        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");

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

    public Task<ConfigEntry?> GetAsync(string ns, string key, CancellationToken ct = default)
    {
        var entry = _configs.FindOne(x => x.Namespace == ns && x.Key == key);
        return Task.FromResult<ConfigEntry?>(entry);
    }

    public Task<IReadOnlyList<ConfigEntry>> ListAsync(string ns, CancellationToken ct = default)
    {
        var entries = _configs.Find(x => x.Namespace == ns).ToList();
        return Task.FromResult<IReadOnlyList<ConfigEntry>>(entries);
    }

    public Task<IReadOnlyList<ConfigEntry>> ListAllAsync(CancellationToken ct = default)
    {
        var entries = _configs.FindAll().ToList();
        return Task.FromResult<IReadOnlyList<ConfigEntry>>(entries);
    }

    public Task SetAsync(ConfigEntry entry, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var existing = _configs.FindOne(x => x.Namespace == entry.Namespace && x.Key == entry.Key);

        if (existing is not null)
        {
            entry.Id = existing.Id;
            entry.Version = existing.Version + 1;
            _configs.Update(entry);
            _logger.LogDebug("Updated config {Ns}/{Key} to version {Version} ({ElapsedMs} ms)",
                entry.Namespace, entry.Key, entry.Version, sw.ElapsedMilliseconds);
        }
        else
        {
            entry.Version = 1;
            _configs.Insert(entry);
            _logger.LogDebug("Created config {Ns}/{Key} version {Version} ({ElapsedMs} ms)",
                entry.Namespace, entry.Key, entry.Version, sw.ElapsedMilliseconds);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string ns, string key, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var deleted = _configs.DeleteMany(x => x.Namespace == ns && x.Key == key);
        _logger.LogDebug("Deleted {Count} configs for {Ns}/{Key} ({ElapsedMs} ms)", deleted, ns, key, sw.ElapsedMilliseconds);
        return Task.CompletedTask;
    }

    #endregion

    #region Namespace Operations

    public Task<Namespace?> GetNamespaceAsync(string path, CancellationToken ct = default)
    {
        var ns = _namespaces.FindOne(x => x.Path == path);
        return Task.FromResult<Namespace?>(ns);
    }

    public Task<IReadOnlyList<Namespace>> ListNamespacesAsync(CancellationToken ct = default)
    {
        var namespaces = _namespaces.FindAll().ToList();
        return Task.FromResult<IReadOnlyList<Namespace>>(namespaces);
    }

    public Task SetNamespaceAsync(Namespace ns, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var existing = _namespaces.FindOne(x => x.Path == ns.Path);

        if (existing is not null)
        {
            ns.Id = existing.Id;
            ns.CreatedAt = existing.CreatedAt;
            _namespaces.Update(ns);
            _logger.LogDebug("Updated namespace {Path} ({ElapsedMs} ms)", ns.Path, sw.ElapsedMilliseconds);
        }
        else
        {
            _namespaces.Insert(ns);
            _logger.LogDebug("Created namespace {Path} ({ElapsedMs} ms)", ns.Path, sw.ElapsedMilliseconds);
        }

        return Task.CompletedTask;
    }

    public Task DeleteNamespaceAsync(string path, CancellationToken ct = default)
    {
        // Cascade delete: remove all configs in this namespace first
        var deletedConfigs = _configs.DeleteMany(x => x.Namespace == path);
        if (deletedConfigs > 0)
        {
            _logger.LogDebug("Cascade deleted {Count} configs in namespace {Path}", deletedConfigs, path);
        }

        var deleted = _namespaces.DeleteMany(x => x.Path == path);
        _logger.LogDebug("Deleted {Count} namespaces for {Path}", deleted, path);
        return Task.CompletedTask;
    }

    #endregion

    #region Audit Operations

    public Task AppendAuditAsync(AuditEvent evt, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // Use upsert for idempotency during log replay on all nodes
        _audit.Upsert(evt);
        _logger.LogDebug("Upserted audit event: {Action} on {Ns}/{Key} ({ElapsedMs} ms)",
            evt.Action, evt.Namespace, evt.Key, sw.ElapsedMilliseconds);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(string ns, int limit = 50, CancellationToken ct = default)
    {
        var events = _audit
            .Find(x => x.Namespace == ns)
            .OrderByDescending(x => x.Timestamp)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<AuditEvent>>(events);
    }

    #endregion

    #region Bulk Operations for Snapshots

    public Task<List<ConfigEntry>> GetAllConfigsAsync(CancellationToken ct = default)
    {
        var configs = _configs.FindAll().ToList();
        return Task.FromResult(configs);
    }

    public Task<List<AuditEvent>> GetAllAuditEventsAsync(CancellationToken ct = default)
    {
        var events = _audit.FindAll().ToList();
        return Task.FromResult(events);
    }

    public Task RestoreFromSnapshotAsync(SnapshotData snapshot, CancellationToken ct = default)
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

        return Task.CompletedTask;
    }

    #endregion

    public void Dispose()
    {
        _db.Dispose();
        _logger.LogInformation("LiteDB connection closed");
    }
}