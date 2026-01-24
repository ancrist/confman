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
        var existing = _configs.FindOne(x => x.Namespace == entry.Namespace && x.Key == entry.Key);

        if (existing is not null)
        {
            entry.Id = existing.Id;
            entry.Version = existing.Version + 1;
            _configs.Update(entry);
            _logger.LogDebug("Updated config {Ns}/{Key} to version {Version}",
                entry.Namespace, entry.Key, entry.Version);
        }
        else
        {
            entry.Version = 1;
            _configs.Insert(entry);
            _logger.LogDebug("Created config {Ns}/{Key} version {Version}",
                entry.Namespace, entry.Key, entry.Version);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string ns, string key, CancellationToken ct = default)
    {
        var deleted = _configs.DeleteMany(x => x.Namespace == ns && x.Key == key);
        _logger.LogDebug("Deleted {Count} configs for {Ns}/{Key}", deleted, ns, key);
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
        var existing = _namespaces.FindOne(x => x.Path == ns.Path);

        if (existing is not null)
        {
            ns.Id = existing.Id;
            ns.CreatedAt = existing.CreatedAt;
            _namespaces.Update(ns);
            _logger.LogDebug("Updated namespace {Path}", ns.Path);
        }
        else
        {
            _namespaces.Insert(ns);
            _logger.LogDebug("Created namespace {Path}", ns.Path);
        }

        return Task.CompletedTask;
    }

    public Task DeleteNamespaceAsync(string path, CancellationToken ct = default)
    {
        var deleted = _namespaces.DeleteMany(x => x.Path == path);
        _logger.LogDebug("Deleted {Count} namespaces for {Path}", deleted, path);
        return Task.CompletedTask;
    }

    #endregion

    #region Audit Operations

    public Task AppendAuditAsync(AuditEvent evt, CancellationToken ct = default)
    {
        _audit.Insert(evt);
        _logger.LogDebug("Appended audit event: {Action} on {Ns}/{Key}",
            evt.Action, evt.Namespace, evt.Key);
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

    public void Dispose()
    {
        _db.Dispose();
        _logger.LogInformation("LiteDB connection closed");
    }
}
