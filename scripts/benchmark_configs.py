#!/usr/bin/env python3
"""
Benchmark script for Confman API.
Creates a namespace and then creates configurable number of configs at a specified rate.

Usage:
    python benchmark_configs.py --rate 10 --count 100 --namespace test-benchmark
    python benchmark_configs.py -r 20 -c 50 -n my-namespace
"""

import argparse
import requests
import time
import sys
from datetime import datetime
from typing import Optional

# ANSI color codes
class Colors:
    GREEN = '\033[92m'
    RED = '\033[91m'
    YELLOW = '\033[93m'
    RESET = '\033[0m'

# Default cluster nodes
DEFAULT_NODES = [
    "http://localhost:6100",
    "http://localhost:6200",
    "http://localhost:6300",
]


def get_headers(api_key: str) -> dict:
    """Get request headers with API key."""
    return {"X-API-Key": api_key, "Content-Type": "application/json"}


def discover_leader(nodes: list) -> Optional[str]:
    """
    Query health endpoints to discover the current Raft leader.
    Returns the leader URL or None if no leader found.
    """
    print("Discovering cluster leader...")

    for node in nodes:
        try:
            response = requests.get(f"{node}/health/ready", timeout=5)
            if response.status_code == 200:
                health = response.json()
                cluster = health.get("cluster", {})
                role = cluster.get("role")

                print(f"  {node}: {role}")

                if role == "leader":
                    return node
                elif cluster.get("leaderKnown") and cluster.get("leader"):
                    # This node knows who the leader is
                    leader_url = cluster["leader"].rstrip("/")
                    # Convert internal address (127.0.0.1) to localhost for consistency
                    leader_url = leader_url.replace("127.0.0.1", "localhost")
                    print(f"  → Leader reported as: {leader_url}")
                    return leader_url
        except requests.RequestException as e:
            print(f"  {node}: unreachable ({e})")

    return None


def create_namespace(base_url: str, namespace: str, api_key: str) -> bool:
    """Create a namespace in Confman."""
    url = f"{base_url}/api/v1/namespaces/{namespace}"
    payload = {
        "name": namespace,
        "description": f"Benchmark namespace created at {datetime.now().isoformat()}"
    }

    try:
        response = requests.put(
            url, json=payload, headers=get_headers(api_key),
            allow_redirects=True, timeout=10
        )
        if response.status_code in (200, 201, 307):
            print(f"✓ Created namespace: {namespace}")
            return True
        else:
            print(f"✗ Failed to create namespace: {response.status_code} - {response.text}")
            return False
    except requests.RequestException as e:
        print(f"✗ Error creating namespace: {e}")
        return False


def create_config(base_url: str, namespace: str, key: str, value: str, api_key: str) -> dict:
    """Create a config entry. Returns dict with request details."""
    path = f"/api/v1/namespaces/{namespace}/config/{key}"
    url = f"{base_url}{path}"
    payload = {"value": value}

    start = time.perf_counter()
    try:
        response = requests.put(
            url, json=payload, headers=get_headers(api_key),
            allow_redirects=True, timeout=10
        )
        latency_ms = (time.perf_counter() - start) * 1000

        return {
            "success": response.status_code in (200, 201, 307),
            "latency_ms": latency_ms,
            "status_code": response.status_code,
            "url": url,
            "path": path,
            "key": key,
            "value": value,
        }
    except requests.RequestException as e:
        latency_ms = (time.perf_counter() - start) * 1000
        return {
            "success": False,
            "latency_ms": latency_ms,
            "status_code": 0,
            "url": url,
            "path": path,
            "key": key,
            "value": value,
            "error": str(e),
        }


def run_benchmark(leader_url: str, namespace: str, count: int, rate: float, api_key: str):
    """Run the benchmark creating configs at the specified rate."""
    delay = 1.0 / rate  # Calculate delay between requests

    print(f"\n{'='*60}")
    print(f"Confman Benchmark")
    print(f"{'='*60}")
    print(f"Leader:      {leader_url}")
    print(f"Namespace:   {namespace}")
    print(f"Config count: {count}")
    print(f"Target rate: {rate} req/s")
    print(f"Delay:       {delay*1000:.2f} ms between requests")
    print(f"{'='*60}\n")

    # Create namespace first
    if not create_namespace(leader_url, namespace, api_key):
        print("Failed to create namespace, aborting benchmark")
        sys.exit(1)

    print(f"\nCreating {count} configs at {rate} req/s...\n")

    successes = 0
    failures = 0
    latencies = []

    start_time = time.perf_counter()

    for i in range(count):
        key = f"config-{i:05d}"
        value = f"value-{i}-{datetime.now().isoformat()}"

        result = create_config(leader_url, namespace, key, value, api_key)
        latencies.append(result["latency_ms"])

        if result["success"]:
            successes += 1
            status = f"{Colors.GREEN}✓{Colors.RESET}"
            http_status = f"{Colors.GREEN}HTTP {result['status_code']}{Colors.RESET}"
        else:
            failures += 1
            status = f"{Colors.RED}✗{Colors.RESET}"
            http_status = f"{Colors.RED}HTTP {result['status_code']}{Colors.RESET}"

        # Print each request with full details
        print(f"  [{i+1:4d}/{count}] {status} PUT {result['url']} | key={result['key']} | value={result['value'][:25]}... | {http_status} | {result['latency_ms']:.1f}ms")

        # Sleep to maintain rate (except for last iteration)
        if i < count - 1:
            time.sleep(delay)

    total_time = time.perf_counter() - start_time

    # Calculate statistics
    avg_latency = sum(latencies) / len(latencies)
    min_latency = min(latencies)
    max_latency = max(latencies)
    actual_rate = count / total_time

    # Sort for percentiles
    sorted_latencies = sorted(latencies)
    p50 = sorted_latencies[int(len(sorted_latencies) * 0.50)]
    p95 = sorted_latencies[int(len(sorted_latencies) * 0.95)]
    p99 = sorted_latencies[int(len(sorted_latencies) * 0.99)]

    print(f"\n{'='*60}")
    print(f"Results")
    print(f"{'='*60}")
    print(f"Total time:    {total_time:.2f}s")
    print(f"Successes:     {successes}")
    print(f"Failures:      {failures}")
    print(f"Target rate:   {rate:.1f} req/s")
    print(f"Actual rate:   {actual_rate:.1f} req/s")
    print(f"\nLatency (ms):")
    print(f"  Min:  {min_latency:.1f}")
    print(f"  Avg:  {avg_latency:.1f}")
    print(f"  Max:  {max_latency:.1f}")
    print(f"  P50:  {p50:.1f}")
    print(f"  P95:  {p95:.1f}")
    print(f"  P99:  {p99:.1f}")
    print(f"{'='*60}\n")


def main():
    parser = argparse.ArgumentParser(
        description="Benchmark Confman API by creating configs at a specified rate"
    )
    parser.add_argument(
        "-r", "--rate",
        type=float,
        default=10,
        help="Requests per second (default: 10)"
    )
    parser.add_argument(
        "-c", "--count",
        type=int,
        default=100,
        help="Number of configs to create (default: 100)"
    )
    parser.add_argument(
        "-n", "--namespace",
        type=str,
        default="benchmark",
        help="Namespace to use (default: benchmark)"
    )
    parser.add_argument(
        "--nodes",
        type=str,
        nargs="+",
        default=DEFAULT_NODES,
        help=f"Cluster node URLs (default: {' '.join(DEFAULT_NODES)})"
    )
    parser.add_argument(
        "-k", "--api-key",
        type=str,
        default="confman_dev_abc123",
        help="API key for authentication (default: confman_dev_abc123)"
    )

    args = parser.parse_args()

    if args.rate <= 0:
        print("Error: Rate must be positive")
        sys.exit(1)

    if args.count <= 0:
        print("Error: Count must be positive")
        sys.exit(1)

    # Discover leader from cluster
    leader_url = discover_leader(args.nodes)
    if not leader_url:
        print("\n✗ Could not discover cluster leader. Is the cluster running?")
        sys.exit(1)

    print(f"\n✓ Using leader: {leader_url}\n")

    run_benchmark(leader_url, args.namespace, args.count, args.rate, args.api_key)


if __name__ == "__main__":
    main()
