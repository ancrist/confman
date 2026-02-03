---
date: 2026-02-02
topic: cluster-benchmark
---

# Cluster Benchmark Suite

## What We're Building

A Python benchmark suite using [Locust](https://locust.io/) that measures throughput, latency distribution, and Raft consensus overhead across the 3-node Confman cluster. The suite runs three dataset tiers (1K, 10K, 100K entries) with four payload sizes (10B, 1KB, 100KB, 1MB) and reports p50/p95/p99/max latency plus ops/sec for reads, writes, and mixed workloads.

The benchmark runs headlessly via a wrapper script that orchestrates tiers, collects CSV/JSON results, and produces a summary report.

## Why Locust

| Considered | Verdict |
|------------|---------|
| Locust | **Chosen** — pure Python, built-in latency histograms (p50/p95/p99), concurrent user simulation, headless mode with CSV export, web UI for interactive exploration |
| NBomber (C#) | Good .NET fit but heavier setup, less flexible scripting |
| Plain aiohttp + statistics | Full control but reinvents reporting, no web UI |
| k6 (Grafana) | Excellent tool but JavaScript-based, adds non-Python dependency |
| Bash + curl | Too primitive for concurrent load generation and statistics |

## Key Decisions

- **Locust over roll-your-own**: Built-in percentile tracking, concurrent user spawning, and CSV export eliminate boilerplate
- **Headless + wrapper script**: Automated runs for CI/reproducibility, but Locust's web UI available for interactive debugging
- **Separate locustfile per scenario**: Cleaner than one monolithic file with conditional logic
- **Target all 3 nodes**: Reads hit followers (simulates real client behavior), writes go to leader (or get 307-redirected)
- **1MB max payload**: Tests serialization and network limits without being unreasonable for a config store

## Benchmark Tiers

| Tier | Entries | Namespaces | Purpose |
|------|---------|------------|---------|
| Small | 1,000 | 10 | Warm-up, baseline latency profiling |
| Large | 10,000 | 50 | Sustained throughput measurement |
| Very Large | 100,000 | 100 | Stress test — LiteDB, Raft log, snapshots |

## Payload Sizes

| Label | Size | Example |
|-------|------|---------|
| Tiny | ~10 B | `"enabled"` |
| Medium | ~1 KB | JSON config blob |
| Large | ~100 KB | Embedded template or schema |
| Very Large | ~1 MB | Large document (stress test) |

## Test Scenarios

### 1. Sequential Write Baseline
- 1 user, writes to leader only
- Measures raw per-request write latency without concurrency noise
- Run for each payload size

### 2. Concurrent Writes
- Ramp from 1 → 10 → 50 → 100 concurrent users
- All writes to leader (follows 307 redirects)
- Measures throughput ceiling and latency degradation under load

### 3. Sequential Read Baseline
- 1 user, reads from a single follower
- Pre-populated dataset, random key selection
- Measures raw read latency

### 4. Concurrent Reads
- Ramp from 1 → 10 → 50 → 100 concurrent users
- Reads distributed across all 3 nodes (round-robin)
- Measures read throughput and consistency across nodes

### 5. Mixed Read/Write Workload
- 80% reads / 20% writes (realistic ratio)
- 50 concurrent users
- Reads from any node, writes to leader
- Measures realistic operational profile

### 6. Raft Consensus Overhead
- Compare: write latency to leader vs read latency from follower
- Same payload, same concurrency
- Quantifies the replication tax

### 7. Audit Trail Query Performance
- Query audit logs at increasing dataset sizes (after 1K, 10K, 100K writes)
- Measures how audit query latency grows with data volume

### 8. Payload Size Sweep
- Fixed concurrency (10 users), sequential tiers
- Sweep through 10B → 1KB → 100KB → 1MB payloads
- Measures how payload size affects throughput and latency

### 9. Snapshot/Restore Performance
- Populate cluster to tier size (1K, 10K, 100K entries)
- Trigger snapshot via health/ready endpoint or kill+restart a follower
- Measure: snapshot creation time, snapshot file size, restore time on a fresh node
- Not a Locust scenario — standalone measurement in the wrapper script

### 10. Single-Node Baseline (No Raft)
- Run scenarios 1-5 against a single standalone node (no cluster membership)
- Same dataset tiers and payload sizes
- Compare results against cluster runs to quantify Raft consensus overhead
- Activated with `--mode single`

## Metrics Collected

| Metric | Source |
|--------|--------|
| Requests/sec (throughput) | Locust stats |
| Latency p50, p95, p99, max | Locust percentile tracking |
| Error rate (%) | Locust failure count |
| Response size (bytes) | Locust stats |
| Raft term / leader changes | Health endpoint polling during test |

## Project Structure

```
benchmarks/
├── README.md                    # How to install and run
├── requirements.txt             # locust, requests
├── run_benchmark.py             # Wrapper: orchestrates tiers, collects results
├── locustfiles/
│   ├── write_sequential.py      # Scenario 1: sequential writes
│   ├── write_concurrent.py      # Scenario 2: concurrent writes
│   ├── read_sequential.py       # Scenario 3: sequential reads
│   ├── read_concurrent.py       # Scenario 4: concurrent reads
│   ├── mixed_workload.py        # Scenario 5: 80/20 read/write
│   ├── audit_query.py           # Scenario 7: audit trail queries
│   └── payload_sweep.py         # Scenario 8: payload size sweep
├── lib/
│   ├── data_generator.py        # Random namespace/key/value generators
│   ├── cluster.py               # Cluster health checks, leader discovery
│   └── report.py                # Result aggregation, summary output
└── results/                     # Output directory for CSV/JSON reports
    └── .gitkeep
```

## Wrapper Script Behavior

```
$ python run_benchmark.py --tier small --scenarios all --host http://127.0.0.1:6100

[1/8] Sequential Write Baseline (1K entries, 10B payload)
  Spawning 1 user... done.
  Throughput: 342 req/s | p50: 2.1ms | p95: 4.8ms | p99: 12.3ms

[2/8] Concurrent Writes (1K entries, ramp 1→100 users)
  ...

Summary written to: results/2026-02-02T21-30-00-small.json
```

**CLI options:**
- `--tier small|large|xlarge|all` — which dataset tier
- `--scenarios all|write|read|mixed|audit|payload` — which scenarios
- `--host` — cluster entry point (auto-discovers leader)
- `--api-key` — API key for authentication
- `--output-dir` — results directory
- `--web-ui` — launch Locust web UI instead of headless

## Data Generation Strategy

- **Namespaces**: `bench-{tier}-{i}` (e.g., `bench-small-001`)
- **Keys**: `key-{uuid4_short}` (random, avoids hotspots)
- **Values**: Random alphanumeric strings of target payload size
- **Cleanup**: Each tier creates its own namespaces, deleted after run (or `--no-cleanup` to inspect)

## Resolved Questions

- **Snapshot/restore benchmark**: Yes — measure snapshot creation and restore time at each tier (1K, 10K, 100K)
- **Single-node comparison mode**: Yes — `--mode single` runs the same workload against one node without Raft to isolate consensus overhead
- **Output format**: JSON only (no auto-generated markdown reports)

## Next Steps

→ `/workflows:plan` for implementation details
