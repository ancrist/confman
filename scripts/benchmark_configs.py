#!/usr/bin/env python3
"""
Benchmark script for Confman API.
Supports both read and write scenarios at configurable rates.

Usage:
    # Write benchmark - creates configs sequentially
    python benchmark_configs.py write --rate 10 --count 100 --namespace test-benchmark

    # Write benchmark with custom payload size (for WAL padding tests)
    python benchmark_configs.py write -r 20 -c 50 -n test-bench --payload-size 1KB
    python benchmark_configs.py write -r 20 -c 50 -n test-bench -s 15KB

    # Write as fast as possible (no rate limiting) - measures true throughput
    python benchmark_configs.py write --count 100 --unlimited --namespace throughput-test

    # Read benchmark - fetches ALL configs from cluster, randomly reads keys
    python benchmark_configs.py read --rate 50 --count 100

    # Read benchmark - fetches configs from specific namespace only
    python benchmark_configs.py read --rate 50 --count 100 --namespace test-benchmark

    # Read as fast as possible (no rate limiting)
    python benchmark_configs.py read --count 1000 --unlimited

    # Read with specific keys (random selection from provided list)
    python benchmark_configs.py read -r 100 -c 500 -n my-namespace --keys config-00000,config-00001

    # Read with reproducible random seed
    python benchmark_configs.py read -r 50 -c 100 --seed 42
"""

import argparse
import random
import requests
import time
import sys
from datetime import datetime
from typing import Optional, List

# ANSI color codes
class Colors:
    GREEN = '\033[92m'
    RED = '\033[91m'
    YELLOW = '\033[93m'
    CYAN = '\033[96m'
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


def parse_payload_size(size_str: str) -> int:
    """Parse payload size string (e.g., '100', '1KB', '15KB') to bytes."""
    size_str = size_str.strip().upper()
    if size_str.endswith("KB"):
        return int(size_str[:-2]) * 1024
    elif size_str.endswith("B"):
        return int(size_str[:-1])
    else:
        return int(size_str)


def generate_payload(size_bytes: int, index: int) -> str:
    """Generate a payload of approximately the specified size."""
    # Start with a prefix containing metadata
    prefix = f"value-{index:05d}-{datetime.now().isoformat()}-"

    # Fill remaining space with repeating pattern
    remaining = size_bytes - len(prefix)
    if remaining <= 0:
        return prefix[:size_bytes]

    # Use a repeating pattern to fill the payload
    pattern = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
    fill = (pattern * ((remaining // len(pattern)) + 1))[:remaining]

    return prefix + fill


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


def list_configs(base_url: str, namespace: Optional[str], api_key: str) -> List[dict]:
    """
    List all configs. Returns list of dicts with 'namespace' and 'key'.
    If namespace is None, fetches all configs from all namespaces via /api/v1/configs.
    If namespace is provided, fetches configs from that namespace only.
    """
    if namespace:
        url = f"{base_url}/api/v1/namespaces/{namespace}/config"
    else:
        url = f"{base_url}/api/v1/configs"

    try:
        response = requests.get(url, headers=get_headers(api_key), timeout=30)
        if response.status_code == 200:
            configs = response.json()
            if isinstance(configs, list):
                result = []
                for c in configs:
                    # Handle both /api/v1/configs (has 'ns') and namespace-specific endpoint (has 'key')
                    ns = c.get("ns") or c.get("namespace") or namespace
                    key = c.get("key") or c.get("name")
                    if ns and key:
                        result.append({"namespace": ns, "key": key})
                return result
            return []
        else:
            print(f"✗ Failed to list configs: {response.status_code} - {response.text}")
            return []
    except requests.RequestException as e:
        print(f"✗ Error listing configs: {e}")
        return []


def read_config(base_url: str, namespace: str, key: str, api_key: str) -> dict:
    """Read a config entry. Returns dict with request details."""
    path = f"/api/v1/namespaces/{namespace}/config/{key}"
    url = f"{base_url}{path}"

    start = time.perf_counter()
    try:
        response = requests.get(
            url, headers=get_headers(api_key),
            timeout=10
        )
        latency_ms = (time.perf_counter() - start) * 1000

        value = None
        if response.status_code == 200:
            try:
                data = response.json()
                value = data.get("value", "<no value>")
            except Exception:
                value = "<parse error>"

        return {
            "success": response.status_code == 200,
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
            "value": None,
            "error": str(e),
        }


def print_header(mode: str, target_url: str, namespace: str, count: int, rate: float):
    """Print benchmark header."""
    delay = 1.0 / rate
    mode_color = Colors.CYAN if mode == "read" else Colors.YELLOW

    print(f"\n{'='*60}")
    print(f"Confman Benchmark - {mode_color}{mode.upper()}{Colors.RESET}")
    print(f"{'='*60}")
    print(f"Target:      {target_url}")
    print(f"Namespace:   {namespace}")
    print(f"Config count: {count}")
    print(f"Target rate: {rate} req/s")
    print(f"Delay:       {delay*1000:.2f} ms between requests")
    print(f"{'='*60}\n")


def print_results(mode: str, total_time: float, successes: int, failures: int,
                  rate: float, latencies: List[float]):
    """Print benchmark results."""
    avg_latency = sum(latencies) / len(latencies)
    min_latency = min(latencies)
    max_latency = max(latencies)
    actual_rate = len(latencies) / total_time

    # Sort for percentiles
    sorted_latencies = sorted(latencies)
    p50 = sorted_latencies[int(len(sorted_latencies) * 0.50)]
    p95 = sorted_latencies[int(len(sorted_latencies) * 0.95)]
    p99 = sorted_latencies[int(len(sorted_latencies) * 0.99)]

    print(f"\n{'='*60}")
    print(f"Results - {mode.upper()}")
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


def calculate_json_overhead(namespace: str, key_template: str = "config-00000") -> int:
    """Calculate the JSON overhead for a SetConfigCommand entry (excluding payload value).

    The full JSON structure is:
    {"$type":"set_config","Namespace":"<ns>","Key":"<key>","Value":"<payload>",
     "Type":"string","Author":"Development Admin","Timestamp":"2026-02-04T23:39:00.000Z"}
    """
    # Approximate timestamp (ISO 8601 format)
    timestamp = "2026-02-04T23:39:00.000Z"
    template = (
        f'{{"$type":"set_config","Namespace":"{namespace}","Key":"{key_template}",'
        f'"Value":"","Type":"string","Author":"Development Admin","Timestamp":"{timestamp}"}}'
    )
    return len(template)


def run_write_benchmark(leader_url: str, namespace: str, count: int, rate: float, api_key: str, payload_size: int = 100, unlimited: bool = False):
    """Run write benchmark creating configs at the specified rate."""
    delay = 0 if unlimited else (1.0 / rate)

    # Calculate JSON overhead for WAL entry
    json_overhead = calculate_json_overhead(namespace)
    total_entry_size = payload_size + json_overhead

    # Generate example payload for display
    example_value = generate_payload(payload_size, 0)
    example_timestamp = datetime.now().isoformat() + "Z"
    example_json = (
        f'{{"$type":"set_config","Namespace":"{namespace}","Key":"config-00000",'
        f'"Value":"{example_value}","Type":"string","Author":"Development Admin",'
        f'"Timestamp":"{example_timestamp}"}}'
    )

    # Print header with payload size info
    print(f"\n{'='*60}")
    if unlimited:
        print(f"Confman Benchmark - {Colors.YELLOW}WRITE{Colors.RESET} (UNLIMITED)")
    else:
        print(f"Confman Benchmark - {Colors.YELLOW}WRITE{Colors.RESET}")
    print(f"{'='*60}")
    print(f"Target:       {leader_url}")
    print(f"Namespace:    {namespace}")
    print(f"Config count: {count}")
    if unlimited:
        print(f"Rate:         {Colors.YELLOW}UNLIMITED (as fast as possible){Colors.RESET}")
    else:
        print(f"Target rate:  {rate} req/s")
        print(f"Delay:        {delay*1000:.2f} ms between requests")
    print(f"Payload size: {payload_size} bytes ({payload_size/1024:.1f} KB)")
    print(f"JSON overhead: {json_overhead} bytes (includes $type, Type, Timestamp)")
    print(f"Total entry:  {total_entry_size} bytes ({total_entry_size/1024:.2f} KB)")
    print(f"Actual JSON:  {len(example_json)} bytes")
    print(f"{'='*60}")
    print(f"\nExample WAL entry JSON ({len(example_json)} bytes):")
    print(f"{example_json}")
    print(f"{'='*60}\n")

    # Create namespace first
    if not create_namespace(leader_url, namespace, api_key):
        print("Failed to create namespace, aborting benchmark")
        sys.exit(1)

    print(f"\nCreating {count} configs at {rate} req/s (payload: {payload_size} bytes)...\n")

    successes = 0
    failures = 0
    latencies = []

    start_time = time.perf_counter()

    for i in range(count):
        key = f"config-{i:05d}"
        value = generate_payload(payload_size, i)

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

        # Print every 100th request (or first/last) to avoid console spam
        if (i + 1) % 100 == 0 or i == 0 or i == count - 1:
            value_preview = result['value'][:25] if result['value'] else ''
            print(f"  [{i+1:4d}/{count}] {status} PUT {result['path']} | key={result['key']} | value={value_preview}... | {http_status} | {result['latency_ms']:.1f}ms")

        # Sleep to maintain rate (except for last iteration, or if unlimited)
        if not unlimited and i < count - 1:
            time.sleep(delay)

    total_time = time.perf_counter() - start_time

    # For unlimited mode, show actual achieved rate
    if unlimited:
        actual_rate = count / total_time
        print(f"\n{'='*60}")
        print(f"Results - WRITE (UNLIMITED)")
        print(f"{'='*60}")
        print(f"Total time:    {total_time:.2f}s")
        print(f"Successes:     {successes}")
        print(f"Failures:      {failures}")
        print(f"Achieved rate: {Colors.GREEN}{actual_rate:.1f} req/s{Colors.RESET}")

        avg_latency = sum(latencies) / len(latencies)
        min_latency = min(latencies)
        max_latency = max(latencies)
        sorted_latencies = sorted(latencies)
        p50 = sorted_latencies[int(len(sorted_latencies) * 0.50)]
        p95 = sorted_latencies[int(len(sorted_latencies) * 0.95)]
        p99 = sorted_latencies[int(len(sorted_latencies) * 0.99)]

        print(f"\nLatency (ms):")
        print(f"  Min:  {min_latency:.1f}")
        print(f"  Avg:  {avg_latency:.1f}")
        print(f"  Max:  {max_latency:.1f}")
        print(f"  P50:  {p50:.1f}")
        print(f"  P95:  {p95:.1f}")
        print(f"  P99:  {p99:.1f}")
        print(f"{'='*60}\n")
    else:
        print_results("write", total_time, successes, failures, rate, latencies)


def run_read_benchmark(target_urls: List[str], namespace: Optional[str], count: int, rate: float,
                       api_key: str, keys: Optional[List[str]] = None, seed: Optional[int] = None,
                       unlimited: bool = False):
    """Run read benchmark reading configs at the specified rate with random key selection.

    target_urls can be a single URL or multiple URLs for round-robin distribution.
    """
    delay = 0 if unlimited else (1.0 / rate)

    display_namespace = namespace or "(all namespaces)"

    # Display target info
    if len(target_urls) == 1:
        target_display = target_urls[0]
    else:
        target_display = f"{len(target_urls)} nodes (round-robin)"

    if unlimited:
        print(f"\n{'='*60}")
        print(f"Confman Benchmark - {Colors.CYAN}READ{Colors.RESET} (UNLIMITED)")
        print(f"{'='*60}")
        print(f"Target:      {target_display}")
        if len(target_urls) > 1:
            for url in target_urls:
                print(f"             - {url}")
        print(f"Namespace:   {display_namespace}")
        print(f"Config count: {count}")
        print(f"Rate:        {Colors.YELLOW}UNLIMITED (as fast as possible){Colors.RESET}")
        print(f"{'='*60}\n")
    else:
        print_header("read", target_display, display_namespace, count, rate)

    # Use first URL for fetching config list
    primary_url = target_urls[0]

    # Determine which configs to read
    if keys:
        # Use provided keys (must have namespace specified)
        if not namespace:
            print(f"{Colors.RED}✗ --keys requires --namespace to be specified{Colors.RESET}")
            sys.exit(1)
        config_list = [{"namespace": namespace, "key": k} for k in keys]
        print(f"Using {len(keys)} provided keys\n")
    else:
        # Fetch all configs
        if namespace:
            print(f"Fetching config list from namespace '{namespace}'...")
        else:
            print("Fetching all configs from cluster...")

        config_list = list_configs(primary_url, namespace, api_key)

        if not config_list:
            if namespace:
                print(f"\n{Colors.RED}✗ No configs found in namespace '{namespace}'.{Colors.RESET}")
                print("  Run a write benchmark first to populate configs:")
                print(f"    python benchmark_configs.py write -n {namespace} -c 100")
            else:
                print(f"\n{Colors.RED}✗ No configs found in cluster.{Colors.RESET}")
                print("  Run a write benchmark first to populate configs:")
                print("    python benchmark_configs.py write -n benchmark -c 100")
            sys.exit(1)

        # Count unique namespaces
        unique_ns = set(c["namespace"] for c in config_list)
        print(f"✓ Found {len(config_list)} configs across {len(unique_ns)} namespace(s)\n")

    # Initialize random number generator
    if seed is not None:
        random.seed(seed)
        print(f"Using random seed: {seed}")
    else:
        print("Using random key selection (no seed)")

    print(f"Issuing {count} random GET requests at {rate} req/s...\n")

    successes = 0
    failures = 0
    latencies = []

    start_time = time.perf_counter()

    for i in range(count):
        # Randomly select a config from the list
        config = random.choice(config_list)
        cfg_namespace = config["namespace"]
        cfg_key = config["key"]

        # Round-robin across target URLs
        target_url = target_urls[i % len(target_urls)]

        result = read_config(target_url, cfg_namespace, cfg_key, api_key)
        latencies.append(result["latency_ms"])

        if result["success"]:
            successes += 1
            status = f"{Colors.GREEN}✓{Colors.RESET}"
            http_status = f"{Colors.GREEN}HTTP {result['status_code']}{Colors.RESET}"
            value_preview = str(result['value'])[:30] if result['value'] else '<empty>'
        else:
            failures += 1
            status = f"{Colors.RED}✗{Colors.RESET}"
            http_status = f"{Colors.RED}HTTP {result['status_code']}{Colors.RESET}"
            value_preview = result.get('error', 'failed')[:30]

        # Print every 100th request (or first/last) to avoid console spam
        if (i + 1) % 100 == 0 or i == 0 or i == count - 1:
            print(f"  [{i+1:4d}/{count}] {status} GET {result['path']} | value={value_preview}... | {http_status} | {result['latency_ms']:.1f}ms")

        # Sleep to maintain rate (except for last iteration, or if unlimited)
        if not unlimited and i < count - 1:
            time.sleep(delay)

    total_time = time.perf_counter() - start_time

    # For unlimited mode, show actual achieved rate
    if unlimited:
        actual_rate = count / total_time
        print(f"\n{'='*60}")
        print(f"Results - READ (UNLIMITED)")
        print(f"{'='*60}")
        print(f"Total time:    {total_time:.2f}s")
        print(f"Successes:     {successes}")
        print(f"Failures:      {failures}")
        print(f"Achieved rate: {Colors.GREEN}{actual_rate:.1f} req/s{Colors.RESET}")

        avg_latency = sum(latencies) / len(latencies)
        min_latency = min(latencies)
        max_latency = max(latencies)
        sorted_latencies = sorted(latencies)
        p50 = sorted_latencies[int(len(sorted_latencies) * 0.50)]
        p95 = sorted_latencies[int(len(sorted_latencies) * 0.95)]
        p99 = sorted_latencies[int(len(sorted_latencies) * 0.99)]

        print(f"\nLatency (ms):")
        print(f"  Min:  {min_latency:.1f}")
        print(f"  Avg:  {avg_latency:.1f}")
        print(f"  Max:  {max_latency:.1f}")
        print(f"  P50:  {p50:.1f}")
        print(f"  P95:  {p95:.1f}")
        print(f"  P99:  {p99:.1f}")
        print(f"{'='*60}\n")
    else:
        print_results("read", total_time, successes, failures, rate, latencies)


def main():
    parser = argparse.ArgumentParser(
        description="Benchmark Confman API with read and write scenarios"
    )

    # Subcommands for read/write
    subparsers = parser.add_subparsers(dest="mode", help="Benchmark mode")

    # Common arguments for both modes
    def add_common_args(subparser):
        subparser.add_argument(
            "-r", "--rate",
            type=float,
            default=10,
            help="Requests per second (default: 10)"
        )
        subparser.add_argument(
            "-c", "--count",
            type=int,
            default=100,
            help="Number of requests to make (default: 100)"
        )
        subparser.add_argument(
            "-n", "--namespace",
            type=str,
            default="benchmark",
            help="Namespace to use (default: benchmark)"
        )
        subparser.add_argument(
            "--nodes",
            type=str,
            nargs="+",
            default=DEFAULT_NODES,
            help=f"Cluster node URLs (default: {' '.join(DEFAULT_NODES)})"
        )
        subparser.add_argument(
            "-k", "--api-key",
            type=str,
            default="confman_dev_abc123",
            help="API key for authentication (default: confman_dev_abc123)"
        )

    # Write subcommand
    write_parser = subparsers.add_parser("write", help="Write benchmark - create configs")
    add_common_args(write_parser)
    write_parser.add_argument(
        "-s", "--payload-size",
        type=str,
        default="100",
        help="Payload size in bytes, or use KB suffix (e.g., '1KB', '15KB'). Default: 100 bytes"
    )
    write_parser.add_argument(
        "--unlimited",
        action="store_true",
        help="Run at maximum speed (no rate limiting)"
    )

    # Read subcommand (namespace is optional - reads from all namespaces if not specified)
    read_parser = subparsers.add_parser("read", help="Read benchmark - read configs")
    add_common_args(read_parser)
    # Override namespace to make it optional with None default
    read_parser.set_defaults(namespace=None)
    for action in read_parser._actions:
        if action.dest == 'namespace':
            action.default = None
            action.help = "Namespace to read from (default: all namespaces via /api/v1/configs)"
    read_parser.add_argument(
        "--keys",
        type=str,
        help="Comma-separated list of specific keys to read (random selection from list)"
    )
    read_parser.add_argument(
        "--seed",
        type=int,
        help="Random seed for reproducible key selection"
    )
    read_parser.add_argument(
        "--unlimited",
        action="store_true",
        help="No rate limiting - send requests as fast as possible"
    )
    read_parser.add_argument(
        "--any-node",
        action="store_true",
        help="Read from any healthy node, not just leader (for read scaling tests)"
    )

    args = parser.parse_args()

    if not args.mode:
        parser.print_help()
        print("\nError: Please specify a mode: 'write' or 'read'")
        sys.exit(1)

    if args.rate <= 0:
        print("Error: Rate must be positive")
        sys.exit(1)

    if args.count <= 0:
        print("Error: Count must be positive")
        sys.exit(1)

    # For writes, always use leader
    # For reads, can use any node if --any-node is specified
    if args.mode == "write":
        target_url = discover_leader(args.nodes)
        if not target_url:
            print("\n✗ Could not discover cluster leader. Is the cluster running?")
            sys.exit(1)
        print(f"\n✓ Using leader: {target_url}\n")

        # Parse payload size
        try:
            payload_size = parse_payload_size(args.payload_size)
        except ValueError:
            print(f"Error: Invalid payload size '{args.payload_size}'. Use bytes (e.g., '100') or KB (e.g., '1KB')")
            sys.exit(1)

        unlimited = getattr(args, 'unlimited', False)
        run_write_benchmark(target_url, args.namespace, args.count, args.rate, args.api_key, payload_size, unlimited)

    elif args.mode == "read":
        if hasattr(args, 'any_node') and args.any_node:
            # Use ALL healthy nodes for reads (round-robin)
            target_urls = []
            print("Discovering healthy nodes...")
            for node in args.nodes:
                try:
                    response = requests.get(f"{node}/health/ready", timeout=5)
                    if response.status_code == 200:
                        target_urls.append(node)
                        print(f"  ✓ {node}")
                    else:
                        print(f"  ✗ {node} (HTTP {response.status_code})")
                except requests.RequestException as e:
                    print(f"  ✗ {node} (unreachable)")
            if not target_urls:
                print("\n✗ No healthy nodes found. Is the cluster running?")
                sys.exit(1)
            print(f"\n✓ Using {len(target_urls)} nodes (round-robin)\n")
        else:
            target_url = discover_leader(args.nodes)
            if not target_url:
                print("\n✗ Could not discover cluster leader. Is the cluster running?")
                sys.exit(1)
            print(f"\n✓ Using leader: {target_url}\n")
            target_urls = [target_url]

        # Parse keys if provided
        keys = None
        if hasattr(args, 'keys') and args.keys:
            keys = [k.strip() for k in args.keys.split(",")]

        # Get seed if provided
        seed = getattr(args, 'seed', None)

        # Check if unlimited mode
        unlimited = getattr(args, 'unlimited', False)

        run_read_benchmark(target_urls, args.namespace, args.count, args.rate, args.api_key, keys, seed, unlimited)


if __name__ == "__main__":
    main()
