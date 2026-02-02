#!/usr/bin/env python3
"""Confman Cluster Benchmark Suite.

Measures throughput and latency for read/write operations against
the 3-node Confman Raft cluster using Locust.

Usage:
    python run_benchmark.py --tier small --host http://127.0.0.1:6100
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

import requests as req

# --- Constants ---

TIERS: dict[str, dict[str, int]] = {
    "small": {"namespaces": 5, "keys_per_ns": 20},
    "large": {"namespaces": 20, "keys_per_ns": 50},
}

DEFAULT_PAYLOAD_SIZE = 1024  # 1 KB
CLUSTER_PORTS = [6100, 6200, 6300]
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


def discover_leader(host: str) -> str:
    """Find the leader node starting from any cluster entry point."""
    nodes = [host]
    for port in CLUSTER_PORTS:
        url = f"http://127.0.0.1:{port}"
        if url not in nodes:
            nodes.append(url)

    for node in nodes:
        try:
            r = req.get(f"{node}/health/ready", timeout=2)
            data = r.json()
            if data.get("cluster", {}).get("role") == "leader":
                return node
        except (req.RequestException, ValueError):
            continue

    raise RuntimeError(
        "Could not discover leader. Is the cluster running? "
        "Try: cluster.sh start"
    )


def wait_for_healthy_cluster(host: str, timeout: int = 60) -> bool:
    """Wait until the cluster reports ready status. Returns True if healthy."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            r = req.get(f"{host}/health/ready", timeout=2)
            data = r.json()
            if data.get("status") == "ready":
                return True
        except (req.RequestException, ValueError):
            pass
        time.sleep(2)
    return False


def verify_api_key(host: str, api_key: str) -> None:
    """Verify the API key works before running benchmarks."""
    try:
        r = req.get(
            f"{host}/api/v1/namespaces",
            headers={"X-Api-Key": api_key},
            timeout=5,
        )
    except req.RequestException as e:
        raise RuntimeError(f"Cannot reach {host}: {e}") from e

    if r.status_code == 401:
        raise RuntimeError(
            f"API key rejected (401). Check CONFMAN_BENCH_API_KEY or --api-key. "
            f"The default dev admin key is 'confman_dev_abc123'."
        )
    r.raise_for_status()


# --- Seeding ---


def seed_data(
    leader: str,
    api_key: str,
    tier: str,
    payload_size: int,
) -> list[dict[str, str]]:
    """Create namespaces and config entries. Returns key registry."""
    cfg = TIERS[tier]
    headers = {"X-Api-Key": api_key}
    registry: list[dict[str, str]] = []

    # Pre-generate a payload to reuse (avoids per-request generation overhead)
    payload_value = generate_value(payload_size)

    # Create namespaces
    ns_count = cfg["namespaces"]
    print(f"  Creating {ns_count} namespaces...")
    for i in range(ns_count):
        ns = namespace_name(tier, i)
        try:
            r = req.put(
                f"{leader}/api/v1/namespaces/{ns}",
                json={},
                headers=headers,
                timeout=10,
            )
            r.raise_for_status()
        except (req.RequestException, Exception) as e:
            print(f"    Warning: failed to create namespace {ns}: {e}")

    # Create config entries with progress
    total_entries = ns_count * cfg["keys_per_ns"]
    print(f"  Creating {total_entries} config entries...")
    count = 0
    for ns_idx in range(ns_count):
        ns = namespace_name(tier, ns_idx)
        for _ in range(cfg["keys_per_ns"]):
            key = generate_key()
            try:
                req.put(
                    f"{leader}/api/v1/namespaces/{ns}/config/{key}",
                    json={"value": payload_value},
                    headers=headers,
                    timeout=10,
                )
            except req.RequestException:
                pass  # Best-effort seeding; Locust will report errors on missing keys
            registry.append({"namespace": ns, "key": key})
            count += 1
            # Throttle seeding to avoid overwhelming Raft consensus.
            # Each write requires propose → replicate → commit → apply,
            # and generates an audit event (doubling Raft log entries).
            time.sleep(0.1)
            if count % 50 == 0:
                print(f"    {count}/{total_entries} entries seeded...")

    print(f"  Seeding complete: {count} entries across {ns_count} namespaces.")
    return registry


def write_key_registry(
    registry: list[dict[str, str]], tier: str
) -> tuple[str, str]:
    """Write key registry to a temp file. Returns (file_path, temp_dir)."""
    tmpdir = tempfile.mkdtemp(prefix="confman-bench-")
    path = os.path.join(tmpdir, f"keys-{tier}.json")
    with open(path, "w") as f:
        json.dump(registry, f)
    return path, tmpdir


# --- Scenario Runner ---


def run_scenario(
    locustfile: str,
    host: str,
    users: int,
    duration_s: int,
    csv_prefix: str,
    env_vars: dict[str, str],
) -> bool:
    """Run a Locust scenario via CLI. Returns True if successful."""
    env = os.environ.copy()
    env.update(env_vars)

    cmd = [
        sys.executable, "-m", "locust",
        "--headless",
        "--locustfile", str(LOCUSTFILES_DIR / locustfile),
        "--host", host,
        "--users", str(users),
        "--spawn-rate", str(min(users, 10)),
        "--run-time", f"{duration_s}s",
        "--csv", csv_prefix,
    ]

    result = subprocess.run(cmd, env=env)
    return result.returncode == 0


# --- Cleanup ---


def cleanup_data(leader: str, api_key: str, tier: str) -> None:
    """Delete all bench-* namespaces for the tier."""
    cfg = TIERS[tier]
    headers = {"X-Api-Key": api_key}
    print(f"  Deleting {cfg['namespaces']} bench namespaces...")
    for i in range(cfg["namespaces"]):
        ns = namespace_name(tier, i)
        try:
            req.delete(
                f"{leader}/api/v1/namespaces/{ns}",
                headers=headers,
                timeout=10,
            )
        except req.RequestException:
            print(f"    Warning: failed to delete namespace {ns}")


# --- Main ---


def main() -> None:
    p = argparse.ArgumentParser(
        description="Confman Cluster Benchmark Suite",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Examples:\n"
            "  python run_benchmark.py --tier small\n"
            "  python run_benchmark.py --tier large --no-cleanup\n"
            "  CONFMAN_BENCH_API_KEY=mykey python run_benchmark.py\n"
        ),
    )
    p.add_argument(
        "--tier",
        choices=["small", "large"],
        default="small",
        help="Dataset tier (default: small)",
    )
    p.add_argument(
        "--host",
        default="http://127.0.0.1:6100",
        help="Cluster entry point — auto-discovers leader (default: %(default)s)",
    )
    p.add_argument(
        "--api-key",
        default=os.environ.get("CONFMAN_BENCH_API_KEY", "confman_dev_abc123"),
        help="API key for authentication (default: CONFMAN_BENCH_API_KEY env var or confman_dev_abc123)",
    )
    p.add_argument(
        "--output-dir",
        default=str(Path(__file__).parent / "results"),
        help="Directory for CSV output (default: benchmarks/results/)",
    )
    p.add_argument(
        "--no-cleanup",
        action="store_true",
        help="Preserve test data after run (don't delete bench-* namespaces)",
    )
    args = p.parse_args()

    print("Confman Cluster Benchmark Suite")
    print("=" * 40)

    # 1. Discover leader
    print(f"\nDiscovering leader from {args.host}...")
    leader = discover_leader(args.host)
    print(f"  Leader: {leader}")

    # 2. Verify API key
    verify_api_key(leader, args.api_key)
    print("  API key verified.")

    # 3. Seed data
    tier_cfg = TIERS[args.tier]
    total = tier_cfg["namespaces"] * tier_cfg["keys_per_ns"]
    print(f"\nSeeding data (tier={args.tier}, {total} entries, payload={DEFAULT_PAYLOAD_SIZE}B)...")
    registry = seed_data(leader, args.api_key, args.tier, DEFAULT_PAYLOAD_SIZE)
    registry_path, tmpdir = write_key_registry(registry, args.tier)
    print(f"  Key registry: {registry_path}")

    # 3b. Wait for cluster to stabilize after seeding
    print("  Waiting for cluster to stabilize...")
    if not wait_for_healthy_cluster(leader):
        # Re-discover leader (may have changed during seeding)
        print("  Cluster not ready on original leader, re-discovering...")
        try:
            leader = discover_leader(args.host)
            print(f"  New leader: {leader}")
        except RuntimeError:
            print("  WARNING: Cluster may be unhealthy. Proceeding anyway.")
    else:
        print("  Cluster healthy.")

    # 4. Prepare env vars for locustfiles
    namespaces = sorted({e["namespace"] for e in registry})
    env_vars = {
        "BENCH_API_KEY": args.api_key,
        "BENCH_KEY_REGISTRY_FILE": registry_path,
        "BENCH_NAMESPACES": json.dumps(namespaces),
        "BENCH_PAYLOAD_SIZE": str(DEFAULT_PAYLOAD_SIZE),
    }

    # 5. Prepare output directory
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    prefix = str(output_dir / args.tier)

    # 6. Run scenarios
    scenarios = [
        ("write.py", leader, 1, 15, f"{prefix}-write-1u"),
        ("write.py", leader, 10, 15, f"{prefix}-write-10u"),
        ("read.py", args.host, 1, 15, f"{prefix}-read-1u"),
        ("read.py", args.host, 10, 15, f"{prefix}-read-10u"),
        ("mixed.py", args.host, 10, 30, f"{prefix}-mixed-10u"),
    ]

    print(f"\nRunning {len(scenarios)} scenarios...")
    failed = []
    for i, (locustfile, host, users, duration, csv_pfx) in enumerate(scenarios, 1):
        name = f"{Path(locustfile).stem} ({users}u, {duration}s)"
        print(f"\n{'='*40}")
        print(f"[{i}/{len(scenarios)}] {name}")
        print(f"{'='*40}")
        # Ensure cluster is healthy before each scenario
        if not wait_for_healthy_cluster(leader, timeout=15):
            print(f"  SKIP: cluster unhealthy, skipping {name}")
            failed.append(f"{name} (skipped)")
            continue
        ok = run_scenario(locustfile, host, users, duration, csv_pfx, env_vars)
        if not ok:
            failed.append(name)
            print(f"  WARNING: {name} exited with errors")

    # 7. Cleanup
    if not args.no_cleanup:
        print(f"\nCleaning up...")
        cleanup_data(leader, args.api_key, args.tier)
        shutil.rmtree(tmpdir, ignore_errors=True)
        print("  Done.")
    else:
        print(f"\n--no-cleanup: keeping test data and registry at {registry_path}")

    # 8. Summary
    print(f"\n{'='*40}")
    print("Summary")
    print(f"{'='*40}")
    print(f"  Tier: {args.tier}")
    print(f"  Scenarios run: {len(scenarios)}")
    if failed:
        print(f"  Failed: {len(failed)} — {', '.join(failed)}")
    else:
        print(f"  All scenarios completed successfully.")
    print(f"  Results: {args.output_dir}/")
    print(f"\nCSV files can be opened in any spreadsheet or parsed with pandas.")


if __name__ == "__main__":
    main()
