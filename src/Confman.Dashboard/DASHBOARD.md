# Confman Cluster Dashboard

A lightweight web UI for monitoring the Confman Raft cluster.

## Features

- **Cluster Health Beacon** — Animated status orb showing CLUSTER ONLINE, DEGRADED, or QUORUM LOST
- **Node Status Cards** — Real-time status for each node showing:
  - Connection state (online/offline)
  - Raft role (Leader/Follower)
  - Current epoch (term)
  - Colored top border indicating role
- **Namespaces** — Clickable list to view audit trail
- **Configuration Entries** — Lists all key-value pairs across namespaces
- **Audit Trail** — Sidebar showing change history for selected namespace (fetched from all nodes)
- **Auto-refresh** — Polls every 2 seconds

## Running the Dashboard

```bash
cd src/Confman.Dashboard
npm install
npm run dev
```

Opens at `http://localhost:5173` by default.

## Design System

### Aesthetic Direction

**Mission Control / Industrial Control Room** — The dashboard evokes a sophisticated mission control center, combining technical precision with dramatic tension. Every element feels like it's monitoring something critical.

### Typography

| Usage | Font | Weight |
|-------|------|--------|
| Data, code, metrics | IBM Plex Mono | 400-600 |
| UI labels, text | Instrument Sans | 400-700 |

### Color Palette

```css
/* Background layers */
--bg-deep: #0a0c10;      /* Deepest background */
--bg-panel: #12151c;     /* Card/panel background */
--bg-elevated: #1a1e28;  /* Elevated elements */

/* Borders */
--border: #2a3142;       /* Primary borders */
--border-subtle: #1e222d; /* Subtle dividers */

/* Text hierarchy */
--text-primary: #e8eaed;   /* Primary text */
--text-secondary: #8b9ab5; /* Secondary text */
--text-muted: #5a6578;     /* Muted/labels */

/* Status colors */
--accent-cyan: #00d4ff;    /* Primary accent, follower nodes */
--accent-green: #00ff88;   /* Success, healthy, leader nodes */
--accent-amber: #ffb800;   /* Warning, degraded */
--accent-red: #ff3d5a;     /* Error, offline, deleted */
```

### Visual Elements

| Element | Treatment |
|---------|-----------|
| **Status Beacon** | Glowing radial gradient orb with pulsing animation |
| **Node Cards** | Colored top border (green gradient for leader, cyan for follower, red for offline) |
| **Section Headers** | Uppercase monospace with fading gradient line |
| **Tables** | Elevated header row, subtle row borders, cyan/green highlighting |
| **Scrollbars** | Styled thin dark scrollbars |
| **CRT Effect** | Subtle scanline overlay for atmosphere |

### Animations

- **Beacon pulse** — Status orb radiates outward every 2s
- **Node indicators** — Green dots pulse gently when online
- **Hover states** — Cards lift slightly, borders highlight cyan
- **Alert state** — Red beacon flashes when quorum lost

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
| `GET /api/v1/namespaces` | List all namespaces |
| `GET /api/v1/namespaces/{path}/audit` | Audit trail for namespace |

## Status Indicators

### Cluster Summary

| Status | Meaning |
|--------|---------|
| **CLUSTER ONLINE** (green) | All nodes ready, leader elected |
| **DEGRADED PERFORMANCE** (amber) | Quorum exists but not all nodes ready |
| **QUORUM LOST** (red) | Fewer than 2 nodes online, no leader |

### Node Status

| Status | Color | Meaning |
|--------|-------|---------|
| READY | Green | Node reachable, quorum established |
| NO QUORUM | Amber | Node reachable but no leader elected |
| OFFLINE | Red | Node unreachable (network error/timeout) |

### Node Roles

| Role | Card Style |
|------|------------|
| Leader | Green-to-cyan gradient top border |
| Follower | Cyan top border (50% opacity) |
| Unknown/Offline | Red top border |

## Audit Log Behavior

The audit trail fetches from **all nodes** and merges results. This is necessary because:
- Audit events are stored locally in each node's LiteDB
- Events are only created on the leader (to avoid duplicates)
- When leadership changes, historical events remain on the previous leader

The dashboard deduplicates by `timestamp + action + actor + key` and sorts newest first.

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
- **Google Fonts** — IBM Plex Mono, Instrument Sans
- **CSS Custom Properties** — Consistent theming

## Build for Production

```bash
npm run build
```

Output in `dist/` directory. Serve with any static file server.
