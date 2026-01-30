# Confman Failure Modes

This document describes failure scenarios for the Confman distributed configuration service, their symptoms, impact, and recovery procedures. Confman uses Raft consensus (via DotNext.Net.Cluster) for distributed coordination.

---

## 1. Minor Follower Failure

**Description:** Fewer than half of the follower nodes become unavailable.

**Impact:**
- Cluster continues accepting reads and writes normally
- Quorum is maintained; Raft log replication proceeds
- Remaining nodes experience slightly higher load
- Audit events continue replicating to available nodes

**Symptoms:**
- Health check failures on affected nodes (`/health/ready` returns unhealthy)
- Dashboard shows fewer nodes in cluster status
- Client connections to failed nodes timeout or refuse
- Logs show failed heartbeat responses from unavailable members

**Recovery:**
- **Automatic:** Clients following HTTP 307 redirects automatically find healthy nodes
- **Manual:** Restart failed nodes; they will catch up via Raft log replay
- **Data:** No data loss; failed nodes receive missed log entries upon recovery

**Confman-Specific Notes:**
- Dashboard polling (2-second interval) will show stale data if connected to a failed node
- API clients should implement retry logic with different node endpoints
- Configuration reads remain available from any healthy node

---

## 2. Leader Failure

**Description:** The current Raft leader node becomes unavailable.

**Impact:**
- **Writes blocked:** No configuration changes, namespace operations, or publish requests can be processed
- **Reads continue:** Followers can still serve read requests (potentially stale)
- **Election triggered:** Remaining nodes hold leader election (typically completes in 1-2 election timeout periods)

**Symptoms:**
- `PUT`/`DELETE` requests return HTTP 503 or timeout
- HTTP 307 redirects point to unreachable leader
- Logs show "leader unreachable" or "starting election" messages
- `/health/ready` may report degraded state during election

**Recovery:**
- **Automatic:** Raft leader election completes; new leader begins accepting writes
- **Timing:** Election typically completes within 150-300ms (configurable election timeout)
- **Client handling:** Retry write requests after brief delay; new leader will be elected

**Confman-Specific Notes:**
- Publishing workflow requests in "Pending Review" state are safe; they're persisted in the Raft log
- Clients should cache leader location but be prepared for 307 redirects to change
- Dashboard will show write failures during election window

---

## 3. Majority Failure (Quorum Loss)

**Description:** More than half of cluster members become unavailable simultaneously.

**Impact:**
- **Complete write unavailability:** Cluster cannot commit any new entries to Raft log
- **Reads may work:** Individual nodes can serve cached/local reads, but data may be stale
- **No leader:** Cannot elect a new leader without majority

**Symptoms:**
- All write operations fail with timeout or 503 errors
- `/health/ready` returns unhealthy on all remaining nodes
- Logs show "cannot reach quorum" or "election failed" messages
- Dashboard shows cluster in degraded/unavailable state

**Recovery:**
- **Wait for recovery:** If nodes will return, wait for majority to come back online
- **Manual intervention required:** If permanent failure, requires disaster recovery:
  1. Identify nodes with most recent Raft log index
  2. Reconfigure cluster membership
  3. Restore from snapshots if necessary
- **Data risk:** Uncommitted writes may be lost

**Confman-Specific Notes:**
- Configuration consumers should cache last-known-good values
- Publishing workflows in progress will be blocked; no approvals or rejections possible
- Audit log will have gaps for the outage period
- Consider running 5-node clusters in production for higher fault tolerance (tolerates 2 failures vs 1)

---

## 4. Network Partition

**Description:** Network failure splits the cluster into isolated groups.

**Impact depends on partition topology:**

### Scenario A: Leader in Majority Partition
- Majority partition continues operating normally
- Minority partition nodes become read-only (stale data)
- No split-brain: minority cannot elect a new leader

### Scenario B: Leader in Minority Partition
- Leader steps down (cannot maintain quorum)
- Majority partition elects new leader
- Old leader's uncommitted writes may be lost
- Minority partition becomes completely unavailable

**Symptoms:**
- Nodes report other nodes as unreachable
- Different clients see different cluster states depending on which partition they reach
- Logs show intermittent connectivity or "member unreachable" errors

**Recovery:**
- **Automatic:** When partition heals, nodes reconcile state via Raft log
- **Consistency guaranteed:** Raft ensures no split-brain; only one partition can make progress
- **Uncommitted data:** Writes accepted by old leader but not replicated may be rolled back

**Confman-Specific Notes:**
- Clients in minority partition will see stale configuration data
- Dashboard connected to minority partition will show incorrect cluster status
- Publishing workflows may appear stuck if approver is in different partition than leader
- **Recommendation:** Deploy nodes across availability zones but within same region to minimize partition risk

---

## 5. Slow Follower (Performance Degradation)

**Description:** A follower node responds slowly due to disk I/O, CPU, or network issues.

**Impact:**
- Cluster continues operating but with degraded performance
- Slow follower may fall behind on log replication
- If follower falls too far behind, it may require snapshot transfer

**Symptoms:**
- Increased commit latency (leader waits for slow follower in some configurations)
- Logs show "follower lagging" or "snapshot required" messages
- Higher memory usage on leader (buffering pending log entries)

**Recovery:**
- Investigate and resolve performance issue on slow node
- If persistent, consider replacing the node
- Snapshots automatically bring lagging nodes up to date

**Confman-Specific Notes:**
- Monitor Raft log index gap between leader and followers
- Slow followers don't affect read availability
- Snapshot transfers may temporarily increase network usage

---

## 6. Disk Full / Storage Failure

**Description:** A node runs out of disk space or experiences storage corruption.

**Impact:**
- Affected node cannot persist Raft log entries or snapshots
- Node becomes unavailable or crashes
- If leader: triggers election and temporary write unavailability
- If follower: reduces fault tolerance margin

**Symptoms:**
- Write operations fail on affected node
- Logs show I/O errors or "disk full" messages
- Node may crash or become unresponsive

**Recovery:**
- **Immediate:** Free disk space or replace storage
- **If corruption:** Remove node from cluster, wipe data directory, rejoin as new member
- **Snapshot:** Rebuilding node will receive snapshot from leader

**Confman-Specific Notes:**
- Raft log and snapshots are stored in configurable data directory
- Monitor disk usage; set alerts at 80% capacity
- Audit events contribute to storage growth; plan retention policy

---

## 7. Snapshot/Restore Failure

**Description:** Snapshot creation or restoration fails.

**Impact:**
- Without snapshots, log replay takes longer after restart
- Very old log entries cannot be compacted
- New nodes cannot join efficiently (must replay entire log)

**Symptoms:**
- Logs show snapshot creation errors
- Increasing Raft log size on disk
- Slow node recovery after restart

**Recovery:**
- Investigate snapshot directory permissions and disk space
- Force snapshot creation once issue resolved
- If persistent, node may need to be rebuilt from scratch

**Confman-Specific Notes:**
- Snapshots include: all configs, namespaces, and audit events (JSON serialized)
- `PersistAsync` and `RestoreAsync` in state machine handle snapshot lifecycle
- Monitor snapshot age; stale snapshots indicate a problem

---

## 8. Bootstrap / Initial Cluster Formation Failure

**Description:** Cluster fails to form during initial startup.

**Impact:**
- No cluster exists; complete unavailability
- Nodes cannot communicate or elect leader

**Symptoms:**
- All nodes stuck in "waiting for cluster" state
- Logs show "cannot discover peers" or "bootstrap failed" messages
- No leader election occurs

**Recovery:**
1. Verify all nodes have correct peer addresses in configuration
2. Check network connectivity between all nodes (gRPC ports)
3. If using static configuration, ensure all nodes list the same members
4. **Clean restart:** Remove data directories on all nodes, restart simultaneously

**Confman-Specific Notes:**
- Node addresses configured in `appsettings.json` (static discovery)
- All nodes must agree on initial cluster membership
- Consider using consistent configuration management for node configs (ironic but necessary)

---

## 9. Client-Side Failures

**Description:** Issues in client applications consuming configuration.

**Impact:**
- Individual applications may use stale or missing configuration
- Does not affect Confman cluster itself

**Symptoms:**
- Application logs show connection failures to Confman
- Applications using default/fallback values
- Inconsistent behavior across application instances

**Recovery:**
- Implement robust retry logic with exponential backoff
- Cache last-known-good configuration locally
- Use health check endpoints to detect Confman availability

**Confman-Specific Notes:**
- Clients should handle HTTP 307 redirects for write operations
- Implement circuit breaker pattern for Confman calls
- Consider local config file fallback for critical settings

---

## Failure Mode Summary Table

| Failure | Write Impact | Read Impact | Auto-Recovery | Data Loss Risk |
|---------|--------------|-------------|---------------|----------------|
| Minor Follower | None | None | Yes | None |
| Leader Failure | Blocked (seconds) | None | Yes | None |
| Majority Failure | **Complete outage** | Stale reads | No | Uncommitted |
| Network Partition | Depends | Depends | Yes | Uncommitted |
| Slow Follower | Latency increase | None | Yes | None |
| Disk Full | Node-specific | Node-specific | Manual | Possible |
| Snapshot Failure | None | None | Manual | None |
| Bootstrap Failure | **No cluster** | **No cluster** | Manual | N/A |
| Client Failure | N/A | N/A | App-specific | N/A |

---

## Recommendations for Production

1. **Cluster Size:** Run 5 nodes (tolerates 2 failures) rather than 3 (tolerates 1)
2. **Monitoring:** Alert on quorum health, leader changes, and log replication lag
3. **Backups:** Regular snapshots stored off-cluster for disaster recovery
4. **Network:** Deploy across availability zones; avoid cross-region latency
5. **Disk:** Use SSDs; monitor capacity; set 80% usage alerts
6. **Clients:** Implement retry, caching, and circuit breaker patterns
7. **Testing:** Regularly practice failure scenarios (chaos engineering)

---

## Related Documentation

- [CLAUDE.md](./CLAUDE.md) - Project overview and architecture
- [CLUSTER_NOTES.md](./CLUSTER_NOTES.md) - Cluster configuration details
- [DotNext.Net.Cluster Documentation](https://dotnet.github.io/dotNext/) - Raft implementation details
