#!/bin/bash
#
# Run Confman 3-node Raft cluster in Release mode
#
# Usage:
#   ./scripts/run-cluster.sh          # Start all 3 nodes in background
#   ./scripts/run-cluster.sh node1    # Start only node1 in foreground
#   ./scripts/run-cluster.sh stop     # Stop all running nodes
#   ./scripts/run-cluster.sh status   # Check which nodes are running
#   ./scripts/run-cluster.sh build    # Build Release without starting
#

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DLL_PATH="$PROJECT_ROOT/src/Confman.Api/bin/Release/net10.0/Confman.Api.dll"
LOG_DIR="$PROJECT_ROOT/logs"

# Node configuration
declare -A NODES=(
    ["node1"]="http://localhost:6100"
    ["node2"]="http://localhost:6200"
    ["node3"]="http://localhost:6300"
)

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
NC='\033[0m' # No Color

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

build_release() {
    log_info "Building Release configuration..."
    cd "$PROJECT_ROOT"
    dotnet build -c Release --verbosity quiet
    log_info "Build complete: $DLL_PATH"
}

check_build() {
    if [[ ! -f "$DLL_PATH" ]]; then
        log_warn "Release build not found, building..."
        build_release
    fi
}

start_node() {
    local node_id=$1
    local url=${NODES[$node_id]}

    if [[ -z "$url" ]]; then
        log_error "Unknown node: $node_id"
        exit 1
    fi

    check_build

    log_info "Starting $node_id on $url..."
    cd "$PROJECT_ROOT"
    CONFMAN_NODE_ID=$node_id exec dotnet "$DLL_PATH" --urls "$url"
}

start_node_background() {
    local node_id=$1
    local url=${NODES[$node_id]}

    mkdir -p "$LOG_DIR"

    log_info "Starting $node_id on $url (background)..."
    cd "$PROJECT_ROOT"
    CONFMAN_NODE_ID=$node_id nohup dotnet "$DLL_PATH" --urls "$url" > "$LOG_DIR/$node_id.log" 2>&1 &
    echo $! > "$LOG_DIR/$node_id.pid"
    log_info "  PID: $! | Log: $LOG_DIR/$node_id.log"
}

start_all() {
    check_build

    log_info "Starting 3-node Raft cluster..."
    echo ""

    for node_id in node1 node2 node3; do
        start_node_background "$node_id"
    done

    echo ""
    log_info "Cluster started. Waiting for leader election..."
    sleep 3

    # Check health
    for node_id in node1 node2 node3; do
        local url=${NODES[$node_id]}
        local status=$(curl -s "$url/health/ready" 2>/dev/null | grep -o '"role":"[^"]*"' | head -1 || echo "unreachable")
        echo "  $node_id ($url): $status"
    done

    echo ""
    log_info "View logs: tail -f $LOG_DIR/*.log"
    log_info "Stop cluster: $0 stop"
}

stop_all() {
    log_info "Stopping cluster..."

    for node_id in node1 node2 node3; do
        local pid_file="$LOG_DIR/$node_id.pid"
        if [[ -f "$pid_file" ]]; then
            local pid=$(cat "$pid_file")
            if kill -0 "$pid" 2>/dev/null; then
                kill "$pid"
                log_info "Stopped $node_id (PID: $pid)"
            else
                log_warn "$node_id was not running"
            fi
            rm -f "$pid_file"
        else
            log_warn "No PID file for $node_id"
        fi
    done
}

show_status() {
    echo "Cluster Status:"
    echo "==============="

    for node_id in node1 node2 node3; do
        local url=${NODES[$node_id]}
        local pid_file="$LOG_DIR/$node_id.pid"
        local running="no"
        local pid="-"

        if [[ -f "$pid_file" ]]; then
            pid=$(cat "$pid_file")
            if kill -0 "$pid" 2>/dev/null; then
                running="yes"
            fi
        fi

        local health=$(curl -s -m 2 "$url/health/ready" 2>/dev/null || echo '{"status":"unreachable"}')
        local role=$(echo "$health" | grep -o '"role":"[^"]*"' | cut -d'"' -f4 || echo "-")

        printf "  %-6s | %-25s | PID: %-6s | Running: %-3s | Role: %s\n" \
            "$node_id" "$url" "$pid" "$running" "$role"
    done
}

# Main
case "${1:-all}" in
    node1|node2|node3)
        start_node "$1"
        ;;
    all)
        start_all
        ;;
    stop)
        stop_all
        ;;
    status)
        show_status
        ;;
    build)
        build_release
        ;;
    *)
        echo "Usage: $0 [node1|node2|node3|all|stop|status|build]"
        exit 1
        ;;
esac
