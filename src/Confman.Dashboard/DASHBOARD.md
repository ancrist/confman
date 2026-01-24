# Confman Cluster Dashboard

A lightweight web UI for monitoring the Confman Raft cluster.

## Features

- **Cluster Health Summary** — Overall status (HEALTHY, DEGRADED, NO QUORUM) with node count and current leader
- **Node Status Cards** — Real-time status for each node showing:
  - Connection state (online/offline)
  - Raft role (Leader/Follower)
  - Current term
  - Known leader endpoint
- **Stored Configurations** — Lists all key-value pairs across namespaces
- **Auto-refresh** — Polls every 2 seconds (toggleable)

## Running the Dashboard

```bash
cd src/Confman.Dashboard
npm install
npm run dev
```

Opens at `http://localhost:5173` by default.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Dashboard (Vite)                     │
│                   localhost:5173                        │
└─────────────────────┬───────────────────────────────────┘
                      │ HTTP (CORS)
        ┌─────────────┼─────────────┐
        ▼             ▼             ▼
   ┌─────────┐   ┌─────────┐   ┌─────────┐
   │ Node 1  │   │ Node 2  │   │ Node 3  │
   │  :6100  │   │  :6200  │   │  :6300  │
   └─────────┘   └─────────┘   └─────────┘
```

## API Endpoints Used

| Endpoint | Purpose |
|----------|---------|
| `GET /health/ready` | Node status, role, term, leader info |
| `GET /api/v1/configs` | List all stored configurations |

## Status Indicators

### Cluster Summary

| Status | Meaning |
|--------|---------|
| **HEALTHY** (green) | All nodes ready, leader elected |
| **DEGRADED** (yellow) | Quorum exists but not all nodes ready |
| **NO QUORUM** (red) | Fewer than 2 nodes online, no leader |

### Node Status

| Status | Color | Meaning |
|--------|-------|---------|
| READY | Green | Node reachable, quorum established |
| NO QUORUM | Yellow | Node reachable but no leader elected |
| OFFLINE | Red | Node unreachable (network error/timeout) |

## Configuration

Node endpoints are configured in `index.html`:

```javascript
const nodes = [
    { name: 'Node 1', url: 'http://127.0.0.1:6100' },
    { name: 'Node 2', url: 'http://127.0.0.1:6200' },
    { name: 'Node 3', url: 'http://127.0.0.1:6300' }
];
```

## Tech Stack

- **Vite** — Development server and build tool
- **Vanilla JS** — No framework dependencies
- **CSS** — Custom dark theme styling

## Build for Production

```bash
npm run build
```

Output in `dist/` directory. Serve with any static file server.
