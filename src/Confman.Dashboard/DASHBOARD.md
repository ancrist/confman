# Confman Cluster Dashboard

A lightweight web UI for monitoring the Confman Raft cluster.

## Features

- **Cluster Health Indicator** — Animated status showing INCEPT NOMINAL, NEXUS UNSTABLE, or BASELINE FAILED
- **Replicant Cards** — Real-time status for each cluster node showing:
  - Connection state (online/offline)
  - Raft role (Leader/Follower)
  - Current epoch (term)
  - Colored side border indicating role
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

---

## Nexus-6 Design System (Current)

### Aesthetic Direction

**Blade Runner 1982 Terminal** — Inspired by the computer interfaces in Ridley Scott's Blade Runner, the dashboard evokes a retro-futuristic CRT terminal aesthetic. Amber phosphor monochrome with subtle scanlines, boxy wireframe panels, and uppercase text throughout. The terminology references the Nexus-6 replicants and Voight-Kampff empathy tests from the film.

### Typography

| Usage | Font | Style |
|-------|------|-------|
| Primary UI text | VT323 | Chunky terminal/CRT look |
| Data, URLs, code | Share Tech Mono | Technical monospace |

All text is displayed in **UPPERCASE** for that authentic 1982 terminal feel.

### Color Palette

```css
/* Background layers */
--bg-black: #0a0806;     /* Deep black with warm tint */
--bg-panel: #0d0b08;     /* Panel background */

/* Amber phosphor palette */
--border-amber: #ff9500;  /* Bright amber borders */
--border-dim: #4a3800;    /* Dim amber borders */
--text-bright: #ffb800;   /* Bright amber text */
--text-amber: #ff9500;    /* Primary amber text */
--text-dim: #8a6a00;      /* Dim/secondary text */
--text-dark: #3a2a00;     /* Darkest text/borders */

/* Status colors */
--green-bright: #39ff14;  /* Online/success (neon green) */
--red-bright: #ff3131;    /* Offline/error (bright red) */

/* Glow effects */
--glow-amber: 0 0 10px #ff9500, 0 0 20px rgba(255, 149, 0, 0.5);
--glow-text: 0 0 8px rgba(255, 184, 0, 0.6);
```

### Visual Elements

| Element | Treatment |
|---------|-----------|
| **Status Indicator** | Square with inner fill, blinking animation, colored glow |
| **Replicant Cards** | Left border glow (green=leader, amber=follower, red=offline) |
| **Panel Headers** | Bracket syntax: `[ REPLICANTS ]` with gradient decorator line |
| **Tables** | Dark header row, amber text glow, selection highlight |
| **Scrollbars** | Thin, amber-tinted with border |
| **CRT Effects** | Subtle scanline overlay, edge vignette darkening |
| **Empty States** | Bracket icons: `[?]`, `[!]`, `[...]`, `[0]` |

### Animations

- **Status blink** — Square indicator blinks with stepped animation
- **Text glow** — Phosphor bloom effect on bright text
- **Hover states** — Amber background tint on interactive rows
- **Selection** — Left border highlight with background tint

### Terminology (Blade Runner References)

| UI Term | Meaning | Reference |
|---------|---------|-----------|
| **REPLICANT-1/2/3** | Cluster nodes | Nexus-6 replicants |
| **INCEPT NOMINAL** | All nodes healthy | Replicant incept dates |
| **NEXUS UNSTABLE** | Degraded performance | Nexus-6 model designation |
| **BASELINE FAILED** | Quorum lost | Baseline test from BR 2049 |
| **VOIGHT-KAMPFF** | Example namespace | Empathy test from BR 1982 |

### Status Indicators

| Status | Color | Meaning |
|--------|-------|---------|
| **INCEPT NOMINAL** | Green | All replicants online, leader elected |
| **NEXUS UNSTABLE** | Amber | Quorum exists but not all nodes ready |
| **BASELINE FAILED** | Red | Fewer than 2 nodes online, no leader |

### Replicant Roles

| Role | Card Style |
|------|------------|
| Leader | Green left border with glow |
| Follower | Amber left border with glow |
| Unknown/Offline | Red left border with glow |

---

## Mission Control Design System (Previous)

> **Note:** This design system was the previous iteration, kept here for reference.

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

### Status Indicators

| Status | Meaning |
|--------|---------|
| **CLUSTER ONLINE** (green) | All nodes ready, leader elected |
| **DEGRADED PERFORMANCE** (amber) | Quorum exists but not all nodes ready |
| **QUORUM LOST** (red) | Fewer than 2 nodes online, no leader |

---

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
│REPLICANT-1│ │REPLICANT-2│ │REPLICANT-3│
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
    { name: 'REPLICANT-1', url: 'http://127.0.0.1:6100' },
    { name: 'REPLICANT-2', url: 'http://127.0.0.1:6200' },
    { name: 'REPLICANT-3', url: 'http://127.0.0.1:6300' }
];
```

## Tech Stack

- **Vite** — Development server and build tool
- **Vanilla JS** — No framework dependencies
- **Google Fonts** — VT323, Share Tech Mono
- **CSS Custom Properties** — Consistent theming

## Build for Production

```bash
npm run build
```

Output in `dist/` directory. Serve with any static file server.
