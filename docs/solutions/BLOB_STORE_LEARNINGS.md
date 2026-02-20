---
title: "Institutional Learnings for Blob Store Feature"
date: 2026-02-12
category: planning
tags: [blob-store, raft, snapshot, serialization, replication, migration]
module: Confman.Api
---

# Institutional Learnings for Blob Store Feature

## Search Summary

**Scope**: Content-addressed blob storage with leader→follower replication, Raft pointer commands, Snapshot v3 (metadata-only), lazy blob fetch, cluster-token auth, hard cutover migration

**Files Scanned**: 6 solution documents in `/docs/solutions/`

**Match Rate**: 5/5 highly relevant (100%)

**Total Insights**: 12 documented learnings mapped to 6 implementation phases

---

## Critical Blocking Decisions

### 1. PHASE 1: Bootstrap Strategy (Must decide before implementation)

**Source**: `docs/solutions/2026-02-01-dotnext-coldstart-leader-tracking-loss.md`

**The Problem**:
- DotNext has two mutually exclusive bootstrapping strategies: **cold start (dynamic)** vs **pre-configured members (static)**
- Mixing them causes **permanent leader tracking loss** after re-election
- Affected node replicates data but reports `leaderKnown: false`, returns 503 on all reads

**The Gotcha**:
When a node with pre-configured members is started with `coldStart: true`, DotNext generates different configuration fingerprints. After re-election, the first heartbeat from the new leader has a mismatched fingerprint → rejected. Subsequent heartbeats sync the fingerprint, but `TryGetMember` keeps returning null. The node stays follower but never identifies the leader. Permanent state.

**Decision Required**:
After hard wipe in Phase 6, the cluster will restart. Choose:
- **Option A (Recommended)**: Static membership via `appsettings.json` (already configured)
  - All nodes start with `coldStart: false` (default)
  - Cluster discovers members via `UseInMemoryConfigurationStorage()`
  - Leader election works immediately when quorum available
- **Option B**: Dynamic membership via cold start
  - One node starts with `coldStart: true`, self-elects
  - Other nodes join via `AddMemberAsync()` (requires orchestration)
  - Slower bootstrap, needs additional bootstrap API

**Recommendation**: Choose Option A (static). Document in Phase 6 migration runbook.

**Action for Plan**:
- Phase 1 design document must state: "After hard cutover, cluster uses static membership (Option A). Never use `coldStart: true` when members are pre-configured."
- Phase 6 runbook must include: "Start all 3 nodes with default `coldStart: false`. Do NOT override with `--coldStart true`."

---

## Phase-by-Phase Implementation Learnings

### Phase 2: Blob Pointer Command & Replication

#### Learning 2.1: WAL Null Bytes in Custom Commands

**Source**: `docs/solutions/dotnext-wal-null-bytes-log-entry-deserialization.md`

**The Problem**:
DotNext's `SimpleStateMachine` WAL occasionally prepends null bytes (`0x00`) to log entry payloads under concurrent write conditions. Manifests as ~1% deserialization failures in high-throughput scenarios.

**Why It Happens**:
- Not caused by application serialization (write-time bytes are clean: `0x7B` = `{`)
- Null bytes added by DotNext's WAL storage layer (memory alignment, buffer management, or framing metadata)
- Triggered by concurrent writes, not just high request rates

**The Gotcha**:
You might observe:
```
[WRN] Skipped 112 null bytes before JSON in log entry (DotNext WAL padding)
```
This is normal and handled. But if you don't implement the workaround, you'll see:
```
[ERR] JsonException: The JSON value could not be converted
[ERR] Error applying log entry to state machine
```

**Required Implementation**:
Add defensive null-byte skipping to `BlobPointerCommand` deserialization:

```csharp
private ICommand? DeserializeCommand(in ReadOnlySequence<byte> payload)
{
    if (payload.IsEmpty)
        return null;

    var bytes = payload.ToArray();

    // Find the start of JSON - skip any leading null bytes
    // DotNext's SimpleStateMachine WAL sometimes prepends null bytes to payloads
    var jsonStart = 0;
    while (jsonStart < bytes.Length && bytes[jsonStart] == 0)
    {
        jsonStart++;
    }

    // If all null bytes or empty, skip
    if (jsonStart >= bytes.Length)
        return null;

    // Check if the remaining content starts with '{' (JSON object)
    if (bytes[jsonStart] != (byte)'{')
    {
        // Genuine Raft internal entry (config change, etc.)
        _logger.LogDebug("Skipping non-JSON entry, firstByte=0x{FirstByte:X2} at offset {Offset}",
            bytes[jsonStart], jsonStart);
        return null;
    }

    // If we had to skip null bytes, log it for observability
    if (jsonStart > 0)
    {
        _logger.LogWarning("Skipped {NullBytes} null bytes before JSON in log entry (DotNext WAL padding)",
            jsonStart);
    }

    try
    {
        var jsonSpan = bytes.AsSpan(jsonStart);
        return JsonSerializer.Deserialize<ICommand>(jsonSpan);
    }
    catch (JsonException ex)
    {
        var jsonString = System.Text.Encoding.UTF8.GetString(bytes, jsonStart, bytes.Length - jsonStart);
        _logger.LogWarning(ex, "JSON deserialization failed. Payload ({Length} bytes): {Payload}",
            bytes.Length - jsonStart, jsonString.Length > 500 ? jsonString[..500] + "..." : jsonString);
        return null;
    }
}
```

**Unit Tests Required**:
```csharp
[Theory]
[InlineData(1)]
[InlineData(8)]
[InlineData(64)]
[InlineData(256)]
public void DeserializeCommand_VariableNullByteCounts_AllSucceed(int nullByteCount)
{
    var command = new BlobPointerCommand { Namespace = "test", Key = "k", BlobHash = "abc123" };
    var json = JsonSerializer.SerializeToUtf8Bytes<ICommand>(command);
    var paddedPayload = new byte[nullByteCount + json.Length];
    json.CopyTo(paddedPayload.AsMemory(nullByteCount));

    var result = DeserializeCommand(new ReadOnlySequence<byte>(paddedPayload));

    Assert.NotNull(result);
    Assert.IsType<BlobPointerCommand>(result);
}
```

**Action for Plan**: Implement null-byte skipping in Phase 2. Add unit tests. Document in code comment: "DotNext WAL known behavior under concurrent writes."

---

#### Learning 2.2: Kestrel Body Size Limit for Raft RPCs

**Source**: `docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md` (Insight #5)

**The Problem**:
Kestrel's default `MaxRequestBodySize = 30MB` applies to **Raft inter-node RPCs**. AppendEntries RPCs bundle multiple log entries into one HTTP request. With many `BlobPointerCommand` entries or large payloads, the request easily exceeds 30MB.

**The Gotcha**:
If you forget to set `MaxRequestBodySize = null`, replication fails silently with:
```
[ERR] BadHttpRequestException: Request body too large
```

**Current Status**:
This is already fixed in the codebase (set to `null` in Program.cs). But document why so future maintainers don't tighten it thinking it's a security issue.

**Action for Plan**:
Verify Phase 2 Program.cs has:
```csharp
// In ConfigureKestrel
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = null); // Required for Raft AppendEntries bundling large payloads
```

If adding separate HTTP endpoints for blob replication (not Raft), use explicit limits (e.g., 512MB for blob chunks).

---

### Phase 3: Snapshot Version 3 (Metadata-Only) Design

#### Learning 3.1: Snapshot OOM and Streaming Serialization

**Source**: `docs/solutions/2026-02-08-large-payload-performance-insights.md` (Insights #1, #2)

**The Problem**:
- `JsonSerializer.SerializeToUtf8Bytes()` allocates the **entire JSON as a single contiguous byte[]**
- With 500 × 1MB configs, JSON exceeds 1GB — impossible to allocate
- `File.ReadAllBytesAsync()` on restore has the same problem
- Peak memory during snapshot can be 1.5GB+

**Why It Matters for Blob Store**:
Snapshot v3 will contain audit history, schema metadata, and blob pointers (no full config values). Even metadata-only, a large cluster can accumulate significant history. Streaming is essential.

**The Fix**:
Use streaming JSON serialization and deserialization:

```csharp
// In ConfigStateMachine.PersistAsync
public async ValueTask PersistAsync(long index)
{
    var snapshotPath = Path.Combine(_snapshotDir, $"snapshot-{index}.json");

    var snapshot = new SnapshotData
    {
        Configs = _configStore.GetAllConfigs(),
        Namespaces = _configStore.GetAllNamespaces(),
        AuditEvents = _configStore.GetAllAuditEvents()
    };

    // Stream to file, not to byte[]
    await using var fileStream = File.Create(snapshotPath);
    await JsonSerializer.SerializeAsync(fileStream, snapshot);
}

// In RestoreAsync
public async ValueTask RestoreAsync(long index, IAsyncBinaryReader reader)
{
    var snapshotPath = Path.Combine(_snapshotDir, $"snapshot-{index}.json");

    // Stream from file, not byte[]
    await using var fileStream = File.OpenRead(snapshotPath);
    var snapshot = await JsonSerializer.DeserializeAsync<SnapshotData>(fileStream);

    // Clear and restore
    _configStore.Clear();
    foreach (var config in snapshot.Configs)
        _configStore.Set(config);
    // ... etc
}
```

**Performance Impact**:
- Peak memory drops from ~1.5GB to ~500MB for 1GB snapshot
- Disk I/O unchanged but more CPU-friendly (streaming decompression)

**Action for Plan**: Phase 3 must verify streaming is used. This is already implemented in current code but critical for Snapshot v3.

---

#### Learning 3.2: SnapshotInterval Tuning for WAL Bloat

**Source**: `docs/solutions/2026-02-08-large-payload-performance-insights.md` (Insight #4) + `2026-02-08-raft-snapshot-starvation-spurious-elections.md`

**The Problem**:
- `SnapshotInterval: 1000` means Raft doesn't compact the WAL until 1000 entries accumulated
- With 1MB entries, WAL grows to ~1.3GB across ~167 chunk files (8MB each)
- DotNext operations **degrade progressively**: 22ms → 128ms → 611ms → 2175ms latency
- WAL bloat is invisible during benchmarks but cumulative (hours/days of growth)

**Why It Matters for Blob Store**:
With metadata-only snapshots, snapshot creation becomes much faster and cheaper. Frequent snapshots prevent WAL bloat without performance penalty.

**The Fix**:
Reduce `SnapshotInterval` in Phase 3 migration:

```json
{
  "Raft": {
    "SnapshotInterval": 50
  }
}
```

**Tuning Guidelines**:
| Entry Size | Recommended SnapshotInterval | Reason |
|------------|------------------------------|--------|
| 1-10 KB    | 1000 | Snapshots fast (~50ms), WAL stays <1GB |
| 10-100 KB  | 500 | Moderate payloads, balance snapshot cost vs WAL bloat |
| Metadata-only (blob store) | 50 | Snapshots <100ms, prevents WAL from exceeding 500MB |

**Action for Plan**: Phase 3 applies this tuning. Current code at SnapshotInterval 100 is safe; reduce to 50 for blob store.

---

#### Learning 3.3: Snapshot Starvation and Raft Timing Invariant

**Source**: `docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md` (Insights #1, #6, Invariant #8)

**The Problem**:
- `PersistAsync` runs on the **Raft state machine thread**
- While creating a large snapshot (~700ms), the node cannot:
  - Apply new log entries
  - Respond to AppendEntries RPCs (heartbeats)
  - Participate in elections
- If `snapshot_time > election_timeout`, follower assumes leader is dead and starts election
- Violating the invariant causes cluster flapping: "No leader in cluster" → recovery loop

**The Raft Invariant** (Not Enforced by DotNext):
```
snapshot_time < election_timeout < request_timeout
```

**Why It Matters for Blob Store**:
Snapshot v3 is metadata-only, so snapshot time should drop dramatically (<100ms even for 10K+ configs). This gives plenty of headroom with current timeouts (1000-3000ms election, 10s request).

**Example Failure Mode**:
```
# With old code: SnapshotInterval 1000, election timeout 150-300ms
22:57:46.974  Creating snapshot (499 configs, 523MB)
22:57:47.687  Snapshot created (713ms)  ← exceeds election timeout by 5x
22:57:52.086  Transition to Candidate state has started with term 1
22:58:07.140  No leader in cluster
22:58:07.245  Leader changed to: http://127.0.0.1:6100/  ← recovered, flap

# Logs show heartbeats were missed during snapshot creation
```

**Tuning Guideline**:
Rule of thumb: `snapshot_time ≈ 1ms per config entry`
- 100 entries → ~100ms snapshot
- 1000 entries → ~1000ms snapshot
- Metadata-only → <100ms snapshot (pointers are tiny)

**Current Configuration** (Safe for Blob Store):
```json
{
  "lowerElectionTimeout": 1000,
  "upperElectionTimeout": 3000,
  "requestTimeout": "00:00:10",
  "Raft": {
    "SnapshotInterval": 50  // Or 100, both safe
  }
}
```

Invariant check: 100ms < 1000ms < 10s ✓ (headroom of 10x on election, 100x on request)

**Action for Plan**:
- Phase 3: Verify snapshot creation time with metadata-only format
- Phase 6: Stress test and log actual snapshot times; confirm invariant holds
- Documentation: "Snapshot time observed: X ms for N entries" in Phase 6 runbook

---

### Phase 4: Lazy Blob Fetch Implementation

#### Learning 4.1: Stream Large Objects Prevents OOM and LOH Fragmentation

**Source**: `docs/solutions/2026-02-08-large-payload-performance-insights.md` (Insights #1, #10)

**The Problem**:
- Buffering full blobs into `byte[]` before serialization causes LOH (Large Object Heap) fragmentation
- LOH fragments with thousands of 1MB+ allocations
- Fragmented LOH causes: out-of-memory exceptions, GC pauses, SSD wear from compaction

**Why It Matters for Blob Store**:
Clients request blobs by SHA256 hash. If you buffer the blob into memory for HTTP response, you fragment LOH for every request.

**The Fix**:
Stream blobs directly to HTTP response without intermediate buffering:

```csharp
[HttpGet("/api/internal/blobs/{sha256}")]
public async Task GetBlob(string sha256)
{
    var blob = await _blobStore.GetBlobStreamAsync(sha256);
    if (blob == null)
        return NotFound();

    Response.ContentType = "application/octet-stream";
    Response.ContentLength = blob.Length;

    // Stream directly, no intermediate byte[] allocation
    await blob.CopyToAsync(Response.Body);
}
```

**Performance Impact**:
- Memory stays constant (buffering only ~64KB chunk)
- LOH stays clean (no 1MB+ allocations)
- Client receives data as soon as blob is available

**What NOT to Do**:
```csharp
// WRONG: Buffers entire blob in memory
var blobBytes = await _blobStore.GetBlobBytesAsync(sha256);
return File(blobBytes, "application/octet-stream");

// ALSO WRONG: Two-stage buffering (double memory)
var stream = await _blobStore.GetBlobStreamAsync(sha256);
var buffer = new MemoryStream();
await stream.CopyToAsync(buffer);
return File(buffer.ToArray(), "application/octet-stream");
```

**Action for Plan**: Phase 4 blob read endpoint MUST use streaming. No buffering to byte[].

---

### Phase 5: Internal Blob Endpoints & Cluster Token Auth

#### Learning 5.1: Use IClusterMembership for Auth

**Source**: `docs/solutions/2026-01-24-dotnext-raft-cluster-members-configuration.md`

**The Problem**:
Phase 5 creates internal blob endpoints (leader→follower replication). These must restrict access to cluster members only. How to validate?

**The Solution**:
DotNext provides `IClusterMembership` service (available in DI). Use it to validate that incoming requests come from known cluster members.

**Implementation**:
```csharp
public class ClusterTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IClusterMembership _membership;
    private readonly ILogger<ClusterTokenAuthenticationHandler> _logger;

    public ClusterTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory factory,
        UrlEncoder encoder,
        IClusterMembership membership,
        ILogger<ClusterTokenAuthenticationHandler> logger)
        : base(options, factory, encoder)
    {
        _membership = membership;
        _logger = logger;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments("/api/internal"))
            return AuthenticateResult.NoResult();

        var token = Request.Headers["X-Cluster-Token"].ToString();
        var remoteAddress = Context.Connection.RemoteIpAddress?.ToString();

        // Verify token
        if (!VerifyClusterToken(token))
        {
            _logger.LogWarning("Invalid cluster token from {Remote}", remoteAddress);
            return AuthenticateResult.Fail("Invalid cluster token");
        }

        // Verify remote is a cluster member
        var isClusterMember = _membership.Members.Any(m =>
            m.Endpoint.ToString().Contains(remoteAddress));

        if (!isClusterMember)
        {
            _logger.LogWarning("Request from non-cluster member {Remote}", remoteAddress);
            return AuthenticateResult.Fail("Not a cluster member");
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "cluster-node") }, Scheme.Name));

        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name));
    }

    private bool VerifyClusterToken(string token)
    {
        // Use static token from configuration or derive from cluster state
        var expected = Environment.GetEnvironmentVariable("CONFMAN_CLUSTER_TOKEN");
        return !string.IsNullOrEmpty(token) && token == expected;
    }
}
```

**Registration**:
```csharp
// In Program.cs
builder.Services.AddAuthentication("ClusterToken")
    .AddScheme<AuthenticationSchemeOptions, ClusterTokenAuthenticationHandler>("ClusterToken", null);

// Apply to internal endpoints via policy
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("InternalCluster", policy =>
        policy.RequireRole("cluster-node"));
```

**Action for Plan**: Phase 5 uses IClusterMembership for validation. Simpler than custom token validation.

---

### Phase 6: Hard Cutover Migration & Post-Wipe Startup

#### Learning 6.1: Bootstrap Strategy (Revisited)

**Source**: `docs/solutions/2026-02-01-dotnext-coldstart-leader-tracking-loss.md`

**From Phase 1 Decision**: The cluster will use static membership (pre-configured via `appsettings.json`).

**Migration Runbook**:
1. **Preparation**: Ensure all 3 nodes have `appsettings.nodeN.json` with identical cluster members
2. **Shutdown**: Stop all 3 nodes gracefully
3. **Wipe**: Delete `data-6100/`, `data-6200/`, `data-6300/` directories (persistent Raft state)
4. **Restart**: Start all 3 nodes simultaneously
   ```bash
   # Node 1
   CONFMAN_NODE_ID=node1 dotnet run --project src/Confman.Api &
   # Node 2
   CONFMAN_NODE_ID=node2 dotnet run --project src/Confman.Api &
   # Node 3
   CONFMAN_NODE_ID=node3 dotnet run --project src/Confman.Api &
   ```
5. **Verify**: Check `/health/ready` on all nodes within ~3 seconds
   ```bash
   for port in 6100 6200 6300; do
     curl -s http://localhost:$port/health/ready | jq '.cluster'
     # Should show: { "role": "follower|leader", "leaderKnown": true, "leader": "http://..." }
   done
   ```

**What NOT to Do**:
- Do NOT start with `--coldStart true` on any node
- Do NOT use mixed timeouts (e.g., 150ms election on one node, 1000ms on another)
- Do NOT proceed with data migration if `leaderKnown: false` persists >5 seconds

**Action for Plan**: Phase 6 runbook must include this verbatim. Test migration on staging before production.

---

#### Learning 6.2: Snapshot Timing Invariant Post-Migration

**Source**: `docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md` (Invariant #8)

**Verification Checklist**:
```bash
# Phase 6 stress test (write 10K config entries, observe snapshots)

# 1. Monitor snapshot creation time (from logs)
grep "Creating snapshot" /tmp/confman-*.log
# Expected output: Creating snapshot (1000 configs, ~1MB metadata)
#                  Snapshot created (47ms)  ← Should be <100ms for metadata-only

# 2. Verify no spurious elections (error patterns to NEVER see)
grep -i "transition to candidate state" /tmp/confman-*.log
grep -i "no leader in cluster" /tmp/confman-*.log
# Both should return empty

# 3. Verify leader tracking (all nodes should have leader)
curl -s http://localhost:6100/health/ready | jq '.cluster.leaderKnown'
curl -s http://localhost:6200/health/ready | jq '.cluster.leaderKnown'
curl -s http://localhost:6300/health/ready | jq '.cluster.leaderKnown'
# All should be: true

# 4. Measure write throughput
# Should maintain >100 req/s for small configs
# Should maintain >10 req/s for 1MB configs (limited by snapshot I/O)
```

**What Indicates Failure**:
```
[WRN] Transition to Candidate state has started
[WRN] No leader in cluster
[ERR] Cluster member http://127.0.0.1:6200/ is unavailable
```
If observed, snapshot time > election timeout. Increase election timeout or reduce snapshot size.

**Action for Plan**: Phase 6 runbook includes stress test. Document observed snapshot times and confirm invariant holds.

---

#### Learning 6.3: WAL Null Bytes Under High-Volume Migration

**Source**: `docs/solutions/dotnext-wal-null-bytes-log-entry-deserialization.md`

**Context**: Phase 6 migration involves writing thousands of `BlobPointerCommand` entries at high volume (concurrent writes). This triggers WAL null bytes.

**What You'll See** (If Phase 2 Correctly Implemented):
```
[WRN] Skipped 8 null bytes before JSON in log entry (DotNext WAL padding)
[DBG] Entry 12345: payload length=215, first bytes: 00 00 00 00 7B 22 73 63 68 ...
```
This is normal and harmless. The null bytes are skipped and the command applies correctly.

**What You'll See** (If Phase 2 NOT Implemented):
```
[ERR] JsonException: 'null' is an invalid start of a value. Path: $ | LineNumber: 0 | ByteIndex: 0.
[ERR] Error applying log entry to state machine at index 12345
[WRN] Cluster member http://127.0.0.1:6200/ is unavailable
```
This indicates missing null-byte skipping. Implement Phase 2 fix immediately.

**Action for Plan**: Phase 2 implementation is a prerequisite for Phase 6. Verify during migration that logs do NOT contain `JsonException`.

---

## Summary Matrix

| Phase | Blocking Decision | Required Implementation | Gotcha to Avoid |
|-------|------------------|------------------------|-----------------|
| **1** | Static bootstrap (no coldStart) | Design document | Never mix coldStart:true with pre-configured members |
| **2** | N/A | Null-byte skipping in deserialization | Forgetting defensive payload parsing under concurrent writes |
| **2** | N/A | Verify MaxRequestBodySize=null | Tightening limit and breaking Raft RPC bundling |
| **3** | N/A | Streaming snapshot serialization | Using SerializeToUtf8Bytes (causes OOM) |
| **3** | N/A | SnapshotInterval=50 tuning | High interval causing WAL bloat and latency degradation |
| **3** | N/A | Verify timing invariant | snapshot_time exceeding election_timeout |
| **4** | N/A | Streaming blob reads | Buffering blobs to byte[] (LOH fragmentation) |
| **5** | N/A | IClusterMembership auth | Custom token validation (redundant) |
| **6** | Static bootstrap verification | Migration runbook | Starting cluster with mixed bootstrap strategies |
| **6** | N/A | Snapshot timing validation | Skipping stress test (hidden regressions) |
| **6** | N/A | WAL null-byte verification | Assuming Phase 2 not needed (1% failure rate catastrophic) |

---

## Critical Path

1. **Phase 1**: Decide bootstrap strategy (static) — gates all others
2. **Phase 2**: Implement null-byte skipping + verify Kestrel limit — blocks Phase 6 migration
3. **Phase 3**: Design Snapshot v3, apply streaming + SnapshotInterval tuning
4. **Phase 4**: Implement streaming blob reads
5. **Phase 5**: Add cluster token auth via IClusterMembership
6. **Phase 6**: Execute migration with runbook; verify all invariants

---

## Reference Files

- `/Users/ancris/code/distributed/confman/docs/solutions/2026-02-08-large-payload-performance-insights.md`
- `/Users/ancris/code/distributed/confman/docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md`
- `/Users/ancris/code/distributed/confman/docs/solutions/dotnext-wal-null-bytes-log-entry-deserialization.md`
- `/Users/ancris/code/distributed/confman/docs/solutions/2026-01-24-dotnext-raft-cluster-members-configuration.md`
- `/Users/ancris/code/distributed/confman/docs/solutions/2026-02-01-dotnext-coldstart-leader-tracking-loss.md`
