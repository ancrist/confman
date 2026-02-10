#!/usr/bin/env bash
set -euo pipefail

# Confman Raft Cluster Manager
# Manages a local 3-node cluster for development.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../../.." && pwd)"
PROJECT="src/Confman.Api"
PROJECT_DIR="${REPO_ROOT}/${PROJECT}"
PORTS=(6100 6200 6300)
NODES=(node1 node2 node3)

usage() {
    cat <<EOF
Usage: $(basename "$0") <command>

Commands:
  start [--tmux]  Start all 3 nodes (--tmux: open log tail panes)
  stop            Stop all running nodes
  status          Check health of each node
  wipe            Stop nodes and delete all data directories
EOF
    exit 1
}

start_cluster() {
    echo "Starting 3-node Confman cluster..."

    # Build once upfront to avoid DLL lock contention between nodes
    echo "  Building project..."
    if ! dotnet build "${REPO_ROOT}/${PROJECT}" -c Release 2>&1 | grep -v "^MSBUILD : error : Building target"; then
        echo "ERROR: Build failed. Check build output above."
        return 1
    fi

    for i in "${!PORTS[@]}"; do
        local port="${PORTS[$i]}"
        local node="${NODES[$i]}"
        local log_file="/tmp/confman-${node}.log"

        if curl -s --max-time 1 "http://127.0.0.1:${port}/health" >/dev/null 2>&1; then
            echo "  Node ${node} (port ${port}) is already running, skipping."
            continue
        fi

        echo "  Starting ${node} on port ${port}..."
        ASPNETCORE_ENVIRONMENT="${node}" \
            nohup dotnet run --project "${REPO_ROOT}/${PROJECT}" \
                -c Release --no-build --no-launch-profile \
                --urls "http://127.0.0.1:${port}" \
                > "${log_file}" 2>&1 &
    done

    echo ""
    echo "Waiting for quorum..."
    local attempts=0
    local max_attempts=20
    while [ $attempts -lt $max_attempts ]; do
        sleep 1
        attempts=$((attempts + 1))

        local ready=0
        for port in "${PORTS[@]}"; do
            if curl -s --max-time 1 "http://127.0.0.1:${port}/health/ready" | grep -q '"status":"ready"' 2>/dev/null; then
                ready=$((ready + 1))
            fi
        done

        if [ $ready -ge 2 ]; then
            echo "Quorum established (${ready}/3 nodes ready)."
            echo ""
            check_status
            return 0
        fi
    done

    echo "WARNING: Quorum not established after ${max_attempts}s. Check logs in /tmp/confman-node*.log"
    check_status
    return 1
}

stop_cluster() {
    echo "Stopping Confman cluster..."
    pkill -f "Confman.Api" 2>/dev/null && echo "  Nodes stopped." || echo "  No nodes were running."
}

check_status() {
    for i in "${!PORTS[@]}"; do
        local port="${PORTS[$i]}"
        local node="${NODES[$i]}"
        echo "--- ${node} (port ${port}) ---"
        curl -s --max-time 2 "http://127.0.0.1:${port}/health/ready" | jq . 2>/dev/null || echo "UNREACHABLE"
    done
}

open_tmux_logs() {
    if ! command -v tmux &>/dev/null; then
        echo "ERROR: tmux is not installed. Install with: brew install tmux"
        return 1
    fi

    local session="confman-logs"

    # Kill existing session for a clean start
    tmux kill-session -t "$session" 2>/dev/null || true

    # Create session with first node's log
    tmux new-session -d -s "$session" "tail -f /tmp/confman-node1.log"
    tmux split-window -v -t "$session" "tail -f /tmp/confman-node2.log"
    tmux split-window -v -t "$session" "tail -f /tmp/confman-node3.log"
    tmux select-layout -t "$session" even-vertical

    echo "Attaching to tmux session '${session}'..."
    if [ -n "${TMUX:-}" ]; then
        tmux switch-client -t "$session"
    else
        tmux attach-session -t "$session"
    fi
}

wipe_data() {
    stop_cluster
    echo ""
    echo "Wiping cluster data..."
    for port in "${PORTS[@]}"; do
        # Data dirs are relative to project dir (dotnet run sets cwd there)
        local dir="${PROJECT_DIR}/data-${port}"
        if [ -d "$dir" ]; then
            rm -rf "$dir"
            echo "  Deleted ${dir}"
        fi
    done
    echo "Clearing log files..."
    for node in "${NODES[@]}"; do
        rm -f "/tmp/confman-${node}.log"
    done
    echo "Done."
}

# --- Main ---
[[ $# -lt 1 ]] && usage

USE_TMUX=false
for arg in "$@"; do
    [[ "$arg" == "--tmux" ]] && USE_TMUX=true
done

case "$1" in
    start)
        start_cluster
        if [ "$USE_TMUX" = true ]; then
            open_tmux_logs
        fi
        ;;
    stop)   stop_cluster ;;
    status) check_status ;;
    wipe)   wipe_data ;;
    *)      usage ;;
esac
