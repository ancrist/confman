---
title: "feat: Cluster benchmark suite (Python/Locust)"
type: feat
date: 2026-02-02
deepened: 2026-02-03
---

# feat: Cluster Benchmark Suite (Python/Locust)

## Enhancement Summary

**Deepened on:** 2026-02-03
**Sections enhanced:** 12
**Research agents used:** performance-oracle, architecture-strategist, code-simplicity-reviewer, security-sentinel, kieran-python-reviewer, dotnet-benchmark-designer, pattern-recognition-specialist

### Key Improvements
1. **Simplified architecture**: Consolidated from 12 Python files to 4, from 7 locustfiles to 3, using Locust CLI (`--csv`) instead of programmatic `Environment` API — eliminates gevent dependency in wrapper
2. **Critical bug fixes**: API key mismatch (`confman_dev_key` → `confman_dev_abc123`), memory amplification in `generate_value()` (50MB for 1MB payload), env var size limits breaking at xlarge tier, missing HTTP error handling, broken import paths
3. **Benchmark methodology**: Added JIT warm-up phase (60s), extended measurement windows (120s), `catch_response=True` for correct error tracking, file-based key registry, `os.urandom` for payload generation
4. **Security hardening**: `tempfile.mkdtemp()` for key registry, env var for API key, `--allow-destructive` flag for scenario 9, separate admin/reader keys
5. **Statistical rigor**: Multiple runs (7+) with mean/stddev/95% CI, Locust `constant_throughput` for open-loop validation, stats reset between concurrency stages

### New Considerations Discovered
- **Read barrier overhead**: All reads pay Raft consensus cost via `ReadBarrierMiddleware` — invalidates the naive `write_p50 - read_p50` formula for Raft overhead
- **GC pressure**: 1MB payloads cause .NET Large Object Heap allocations → Gen2 GC pauses dominate tail latency
- **Coordinated omission**: Locust's closed-loop model understates tail latency; validate with `constant_throughput` wait time
- **LiteDB write lock**: Process-wide single writer means benchmark may measure LiteDB lock contention, not Raft consensus
- **Single-node mode**: Application cannot run without Raft; must use single-member cluster (`coldStart: true`) not a Raft-free mode

---

## Overview

A Python benchmark suite using [Locust](https://locust.io/) that measures throughput, latency distribution, and Raft consensus overhead across the 3-node Confman cluster. The suite runs two dataset tiers (1K, 10K entries) with a default 1KB payload and reports p50/p95/p99/max latency plus ops/sec for reads, writes, and mixed workloads.

The benchmark runs headlessly via a wrapper script that shells out to `locust --headless --csv` for each scenario, collects CSV results, and handles seeding/cleanup.

### Research Insights: Simplified Scope

The original brainstorm spec'd 10 scenarios, 3 tiers, 4 payload sizes, and 7 locustfiles. Multiple reviewers independently recommended the same simplification:

**Consolidation rationale:**
- `write_sequential.py` and `write_concurrent.py` differ only in user count (wrapper parameter, not locustfile concern) → merge into `write.py`
- `read_sequential.py` and `read_concurrent.py` are identical code → merge into `read.py`
- `payload_sweep.py` differs from `write_concurrent.py` by one `name=` tag → eliminated
- `audit_query.py`, snapshot/restore (scenario 9), single-node baseline (scenario 10) → **deferred to v2** when MVP results indicate which questions remain

**Deferred features (add when needed):**
- `xlarge` tier (100K entries) — seeding takes 5+ minutes, blocks fast iteration
- 4 payload sizes with sweep — default 1KB covers common case; pass `BENCH_PAYLOAD_SIZE` env var for manual testing
- `--web-ui` flag — run `locust --locustfile` directly for web UI
- `--scenarios` filter — MVP runs all 5 scenarios; takes <5 minutes at `small` tier
- `--mode single` — requires `appsettings.single.json` (see architecture notes below)

## Problem Statement / Motivation

The Confman cluster has no performance baseline. We don't know:
- How many ops/sec the cluster can sustain for reads and writes
- What the latency distribution looks like under load
- How much overhead Raft consensus adds vs a single node
- How payload size and dataset scale affect performance
- Whether LiteDB, Raft log, or snapshots become bottlenecks at scale

Without benchmarks, capacity planning is guesswork and performance regressions go undetected.

## Proposed Solution

A self-contained Python project under `benchmarks/` that uses Locust for load generation and percentile tracking. A wrapper script (`run_benchmark.py`) seeds data, shells out to `locust --headless --csv` for each scenario, and handles cleanup.

Key design decisions (refined from brainstorm + review):
- **Locust CLI mode** (`--headless --csv`) instead of programmatic `Environment` API — eliminates gevent dependency, uses Locust's blessed interface, CSV output gives p50/p95/p99/max/avg for free
- **3 locustfiles** (`write.py`, `read.py`, `mixed.py`) — user count is a wrapper parameter, not a locustfile concern
- **File-based key registry** — env vars have platform-specific size limits (macOS ARG_MAX ~1MB); JSON file in temp directory is safer
- **`os.urandom` + base64 for payloads** — `random.choices(chars, k=1_000_000)` creates 50MB Python heap for 1MB payload
- **`catch_response=True`** — Locust doesn't treat 4xx/5xx as failures by default; must explicitly check status codes
- **Two API keys** — admin key for setup/teardown, viewer key for read scenarios (least privilege, catches auth regressions)

## Technical Considerations

### API Contracts

The benchmark interacts with these endpoints:

| Operation | Method | Endpoint | Auth Policy | Notes |
|-----------|--------|----------|-------------|-------|
| List configs | GET | `/api/v1/namespaces/{ns}/config` | ReadOnly | Returns array |
| Read config | GET | `/api/v1/namespaces/{ns}/config/{key}` | ReadOnly | Returns single entry |
| Write config | PUT | `/api/v1/namespaces/{ns}/config/{key}` | Write | 307 redirect to leader if follower |
| Delete config | DELETE | `/api/v1/namespaces/{ns}/config/{key}` | Write | 307 redirect to leader |
| Create namespace | PUT | `/api/v1/namespaces/{ns}` | Admin | 307 redirect to leader |
| Delete namespace | DELETE | `/api/v1/namespaces/{ns}` | Admin | 307 redirect to leader |
| Audit trail | GET | `/api/v1/namespaces/{ns}/audit?limit=N` | ReadOnly | Limit 1-1000, default 50 |
| Health/ready | GET | `/health/ready` | None | Returns cluster role, term, leader |

### Authentication

All benchmark requests use the `X-Api-Key` header. Two API keys are used:

| Key | Value | Roles | Used For |
|-----|-------|-------|----------|
| Admin | `confman_dev_abc123` | `admin` | Namespace creation/deletion (setup/teardown) |
| Reader | `confman_dev_reader` | `viewer` | Read-only scenarios (3, 4, read portion of mixed) |

> **Important**: The admin key value is `confman_dev_abc123` (from `appsettings.json:28`), NOT `confman_dev_key`. The ReadOnly policy accepts `admin`, `publisher`, `editor`, and `viewer` roles. The Write policy accepts `admin`, `publisher`, and `editor`. Using two separate keys makes the benchmark a lightweight integration test for the authorization layer and catches role mismatch regressions like the one documented in `docs/solutions/2026-02-02-role-name-mismatch-prevents-read-access.md`.

### Research Insights: API Key Handling

**Security best practices (from security-sentinel):**
- Prefer `CONFMAN_BENCH_API_KEY` environment variable over `--api-key` CLI flag — CLI args visible in `ps aux` and shell history
- Never include API keys in results JSON output
- Document: "Do not use production API keys with this tool"

### Leader Forwarding (307 Redirects)

Write operations (PUT/DELETE) return HTTP 307 when hitting a follower. The benchmark must:
- **For writes**: Use `allow_redirects=True` (Python `requests` default) to transparently follow 307s
- **For Locust**: `HttpUser` (based on `requests`) handles redirects natively; `FastHttpUser` does not
- **Track redirect overhead**: Use Locust `@events.request.add_listener` to count 307s without modifying locustfiles

### Research Insights: Read Barrier Impact

**Critical discovery (from architecture-strategist):** The system has `ReadBarrierMiddleware` enabled by default (`ReadBarrier:Enabled: true`). This calls `cluster.ApplyReadBarrierAsync()` on every GET to `/api/*`. This means **reads also pay a Raft consensus cost** — the naive `write_p50 - read_p50` formula underestimates Raft overhead.

**Correct formula:**
```
raft_write_overhead = cluster_write_p50 - single_node_write_p50
raft_read_overhead  = cluster_read_p50 - single_node_read_p50  (due to read barrier)
total_raft_tax      = raft_write_overhead + raft_read_overhead
```

**For MVP**: Document that read latencies include linearizable read barrier cost. True isolation requires single-node baseline comparison (deferred to v2).

### Locust Patterns

**CLI invocation** (not programmatic API):
```bash
locust --headless --locustfile write.py --host http://127.0.0.1:6100 \
  --users 50 --spawn-rate 10 --run-time 30s --csv results/write-50u
```

This produces three CSV files per run: `*_stats.csv` (p50/p95/p99/max/avg, RPS, failures), `*_stats_history.csv` (time series), `*_failures.csv` (error details).

**Error handling** — use `catch_response=True`:
```python
with self.client.get(url, catch_response=True) as response:
    if response.status_code >= 400:
        response.failure(f"HTTP {response.status_code}")
    else:
        response.success()
```

Without this, 5xx responses during leader election are counted as successes, corrupting latency stats.

**Wait times**: Use `constant(0)` instead of `between(0, 0)` for sequential scenarios — avoids unnecessary `gevent.sleep(0)` scheduling overhead.

**URL naming**: Always use `name="/config/[key] PUT"` on requests with variable URLs to group stats correctly.

### Research Insights: .NET JIT Warm-up

**From dotnet-benchmark-designer:** .NET Tiered Compilation requires ~30 calls per method for Tier-1 JIT promotion. The first 60 seconds of benchmark traffic will show artificially high latency due to JIT compilation, LiteDB cache warming, and Raft state machine initialization.

**Mitigation:** Add a warm-up phase to each scenario:
1. Run 60s of load traffic
2. Reset Locust stats (`env.runner.stats.reset_all()` if using programmatic API, or simply discard the first run's CSV)
3. Begin the timed measurement

For CLI mode: run each scenario twice — first invocation is warm-up (short duration, results discarded), second is measurement.

### Research Insights: Coordinated Omission

**From dotnet-benchmark-designer:** Locust uses a closed-loop model — each user waits for a response before sending the next request. This means when the server slows down, Locust sends fewer requests, which understates tail latency (coordinated omission).

**Mitigation for v2:** Use `constant_throughput(N)` wait time to maintain a fixed request rate regardless of response time. This approximates an open-loop model and reveals true tail latency. Validate with `wrk2` for critical findings.

### Data Seeding Strategy

Seeding happens *outside* Locust to separate setup cost from measurement:

1. **Create namespaces**: `PUT /api/v1/namespaces/bench-{tier}-{i}` (directed to leader)
2. **Create config entries**: `PUT /api/v1/namespaces/{ns}/config/{key}` with target payload size
3. **Build key registry**: Write `(namespace, key)` tuples to JSON file in temp directory
4. **Verify**: Poll `/health/ready` to confirm replication; read back a sample of entries from a follower

### Research Insights: Seeding at Scale

**From performance-oracle:** At `large` tier (10K entries), sequential seeding via HTTP at ~300 req/s takes ~33 seconds. Acceptable for MVP.

For future `xlarge` tier (100K entries): ~5.5 minutes sequential. Consider:
- Parallel namespace creation with `concurrent.futures.ThreadPoolExecutor` (5-10 workers)
- Progress reporting during seeding (log every 1000 entries)
- Direct LiteDB insertion for stress-test tiers (bypasses Raft, measures storage only)

### Key Registry for Reads

Read scenarios need to know which keys exist. The wrapper writes a JSON file during seeding:

```python
import tempfile, json

registry_dir = tempfile.mkdtemp(prefix="confman-bench-")
registry_path = os.path.join(registry_dir, f"keys-{tier}.json")
with open(registry_path, "w") as f:
    json.dump(key_list, f)
os.environ["BENCH_KEY_REGISTRY_FILE"] = registry_path
```

**Why file instead of env var:**
- macOS `ARG_MAX` is ~1MB; 100K keys as JSON exceeds this
- Files are inspectable for debugging
- No symlink attack risk when using `tempfile.mkdtemp()` (creates directory with 0700 permissions)

Locustfiles load the registry once per process (not per greenlet):

```python
_key_cache = None

def load_keys():
    global _key_cache
    if _key_cache is None:
        path = os.environ["BENCH_KEY_REGISTRY_FILE"]
        with open(path) as f:
            _key_cache = json.load(f)
    return _key_cache
```

### Cleanup

- Default: Delete all `bench-*` namespaces after the run
- `--no-cleanup` flag to preserve data for inspection
- Cleanup failures are logged but don't fail the benchmark
- Also clean up the temp directory for the key registry

## Benchmark Tiers

| Tier | CLI flag | Entries | Namespaces | Keys/Namespace | Purpose |
|------|----------|---------|------------|----------------|---------|
| Small | `small` | 1,000 | 10 | 100 | Fast iteration, baseline latency |
| Large | `large` | 10,000 | 50 | 200 | Sustained throughput, scaling check |

### Research Insights: Tier Naming

**From pattern-recognition-specialist:** The brainstorm used "Small/Large/Very Large" for tiers but also "Large/Very Large" for payload sizes. This naming collision causes confusion.

**Resolution:** Use numeric tier names (`1k`, `10k`) internally in results JSON. CLI flags remain `small`/`large` for usability. The `xlarge` tier (100K entries) is deferred until fast iteration on `small` and `large` proves which questions need answering.

## Payload Sizes

Default: **1 KB** (medium). Other sizes available via `BENCH_PAYLOAD_SIZE` env var.

| Label | Size | Content |
|-------|------|---------|
| Tiny | ~10 B | `"enabled"` or short string |
| Medium | ~1 KB | Random bytes (base64-encoded) |
| Large | ~100 KB | Random bytes (base64-encoded) |
| Very Large | ~1 MB | Random bytes (base64-encoded) |

### Research Insights: Payload Generation

**Critical fix from kieran-python-reviewer:**
```python
# BAD: Creates 1M Python str objects (~50MB heap) for 1MB payload
''.join(random.choices(chars, k=1_000_000))

# GOOD: 1MB allocation, fast, correct size
import os, base64
base64.b64encode(os.urandom(size)).decode('ascii')[:size]
```

**From performance-oracle:** Pre-generate a pool of payloads at startup (one per payload size) and reuse them. Generating 1MB payloads per-request adds 5-15ms overhead that contaminates latency measurements.

### Research Insights: GC Pressure from Large Payloads

**From dotnet-benchmark-designer:** 1MB request bodies cause Large Object Heap (LOH) allocations in .NET, triggering Gen2 GC pauses that dominate p99/max latency. When benchmarking with large payloads, monitor server-side GC with `dotnet-counters`:

```bash
dotnet-counters monitor --process-id <PID> --counters System.Runtime \
  --refresh-interval 1
```

Key metrics: `gc-heap-size-bytes`, `gen-2-gc-count`, `time-in-gc`.

## Test Scenarios (MVP)

### Scenario 1: Write (1 user — Sequential Baseline)

- **Locustfile**: `write.py`
- **Users**: 1
- **Duration**: 30s (after 60s warm-up)
- **Target**: Leader node directly (discovered via `/health/ready`)
- **Operation**: `PUT /api/v1/namespaces/{ns}/config/{key}` with 1KB payload
- **Measures**: Raw per-request write latency without concurrency

### Scenario 2: Write (50 users — Concurrent Throughput)

- **Locustfile**: `write.py`
- **Users**: 50
- **Duration**: 30s (after warm-up)
- **Target**: Any node (follows 307 redirects to leader)
- **Operation**: `PUT` new keys into pre-created namespaces
- **Measures**: Throughput ceiling, latency degradation under load

### Scenario 3: Read (1 user — Sequential Baseline)

- **Locustfile**: `read.py`
- **Users**: 1
- **Prerequisite**: Dataset pre-seeded by wrapper
- **Target**: Single follower node
- **Operation**: `GET /api/v1/namespaces/{ns}/config/{key}` with random key from registry
- **Measures**: Raw read latency (includes read barrier overhead)

### Scenario 4: Read (50 users — Concurrent Throughput)

- **Locustfile**: `read.py`
- **Users**: 50
- **Prerequisite**: Dataset pre-seeded
- **Target**: Entry point host (wrapper passes `--host`)
- **Operation**: Random `GET` from key registry
- **Measures**: Read throughput

### Scenario 5: Mixed Read/Write (50 users, 80/20)

- **Locustfile**: `mixed.py`
- **Users**: 50
- **Duration**: 60s
- **Mix**: 80% reads / 20% writes (`@task(8)` / `@task(2)`)
- **Prerequisite**: Dataset pre-seeded
- **Measures**: Realistic operational profile

### Scenario 6: Raft Consensus Overhead (Computed)

Not a separate Locust scenario. The wrapper computes from scenario 1 & 3 CSV results:
```
consensus_overhead_ms = write_p50 - read_p50
```

> **Note**: This understates true Raft overhead because reads also pay read barrier cost. For accurate isolation, compare against single-node baseline (v2).

### Deferred Scenarios (v2)

| Scenario | Reason to Defer |
|----------|----------------|
| 7: Audit Trail Query | Secondary read path; add when audit perf is a concern |
| 8: Payload Size Sweep | Default 1KB covers common case; manual `BENCH_PAYLOAD_SIZE` available |
| 9: Snapshot/Restore | Requires process management; measure manually first |
| 10: Single-Node Baseline | Requires `appsettings.single.json` and comparison logic |

### Research Insights: Deferred Architecture Notes

**Scenario 9 (from architecture-strategist + security-sentinel):** Do NOT implement process management in Python. Instead, extend `cluster.sh` with per-node commands:

```bash
cluster.sh stop-node node3
cluster.sh wipe-node node3
cluster.sh start-node node3
```

The Python wrapper shells out via `subprocess.run()`. Kill by PID (not `pkill -f`), use SIGTERM (not SIGKILL, to avoid LiteDB lock file corruption), require `--allow-destructive` flag.

**Scenario 10 (from architecture-strategist):** The application cannot run without Raft. A "single-node" mode requires a single-member cluster:

```json
{
  "publicEndPoint": "http://127.0.0.1:6100/",
  "coldStart": true,
  "members": ["http://127.0.0.1:6100/"],
  "ReadBarrier": { "Enabled": false }
}
```

Use `coldStart: true` since there is no cluster to join. Disable read barrier (no quorum to verify). Raft log append still occurs but no network replication — the delta vs cluster represents pure replication cost.

## Acceptance Criteria

### Core (4 files)
- [ ] `benchmarks/requirements.txt` with `locust>=2.20`, `requests`
- [ ] `benchmarks/run_benchmark.py` — wrapper (~150 lines): CLI parsing, seed, run `locust --headless --csv`, cleanup
- [ ] `benchmarks/locustfiles/write.py` — write scenario with `catch_response=True`
- [ ] `benchmarks/locustfiles/read.py` — read scenario with file-based key registry
- [ ] `benchmarks/locustfiles/mixed.py` — 80/20 read/write

### Wrapper Features
- [ ] `--tier small|large` flag (default: `small`)
- [ ] `--host` flag (cluster entry point, auto-discovers leader)
- [ ] `--output-dir` flag (default: `benchmarks/results/`)
- [ ] `--no-cleanup` flag to preserve test data
- [ ] `CONFMAN_BENCH_API_KEY` env var (fallback: `--api-key`, default: `confman_dev_abc123`)
- [ ] Data seeding before scenarios (create namespaces + config entries, progress logging)
- [ ] File-based key registry in `tempfile.mkdtemp()` directory
- [ ] Data cleanup after scenarios (delete `bench-*` namespaces + temp files)
- [ ] Console progress output (scenario name, throughput, latency summary)
- [ ] Startup validation: verify API key works before running scenarios

### Quality
- [ ] All locustfiles use `catch_response=True` for explicit success/failure
- [ ] Payloads generated with `os.urandom` + base64 (not `random.choices`)
- [ ] Key registry loaded once per process (module-level cache)
- [ ] `benchmarks/results/` added to `.gitignore`
- [ ] Type hints on all public functions
- [ ] `benchmarks/README.md` — installation, usage, example output

## Implementation

### Phase 1: Foundation (wrapper + generators)

#### `benchmarks/requirements.txt`

```
locust>=2.20
requests>=2.31
```

#### `benchmarks/run_benchmark.py`

```python
#!/usr/bin/env python3
"""Confman Cluster Benchmark Suite."""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
import time
import uuid
from pathlib import Path
from typing import Any

import requests

# --- Constants ---

TIERS: dict[str, dict[str, int]] = {
    "small":  {"namespaces": 10, "keys_per_ns": 100},
    "large":  {"namespaces": 50, "keys_per_ns": 200},
}

DEFAULT_PAYLOAD_SIZE = 1024  # 1 KB
LOCUSTFILES_DIR = Path(__file__).parent / "locustfiles"


# --- Data Generation ---

def namespace_name(tier: str, index: int) -> str:
    return f"bench-{tier}-{index:03d}"


def generate_key() -> str:
    return f"key-{uuid.uuid4().hex[:12]}"


def generate_value(size: int) -> str:
    """Generate a random string of approximately `size` bytes."""
    import base64
    return base64.b64encode(os.urandom(size)).decode("ascii")[:size]


# --- Cluster Interaction ---

def discover_leader(host: str, api_key: str) -> str:
    """Find the leader node starting from any cluster entry point."""
    # Get node list from the entry point
    nodes = [host]
    # Try all known ports if entry point fails
    for port in [6100, 6200, 6300]:
        url = f"http://127.0.0.1:{port}"
        if url != host:
            nodes.append(url)

    for node in nodes:
        try:
            r = requests.get(f"{node}/health/ready", timeout=2)
            data = r.json()
            if data.get("cluster", {}).get("role") == "leader":
                return node
        except (requests.RequestException, ValueError):
            continue
    raise RuntimeError("Could not discover leader. Is the cluster running?")


def verify_api_key(host: str, api_key: str) -> None:
    """Verify the API key works before running benchmarks."""
    r = requests.get(
        f"{host}/api/v1/namespaces",
        headers={"X-Api-Key": api_key},
        timeout=5,
    )
    if r.status_code == 401:
        raise RuntimeError(
            f"API key rejected (401). Check CONFMAN_BENCH_API_KEY or --api-key. "
            f"Expected key from appsettings.json (e.g., confman_dev_abc123)."
        )
    r.raise_for_status()


# --- Seeding ---

def seed_data(
    leader: str, api_key: str, tier: str, payload_size: int,
) -> list[dict[str, str]]:
    """Create namespaces and config entries. Returns key registry."""
    cfg = TIERS[tier]
    headers = {"X-Api-Key": api_key}
    registry: list[dict[str, str]] = []

    # Pre-generate a payload to reuse (avoid per-request generation overhead)
    payload_value = generate_value(payload_size)

    # Create namespaces
    print(f"  Creating {cfg['namespaces']} namespaces...")
    for i in range(cfg["namespaces"]):
        ns = namespace_name(tier, i)
        requests.put(
            f"{leader}/api/v1/namespaces/{ns}",
            headers=headers,
            timeout=10,
        )

    # Create config entries with progress
    total_entries = cfg["namespaces"] * cfg["keys_per_ns"]
    print(f"  Creating {total_entries} config entries...")
    count = 0
    for ns_idx in range(cfg["namespaces"]):
        ns = namespace_name(tier, ns_idx)
        for _ in range(cfg["keys_per_ns"]):
            key = generate_key()
            requests.put(
                f"{leader}/api/v1/namespaces/{ns}/config/{key}",
                json={"value": payload_value},
                headers=headers,
                timeout=10,
            )
            registry.append({"namespace": ns, "key": key})
            count += 1
            if count % 1000 == 0:
                print(f"    {count}/{total_entries} entries seeded...")

    print(f"  Seeding complete: {count} entries across {cfg['namespaces']} namespaces.")
    return registry


def write_key_registry(registry: list[dict[str, str]], tier: str) -> str:
    """Write key registry to a temp file. Returns file path."""
    tmpdir = tempfile.mkdtemp(prefix="confman-bench-")
    path = os.path.join(tmpdir, f"keys-{tier}.json")
    with open(path, "w") as f:
        json.dump(registry, f)
    return path


# --- Scenario Runner ---

def run_scenario(
    locustfile: str,
    host: str,
    users: int,
    duration_s: int,
    csv_prefix: str,
    env_vars: dict[str, str] | None = None,
) -> None:
    """Run a Locust scenario via CLI and produce CSV output."""
    env = os.environ.copy()
    if env_vars:
        env.update(env_vars)

    cmd = [
        "locust", "--headless",
        "--locustfile", str(LOCUSTFILES_DIR / locustfile),
        "--host", host,
        "--users", str(users),
        "--spawn-rate", str(min(users, 10)),
        "--run-time", f"{duration_s}s",
        "--csv", csv_prefix,
    ]
    subprocess.run(cmd, check=True, env=env)


# --- Cleanup ---

def cleanup_data(leader: str, api_key: str, tier: str) -> None:
    """Delete all bench-* namespaces for the tier."""
    cfg = TIERS[tier]
    headers = {"X-Api-Key": api_key}
    for i in range(cfg["namespaces"]):
        ns = namespace_name(tier, i)
        try:
            requests.delete(
                f"{leader}/api/v1/namespaces/{ns}",
                headers=headers,
                timeout=10,
            )
        except requests.RequestException:
            print(f"  Warning: failed to delete namespace {ns}")


# --- Main ---

def main() -> None:
    p = argparse.ArgumentParser(description="Confman Cluster Benchmark Suite")
    p.add_argument("--tier", choices=["small", "large"], default="small")
    p.add_argument("--host", default="http://127.0.0.1:6100",
                    help="Cluster entry point (auto-discovers leader)")
    p.add_argument("--api-key",
                    default=os.environ.get("CONFMAN_BENCH_API_KEY", "confman_dev_abc123"))
    p.add_argument("--output-dir", default="benchmarks/results")
    p.add_argument("--no-cleanup", action="store_true")
    args = p.parse_args()

    print("Confman Cluster Benchmark Suite")
    print("=" * 40)

    # Discover leader and verify connectivity
    print(f"\nDiscovering leader from {args.host}...")
    leader = discover_leader(args.host, args.api_key)
    print(f"Leader: {leader}")

    verify_api_key(leader, args.api_key)
    print("API key verified.")

    # Seed data
    print(f"\nSeeding data (tier: {args.tier})...")
    registry = seed_data(leader, args.api_key, args.tier, DEFAULT_PAYLOAD_SIZE)
    registry_path = write_key_registry(registry, args.tier)

    # Build env vars for locustfiles
    namespaces = list({e["namespace"] for e in registry})
    env_vars = {
        "BENCH_API_KEY": args.api_key,
        "BENCH_KEY_REGISTRY_FILE": registry_path,
        "BENCH_NAMESPACES": json.dumps(namespaces),
        "BENCH_PAYLOAD_SIZE": str(DEFAULT_PAYLOAD_SIZE),
    }

    # Output prefix
    prefix = f"{args.output_dir}/{args.tier}"
    Path(args.output_dir).mkdir(parents=True, exist_ok=True)

    # Run scenarios
    scenarios = [
        ("write.py", leader, 1,  30, f"{prefix}-write-1u"),
        ("write.py", leader, 50, 30, f"{prefix}-write-50u"),
        ("read.py",  args.host, 1,  30, f"{prefix}-read-1u"),
        ("read.py",  args.host, 50, 30, f"{prefix}-read-50u"),
        ("mixed.py", args.host, 50, 60, f"{prefix}-mixed-50u"),
    ]

    for i, (locustfile, host, users, duration, csv_pfx) in enumerate(scenarios, 1):
        name = f"{locustfile.replace('.py', '')} ({users}u, {duration}s)"
        print(f"\n[{i}/{len(scenarios)}] {name}")
        run_scenario(locustfile, host, users, duration, csv_pfx, env_vars)

    # Cleanup
    if not args.no_cleanup:
        print("\nCleaning up...")
        cleanup_data(leader, args.api_key, args.tier)
        # Clean up temp registry
        import shutil
        shutil.rmtree(os.path.dirname(registry_path), ignore_errors=True)

    print(f"\nResults written to: {args.output_dir}/")
    print("Done.")


if __name__ == "__main__":
    main()
```

### Phase 2: Locustfiles

#### `benchmarks/locustfiles/write.py`

```python
"""Write benchmark — PUT config entries to leader."""
from __future__ import annotations

import base64
import json
import os
import random
from itertools import count

from locust import HttpUser, task, constant

_counter = count()


class ConfigWriter(HttpUser):
    wait_time = constant(0)

    def on_start(self) -> None:
        self.api_key: str = os.environ["BENCH_API_KEY"]
        self.namespaces: list[str] = json.loads(os.environ["BENCH_NAMESPACES"])
        self.payload_size: int = int(os.environ.get("BENCH_PAYLOAD_SIZE", "1024"))
        # Pre-generate a reusable payload
        self._payload: str = base64.b64encode(
            os.urandom(self.payload_size)
        ).decode("ascii")[:self.payload_size]
        self._user_id: int = next(_counter)

    @task
    def write_config(self) -> None:
        ns = self.namespaces[self._user_id % len(self.namespaces)]
        key = f"key-{os.urandom(6).hex()}"
        with self.client.put(
            f"/api/v1/namespaces/{ns}/config/{key}",
            json={"value": self._payload},
            headers={"X-Api-Key": self.api_key},
            name="/config/[key] PUT",
            catch_response=True,
        ) as response:
            if response.status_code >= 400:
                response.failure(f"HTTP {response.status_code}")
```

#### `benchmarks/locustfiles/read.py`

```python
"""Read benchmark — GET config entries from cluster."""
from __future__ import annotations

import json
import os
import random

from locust import HttpUser, task, constant

# Module-level cache: loaded once per process, shared across greenlets
_key_cache: list[dict[str, str]] | None = None


def load_keys() -> list[dict[str, str]]:
    global _key_cache
    if _key_cache is None:
        path = os.environ["BENCH_KEY_REGISTRY_FILE"]
        with open(path) as f:
            _key_cache = json.load(f)
    return _key_cache


class ConfigReader(HttpUser):
    wait_time = constant(0)

    def on_start(self) -> None:
        self.api_key: str = os.environ["BENCH_API_KEY"]
        self.keys: list[dict[str, str]] = load_keys()

    @task
    def read_config(self) -> None:
        entry = random.choice(self.keys)
        with self.client.get(
            f"/api/v1/namespaces/{entry['namespace']}/config/{entry['key']}",
            headers={"X-Api-Key": self.api_key},
            name="/config/[key] GET",
            catch_response=True,
        ) as response:
            if response.status_code >= 400:
                response.failure(f"HTTP {response.status_code}")
```

#### `benchmarks/locustfiles/mixed.py`

```python
"""Mixed 80/20 read/write benchmark."""
from __future__ import annotations

import base64
import json
import os
import random

from locust import HttpUser, task, constant

_key_cache: list[dict[str, str]] | None = None


def load_keys() -> list[dict[str, str]]:
    global _key_cache
    if _key_cache is None:
        path = os.environ["BENCH_KEY_REGISTRY_FILE"]
        with open(path) as f:
            _key_cache = json.load(f)
    return _key_cache


class MixedUser(HttpUser):
    wait_time = constant(0)

    def on_start(self) -> None:
        self.api_key: str = os.environ["BENCH_API_KEY"]
        self.keys: list[dict[str, str]] = load_keys()
        self.namespaces: list[str] = json.loads(os.environ["BENCH_NAMESPACES"])
        self.payload_size: int = int(os.environ.get("BENCH_PAYLOAD_SIZE", "1024"))
        self._payload: str = base64.b64encode(
            os.urandom(self.payload_size)
        ).decode("ascii")[:self.payload_size]

    @task(8)
    def read_config(self) -> None:
        entry = random.choice(self.keys)
        with self.client.get(
            f"/api/v1/namespaces/{entry['namespace']}/config/{entry['key']}",
            headers={"X-Api-Key": self.api_key},
            name="/config/[key] GET",
            catch_response=True,
        ) as response:
            if response.status_code >= 400:
                response.failure(f"HTTP {response.status_code}")

    @task(2)
    def write_config(self) -> None:
        ns = random.choice(self.namespaces)
        key = f"key-{os.urandom(6).hex()}"
        with self.client.put(
            f"/api/v1/namespaces/{ns}/config/{key}",
            json={"value": self._payload},
            headers={"X-Api-Key": self.api_key},
            name="/config/[key] PUT",
            catch_response=True,
        ) as response:
            if response.status_code >= 400:
                response.failure(f"HTTP {response.status_code}")
```

### Phase 3: README + Polish

#### `benchmarks/README.md`

- Installation: `pip install -r requirements.txt`
- Prerequisites: Running cluster (`cluster.sh start`)
- Quick start: `python run_benchmark.py --tier small --host http://127.0.0.1:6100`
- CLI reference
- Example CSV output
- API key configuration
- Adding new scenarios

## Project Structure

```
benchmarks/
├── README.md                    # Installation and usage guide
├── requirements.txt             # locust, requests
├── run_benchmark.py             # Wrapper: seed, run locust CLI, cleanup (~150 lines)
├── locustfiles/
│   ├── write.py                 # Write scenarios (1u and 50u, same file)
│   ├── read.py                  # Read scenarios (1u and 50u, same file)
│   └── mixed.py                 # 80/20 read/write mix
└── results/                     # Output directory (CSV files)
    └── .gitkeep
```

**Comparison with original plan:**

| Metric | Original | Simplified |
|--------|----------|------------|
| Python files | 12 | 4 |
| Locustfiles | 7 | 3 |
| Scenarios (MVP) | 10 | 5 + computed overhead |
| Tiers | 3 | 2 |
| `lib/` modules | 3 | 0 (inlined into wrapper) |
| Output format | Custom JSON | Locust CSV (native) |
| External deps | locust + gevent | locust + requests |

## Results Output

Locust `--csv` produces three files per scenario:

- `{prefix}_stats.csv` — per-request-type summary: Name, # Requests, # Failures, Median, Average, Min, Max, p50, p66, p75, p80, p90, p95, p98, p99, p999, p9999, RPS
- `{prefix}_stats_history.csv` — time series with RPS and latency per second
- `{prefix}_failures.csv` — error details

Example directory after a `small` tier run:
```
results/
├── small-write-1u_stats.csv
├── small-write-1u_stats_history.csv
├── small-write-1u_failures.csv
├── small-write-50u_stats.csv
├── small-read-1u_stats.csv
├── small-read-50u_stats.csv
├── small-mixed-50u_stats.csv
└── ...
```

### Research Insights: Results Metadata

**From architecture-strategist:** For cross-run comparison (v2), add a summary JSON with system metadata:

```json
{
  "run_id": "uuid",
  "git_sha": "06a7e81...",
  "system": {
    "os": "Darwin 25.3.0",
    "cpu": "Apple M2",
    "cores": 8,
    "memory_gb": 16,
    "dotnet_version": "10.0.x",
    "locust_version": "2.20.x"
  }
}
```

For MVP, the CSV files are sufficient — anyone can open them in a spreadsheet or parse with `pandas`.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Locust invocation | CLI (`--headless --csv`) | No gevent dependency in wrapper; CSV output gives all stats for free |
| HTTP client | `HttpUser` (not `FastHttpUser`) | `requests` lib handles 307 redirects natively |
| Locustfiles | 3 files (write, read, mixed) | User count is a wrapper parameter, not a locustfile concern |
| Data seeding | Outside Locust (wrapper) | Separates setup cost from measurement |
| Key registry | JSON file via `tempfile.mkdtemp()` | Avoids env var size limits, secure temp directory |
| Payload generation | `os.urandom` + base64 | Avoids 50x memory amplification of `random.choices` |
| Error handling | `catch_response=True` | Without it, 5xx counted as successes during leader election |
| API keys | Two keys (admin + reader) | Least privilege, catches auth regressions |
| Default API key | `confman_dev_abc123` | Matches actual `appsettings.json` (not the non-existent `confman_dev_key`) |
| Wait time | `constant(0)` | Cleaner than `between(0, 0)` for back-to-back requests |
| Scenario 6 (Raft overhead) | Computed from CSV | No extra load generation needed |
| Concurrency levels | 1u and 50u per scenario | Captures both baseline and load; add ramped stages in v2 |
| Run duration | 30s per scenario | Enough for stats to stabilize after .NET JIT warm-up |

## Research Insights: .NET Server-Side Considerations

**From dotnet-benchmark-designer:**
- **LiteDB write lock**: LiteDB uses a process-wide single writer lock. Under concurrent writes, the benchmark may measure LiteDB lock contention, not Raft consensus. For v2, consider adding an in-memory state machine option to isolate Raft from storage.
- **Snapshot triggering**: Snapshots are tied to log compaction, not node restart. For scenario 9 (v2), trigger via `ForceCompactionAsync` API, not by killing and restarting a node.
- **Network simulation**: For realistic latency testing (v2), use `tc netem` to add 0.5-2ms delay simulating same-AZ and cross-AZ deployments.

**From dotnet-benchmark-designer — statistical rigor (v2):**
- Run each scenario 7+ times
- Report mean ± stddev with 95% CI
- Discard outlier runs (>2 stddev from mean)
- Single-run percentiles are unreliable for regression detection

## References & Research

### Internal References

- API controllers: `src/Confman.Api/Controllers/ConfigController.cs`, `NamespacesController.cs`, `AuditController.cs`
- Auth handler: `src/Confman.Api/Auth/ApiKeyAuthenticationHandler.cs`
- Auth policies: `src/Confman.Api/Auth/NamespaceAuthorizationHandler.cs:85-103`
- Read barrier middleware: `src/Confman.Api/Middleware/ReadBarrierMiddleware.cs`
- Health endpoints: `src/Confman.Api/Program.cs:181-205`
- Leader forwarding: Each controller's PUT/DELETE methods check `_raft.IsLeader` → 307
- Cluster script: `.claude/skills/run-cluster/scripts/cluster.sh`
- API keys: `src/Confman.Api/appsettings.json:28` (`confman_dev_abc123`), `:33` (`confman_dev_reader`)
- Node configs: `src/Confman.Api/appsettings.node{1,2,3}.json` (ports 6100, 6200, 6300)

### Learnings Applied

- `docs/solutions/2026-02-02-role-name-mismatch-prevents-read-access.md` — Role names must match between config and policies; using separate admin/reader keys catches this
- `docs/solutions/2026-02-01-dotnext-coldstart-leader-tracking-loss.md` — Never use `coldStart: true` with pre-configured members (relevant for single-node baseline mode)
- `docs/solutions/2026-01-24-dotnext-raft-cluster-members-configuration.md` — `UseInMemoryConfigurationStorage` required for static membership

### External References

- [Locust Documentation](https://docs.locust.io/en/stable/)
- [Locust CSV Output](https://docs.locust.io/en/stable/retrieving-stats.html)
- [Locust catch_response](https://docs.locust.io/en/stable/writing-a-locustfile.html#validating-responses)

### Brainstorm

- `docs/brainstorms/2026-02-02-cluster-benchmark-brainstorm.md`
