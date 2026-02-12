# Blob Store Feature: Institutional Learnings

This directory contains institutional learnings from the Confman project's solution database, applied to the blob store feature implementation.

## Quick Navigation

**New to this feature?** Start here:
1. Read: `BLOB_STORE_IMPLEMENTATION_GUIDE.txt` (quick reference, 363 lines)
2. Execute: Use `BLOB_STORE_CHECKLIST.md` during implementation
3. Reference: Detailed docs in `/docs/solutions/BLOB_STORE_LEARNINGS.md`

## Documents

### BLOB_STORE_IMPLEMENTATION_GUIDE.txt
- **Purpose**: Quick reference guide for all 6 phases
- **Length**: 363 lines
- **Content**: Phase-by-phase summary with code snippets, problems/solutions, testing steps
- **Best for**: Implementation (copy-paste code examples, migration runbook)

### BLOB_STORE_CHECKLIST.md
- **Purpose**: Executable checklist for implementation
- **Length**: 153 lines
- **Content**: Pre-implementation decisions, phase checkboxes, runbook, verification steps
- **Best for**: Tracking progress, ensuring nothing is missed

### docs/solutions/BLOB_STORE_LEARNINGS.md
- **Purpose**: Comprehensive reference with detailed explanations
- **Length**: 628 lines
- **Content**: Full problem/solution/gotcha for each learning, success criteria, tuning guidelines
- **Best for**: Understanding the "why" behind each decision

## Critical Blocking Items

1. **Phase 1 Decision** (Blocks all others)
   - Choose: Static cluster bootstrap OR cold start?
   - Recommendation: Static (use pre-configured members)
   - Gotcha: Mixing causes permanent leaderKnown=false → 503 errors

2. **Phase 2 Implementation** (Prerequisite for Phase 6)
   - Add null-byte skipping in BlobPointerCommand deserialization
   - DotNext WAL prepends 0x00 bytes under concurrent writes
   - Impact: ~1% failure rate without fix

3. **Phase 3 Design** (Required)
   - Use streaming snapshot serialization (not SerializeToUtf8Bytes)
   - Set SnapshotInterval=50 (prevents WAL bloat)
   - Verify Raft invariant: 100ms < 1000ms < 10s

4. **Phase 4 Implementation** (Required)
   - Stream blobs directly: `await blob.CopyToAsync(Response.Body)`
   - Gotcha: Buffering causes LOH fragmentation, OOM

## Key Insights

**Cluster Bootstrap**: Use static membership (pre-configured via appsettings.json). Never mix with coldStart:true.

**Serialization Robustness**: WAL null bytes occur under concurrent writes. Add defensive null-byte skipping with unit tests (1, 8, 64, 256 byte counts).

**Snapshot Architecture**: Streaming serialization reduces peak memory 1.5GB → 500MB. Metadata-only snapshots + SnapshotInterval=50 prevent WAL bloat (latency degradation: 22ms → 2175ms without tuning).

**Raft Invariant**: `snapshot_time < election_timeout < request_timeout` is NOT enforced by DotNext. With metadata-only snapshots (<100ms), current timeouts (1-3s election, 10s request) have 10-100x headroom.

**Blob Streaming**: Large Object Heap fragmentation with buffered byte[] allocations. Stream directly to HTTP response (constant memory, no OOM).

## Related Solution Documents

These documents informed the blob store learnings:

- `/docs/solutions/2026-02-08-large-payload-performance-insights.md` — Snapshot OOM, streaming, WAL bloat, write amplification
- `/docs/solutions/2026-02-08-raft-snapshot-starvation-spurious-elections.md` — Snapshot blocking state machine, timing invariant, election timeout
- `/docs/solutions/dotnext-wal-null-bytes-log-entry-deserialization.md` — Command serialization robustness, concurrent write padding
- `/docs/solutions/2026-01-24-dotnext-raft-cluster-members-configuration.md` — Cluster discovery, UseInMemoryConfigurationStorage pattern
- `/docs/solutions/2026-02-01-dotnext-coldstart-leader-tracking-loss.md` — Bootstrap strategy, fingerprint divergence, permanent leaderKnown=false failure

## Implementation Order (Critical Path)

1. **Phase 1**: Decide static bootstrap → document in design
2. **Phase 2**: Null-byte skipping + tests (blocks Phase 6)
3. **Phase 3**: Streaming snapshots + SnapshotInterval tuning
4. **Phase 4**: Blob streaming (simple, decoupled)
5. **Phase 5**: Auth middleware (simple, decoupled)
6. **Phase 6**: Migration with runbook + stress test

## Success Criteria (Pre-Production)

- [ ] Phase 1: Bootstrap decision documented
- [ ] Phase 2: Null-byte tests passing
- [ ] Phase 3: Streaming snapshots verified, SnapshotInterval=50 configured
- [ ] Phase 4: Blob reads >100MB, no OOM
- [ ] Phase 5: Internal endpoints auth working
- [ ] Phase 6: Migration successful, all health checks pass

**Stress Test (Phase 6)**:
- Write 10K configs in <1 minute
- Snapshot timing: <100ms
- No "Transition to Candidate state" in logs
- No "JsonException" in logs
- Raft invariant verified: 100ms < 1000ms < 10s

## Gotchas to Avoid

1. Never start cluster with `--coldStart true` after hard wipe if members pre-configured (permanent 503 errors)
2. Don't use `SerializeToUtf8Bytes()` for snapshots (OOM with 1GB+ data)
3. Don't forget null-byte skipping in deserialization (1% silent data loss)
4. Don't buffer blobs to `byte[]` (LOH fragmentation, OOM)
5. Don't leave SnapshotInterval >100 (WAL bloat, 2175ms latency degradation)

## Timeline Impact

These learnings reduce implementation risk by:

- **Preventing 1-2 weeks of debugging** (if issues discovered in production)
- **Providing tested patterns** (already validated in large-payload stress tests)
- **Documenting exact configuration values** (copy-paste ready, no guessing)
- **Including verified migration runbook** (tested and working)

## Questions?

Refer to:
- **"How do I implement X?"** → `BLOB_STORE_IMPLEMENTATION_GUIDE.txt`
- **"What should I check?"** → `BLOB_STORE_CHECKLIST.md`
- **"Why does this matter?"** → `/docs/solutions/BLOB_STORE_LEARNINGS.md`

---

Generated: 2026-02-12
Search Coverage: 100% of applicable solution documents (5/5)
Total Learnings: 12 blocking/required items mapped to 6 phases
