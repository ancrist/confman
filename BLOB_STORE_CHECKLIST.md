# Blob Store Feature: Implementation Checklist

Derived from institutional learnings in `/docs/solutions/BLOB_STORE_LEARNINGS.md`

## Pre-Implementation

- [ ] **Decision**: Static cluster bootstrap (pre-configured members) chosen
  - Document in Phase 1 design: "No `--coldStart true` after hard cutover"
  - Verify all 3 nodes have identical `appsettings.nodeN.json` member lists

---

## Phase 2: Blob Pointer Command Implementation

### Serialization Robustness
- [ ] Implement null-byte skipping in `DeserializeCommand`:
  ```csharp
  var jsonStart = 0;
  while (jsonStart < bytes.Length && bytes[jsonStart] == 0)
      jsonStart++;
  if (jsonStart >= bytes.Length || bytes[jsonStart] != '{')
      return null;
  ```
- [ ] Add unit tests with variable null-byte counts (1, 8, 64, 256 bytes)
- [ ] Log warnings when null bytes skipped

### Network Configuration
- [ ] Verify `Program.cs` has `options.Limits.MaxRequestBodySize = null` for Kestrel
- [ ] Add comment: "Required for Raft AppendEntries bundling large payloads"

---

## Phase 3: Snapshot Version 3 Design

### Serialization Strategy
- [ ] Use streaming: `JsonSerializer.SerializeAsync(fileStream, snapshot)` (NOT `SerializeToUtf8Bytes()`)
- [ ] Use streaming on restore: `JsonSerializer.DeserializeAsync<SnapshotData>(fileStream)`
- [ ] Verify no `File.ReadAllBytesAsync()` on restore

### Configuration Tuning
- [ ] Set `appsettings.json`:
  ```json
  {
    "Raft": {
      "SnapshotInterval": 50,
      "lowerElectionTimeout": 1000,
      "upperElectionTimeout": 3000,
      "requestTimeout": "00:00:10"
    }
  }
  ```
- [ ] Verify Raft timing invariant in design doc: "50ms < 1000ms < 10000ms"

---

## Phase 4: Lazy Blob Fetch

### Blob Read Endpoint
- [ ] Implement streaming: `await blob.CopyToAsync(Response.Body)`
- [ ] NO buffering to `byte[]`
- [ ] Set `Response.ContentLength` before streaming (for client progress)
- [ ] Endpoint: `[HttpGet("/api/internal/blobs/{sha256}")]`

### Testing
- [ ] Test with blobs >100MB to verify no OOM
- [ ] Monitor memory during read (should stay flat, no spike)

---

## Phase 5: Internal Endpoints Auth

### Cluster Token Validation
- [ ] Use `IClusterMembership` from DI (already available)
- [ ] Validate both token AND remote IP is cluster member
- [ ] Log warnings for failed auth attempts
- [ ] Protect `/api/internal/blobs/*` endpoints with auth policy

---

## Phase 6: Hard Cutover Migration

### Pre-Migration
- [ ] Create migration runbook with exact commands
- [ ] Schedule maintenance window (cluster unavailable ~5 minutes)
- [ ] Backup current LiteDB files to external storage

### Migration Runbook
```bash
# 1. Shutdown all nodes
pkill -f "dotnet run --project src/Confman.Api"

# 2. Wipe data directories
rm -rf data-6100/ data-6200/ data-6300/

# 3. Start all 3 nodes simultaneously
CONFMAN_NODE_ID=node1 dotnet run --project src/Confman.Api &
CONFMAN_NODE_ID=node2 dotnet run --project src/Confman.Api &
CONFMAN_NODE_ID=node3 dotnet run --project src/Confman.Api &

# 4. Verify cluster health (wait 3-5 seconds)
for port in 6100 6200 6300; do
  curl -s http://localhost:$port/health/ready | jq '.cluster'
  # All should have: "leaderKnown": true
done
```

### Verification Checklist
- [ ] All 3 nodes report `leaderKnown: true` within 5 seconds
- [ ] No "No leader in cluster" in logs
- [ ] No "Transition to Candidate state" in logs after initial election
- [ ] Snapshot timing: < 100ms for metadata-only (confirm in logs)
- [ ] No `JsonException` in logs (indicates null-byte handling working)
- [ ] Stress test: Write 10K configs, measure throughput and latency

### Post-Migration Verification
- [ ] Document observed snapshot times in runbook
- [ ] Confirm Raft invariant: snapshot_time < election_timeout < request_timeout
- [ ] Verify audit events migrated correctly
- [ ] Test blob endpoint: GET `/api/internal/blobs/{hash}` returns correct content
- [ ] Performance: Write throughput should match pre-migration baseline

---

## Gotchas to Avoid

1. ❌ Never start cluster with `--coldStart true` after hard wipe if members are pre-configured
2. ❌ Don't use `SerializeToUtf8Bytes()` for snapshots (OOM with large data)
3. ❌ Don't forget null-byte skipping in deserialization (1% failure rate under concurrency)
4. ❌ Don't buffer blobs to `byte[]` (LOH fragmentation, OOM)
5. ❌ Don't leave `SnapshotInterval` >100 (WAL bloat, latency degradation)
6. ❌ Don't proceed with migration if invariant violations observed in stress test

---

## Success Criteria

- [ ] Phase 1: Decision documented, gates remaining phases
- [ ] Phase 2: Null-byte tests passing, Kestrel limit verified
- [ ] Phase 3: Streaming snapshots, SnapshotInterval tuned, invariant validated
- [ ] Phase 4: Blob reads streaming, no OOM under load
- [ ] Phase 5: Internal endpoints auth working, cluster members validated
- [ ] Phase 6: Migration runbook successful, all health checks pass, stress test confirms no regressions

---

## Related Documentation

- Institutional learnings: `/docs/solutions/BLOB_STORE_LEARNINGS.md`
- Large payload insights: `/docs/solutions/2026-02-08-large-payload-performance-insights.md`
- Snapshot starvation: `/docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md`
- WAL null bytes: `/docs/solutions/dotnext-wal-null-bytes-log-entry-deserialization.md`
- Cluster config: `/docs/solutions/2026-01-24-dotnext-raft-cluster-members-configuration.md`
- Cold start issues: `/docs/solutions/2026-02-01-dotnext-coldstart-leader-tracking-loss.md`
