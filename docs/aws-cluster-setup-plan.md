# Plan: AWS 3-Node Confman Cluster Setup

## Context

Spin up 3 EC2 instances in eu-north-1 for testing the Confman distributed Raft key-value store. Each node gets its own instance with isolated gp3 (tuned) EBS for WAL/LiteDB IO isolation. One instance also runs the Nexus-6 dashboard. Full auto-deploy: upload code, install deps, build, start cluster.

## Infrastructure

| Resource | Details |
|----------|---------|
| Region | eu-north-1 (Stockholm) |
| VPC | Default (`vpc-018b05344c15dce50`) |
| Instances | 3x t3.medium (2 vCPU, 4 GiB) |
| AMI | Amazon Linux 2023 (latest) |
| EBS | 30 GiB gp3, tuned to 6,000 IOPS / 250 MB/s throughput |
| Key Pair | New `confman-test` key pair, saved locally |

## Networking / Security Group: `confman-cluster-sg`

| Rule | Port | Source | Purpose |
|------|------|--------|---------|
| SSH | 22 | User's IP (`188.27.180.169/32`) | Remote access |
| Confman API | 6100 | User's IP | Dashboard JS needs to reach all 3 nodes from browser |
| Confman API | 6100 | Self (SG) | Inter-node Raft communication |
| Dashboard | 5173 | User's IP | HTTP access to Nexus-6 dashboard |

All 3 nodes run Confman on port 6100 (separate machines, no port conflicts). Node identity determined by `CONFMAN_NODE_ID` env var + per-node appsettings.

## Scripts to Create

### 1. `scripts/aws-setup.sh` — Provision & Deploy

Steps:
1. Create SSH key pair `confman-test`, save `.pem` to `scripts/confman-test.pem`
2. Create security group `confman-cluster-sg` with rules above
3. Resolve latest Amazon Linux 2023 AMI via SSM parameter
4. Launch 3 instances with tags (`Name: confman-node1/2/3`) and user-data that installs:
   - .NET SDK 10 (from Microsoft repo)
   - Node.js 20 (from NodeSource)
   - Git
5. Wait for instances to pass status checks
6. For each instance: SCP the confman source code, build, generate per-node `appsettings.nodeN.json` with private IPs
7. Start Confman on each node via SSH
8. On node1: also install dashboard deps and start Vite dev server (bound to `0.0.0.0:5173`)
9. Output connection info (SSH commands, dashboard URL, API endpoints)

The user-data script:
```bash
#!/bin/bash
# Install .NET SDK 10
rpm -Uvh https://packages.microsoft.com/config/rhel/9/packages-microsoft-prod.rpm
dnf install -y dotnet-sdk-10.0
# Install Node.js 20
dnf install -y nodejs20 npm
# Install git
dnf install -y git
```

### 2. `scripts/aws-manage.sh` — Stop/Start/Terminate

Commands:
- `./scripts/aws-manage.sh stop` — Stop all 3 instances (preserves EBS, no compute charges)
- `./scripts/aws-manage.sh start` — Start stopped instances, show new public IPs
- `./scripts/aws-manage.sh status` — Show instance state, IPs, health
- `./scripts/aws-manage.sh terminate` — Terminate all instances + cleanup SG + key pair
- `./scripts/aws-manage.sh ssh node1|node2|node3` — SSH into a specific node

Uses instance tags (`confman-cluster=true`) to find instances, so it works even after IP changes from stop/start.

### 3. Dashboard Adaptation

The dashboard `index.html` has hardcoded node URLs (`127.0.0.1:6100/6200/6300`). On EC2, each node runs on port 6100 but on different public IPs. The deploy step will `sed`-replace the `nodes` array in the deployed copy with the actual public IPs:

```javascript
const nodes = [
    { name: 'REPLICANT-1', url: 'http://<public-ip-1>:6100' },
    { name: 'REPLICANT-2', url: 'http://<public-ip-2>:6100' },
    { name: 'REPLICANT-3', url: 'http://<public-ip-3>:6100' },
];
```

### 4. Per-Node Config Generation

Each node gets a generated `appsettings.nodeN.json` using **private IPs** for inter-node Raft (lower latency, no NAT):

```json
{
  "publicEndPoint": "http://<private-ip-N>:6100/",
  "coldStart": false,
  "standby": false,
  "members": [
    "http://<private-ip-1>:6100/",
    "http://<private-ip-2>:6100/",
    "http://<private-ip-3>:6100/"
  ]
}
```

## File Changes

| File | Action |
|------|--------|
| `scripts/aws-setup.sh` | **Create** — Provision infrastructure + full deploy |
| `scripts/aws-manage.sh` | **Create** — Stop/start/terminate/status/ssh management |

No changes to existing source files. The deploy step modifies configs on the remote instances only.

## Estimated Cost

| Item | Rate | 3 Days (72h) |
|------|------|-------------|
| 3x t3.medium | 3 x $0.0416/hr | $8.99 |
| 3x gp3 30GiB (6K IOPS, 250MB/s) | ~$0.003/hr each | $0.68 |
| Data transfer (minimal) | ~$0 | ~$0 |
| **Total** | | **~$9.67** |

When stopped via `aws-manage.sh stop`, only EBS charges apply (~$0.23/day).

## Verification

1. Run `./scripts/aws-setup.sh` — should complete in ~5 minutes
2. SSH into each node: `./scripts/aws-manage.sh ssh node1`
3. Check cluster health: `curl http://<public-ip>:6100/health/ready`
4. Open dashboard: `http://<node1-public-ip>:5173` in browser
5. Write a config via API and verify replication across nodes
6. Run `./scripts/aws-manage.sh stop` and confirm instances stop
7. Run `./scripts/aws-manage.sh start` and confirm cluster reforms
