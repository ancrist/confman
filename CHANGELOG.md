# Changelog

## [Unreleased]

### Added
- `--tmux` flag to `cluster.sh` for auto-opening 3-pane log tail after cluster start
- `/commit` skill: build-verified git commit workflow with conventional commits
- `/benchmark` skill: cluster benchmarking workflow with leader discovery
- `/debug-cluster` skill: autonomous 5-iteration debug-fix-test loop for Raft bugs
- Python, .NET, debugging, and interaction style guidelines in CLAUDE.md

### Performance
- Increase SnapshotInterval from 100 to 300 to reduce snapshot starvation under load
