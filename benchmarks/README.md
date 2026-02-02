# Confman Cluster Benchmark Suite

Measures throughput and latency for read/write operations against a 3-node Confman Raft cluster using [Locust](https://locust.io/).

## Prerequisites

- Python 3.10+
- Running 3-node Confman cluster (use `cluster.sh start`)
- pip

## Setup

```bash
cd benchmarks
pip install -r requirements.txt
```

## Usage

```bash
# Run small tier (5 namespaces, 100 entries)
python run_benchmark.py --tier small

# Run large tier (20 namespaces, 1000 entries)
python run_benchmark.py --tier large

# Custom entry point and API key
python run_benchmark.py --tier small --host http://127.0.0.1:6200

# Keep test data after run
python run_benchmark.py --tier small --no-cleanup
```

The wrapper auto-discovers the cluster leader via `/health/ready` and seeds test data before running benchmarks.

## Scenarios

| Scenario | Users | Duration | Target | Description |
|----------|-------|----------|--------|-------------|
| write-1u | 1 | 15s | Leader | Single-writer baseline |
| write-10u | 10 | 15s | Leader | Concurrent write throughput |
| read-1u | 1 | 15s | Any node | Single-reader baseline |
| read-10u | 10 | 15s | Any node | Concurrent read throughput |
| mixed-10u | 10 | 30s | Any node | 80/20 read/write mix |

## Tiers

| Tier | Namespaces | Keys/NS | Total Entries | Payload |
|------|------------|---------|---------------|---------|
| small | 5 | 20 | 100 | 1 KB |
| large | 20 | 50 | 1,000 | 1 KB |

## Known Limitations

The local 3-node cluster runs all nodes on a single machine, sharing CPU and I/O. Under sustained write load, Raft heartbeats may be missed, causing leader elections and temporary 503 errors. This is expected behavior for a single-machine deployment — in production, nodes run on separate machines with dedicated resources.

## Output

Results are written as CSV files to `benchmarks/results/`:

```
results/
  small-write-1u_stats.csv
  small-write-1u_stats_history.csv
  small-read-50u_stats.csv
  ...
```

CSV files can be opened in any spreadsheet application or parsed with pandas.

## Configuration

| Flag | Default | Description |
|------|---------|-------------|
| `--tier` | `small` | Dataset size tier |
| `--host` | `http://127.0.0.1:6100` | Cluster entry point |
| `--api-key` | `confman_dev_abc123` | API key (or `CONFMAN_BENCH_API_KEY` env var) |
| `--output-dir` | `benchmarks/results/` | CSV output directory |
| `--no-cleanup` | off | Keep test data after run |

## Architecture

```
benchmarks/
  run_benchmark.py          # Orchestration wrapper
  requirements.txt          # Python dependencies
  locustfiles/
    write.py                # PUT config entries to leader
    read.py                 # GET config entries from any node
    mixed.py                # 80/20 read/write mix
  results/                  # CSV output (gitignored)
```

The wrapper seeds data, writes a key registry to a temp file, then invokes Locust CLI (`--headless --csv`) for each scenario via subprocess. Locustfiles receive configuration through environment variables (`BENCH_API_KEY`, `BENCH_KEY_REGISTRY_FILE`, `BENCH_NAMESPACES`, `BENCH_PAYLOAD_SIZE`).
