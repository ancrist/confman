# Benchmark Cluster
1. Discover the leader node using GET /health/ready on ports 6100, 6200, 6300
2. Run the benchmark script with: python benchmark.py --leader <leader_url> --rate 100 --duration 30
3. Use `uv` for any Python dependencies (NEVER pip)
4. Collect results and append to docs/BENCHMARK_RESULTS.md
5. Report: throughput, p50/p99 latency, error rate
