---
name: debug-cluster
description: Autonomous debug-fix-test loop for Raft cluster bugs. Use when the user reports a cluster bug and wants autonomous investigation. Accepts a symptom description as argument. Iterates up to 5 times using sub-agents to reproduce, diagnose, fix, and verify — without asking for guidance between iterations.
---

# Debug Cluster

Autonomous debug-fix-test loop for Confman Raft cluster bugs. Runs up to 5 iterations of reproduce → diagnose → fix → verify, stopping only when all tests pass or attempts are exhausted.

## Input

The user provides:
- **Symptom**: What's going wrong (passed as the skill argument, e.g., `/debug-cluster leader loses election under load`)
- **Reproduction steps** (optional): How to trigger the bug

If no reproduction steps are given, infer them from the symptom and cluster logs.

## Iteration Loop

Run up to **5 iterations**. Each iteration has 3 phases executed via sub-agents. Do NOT ask the user for guidance between iterations.

### Phase 1: Reproduce (Agent 1 — Bash agent)

Start the cluster and capture logs:

1. Stop any running cluster: `.claude/skills/run-cluster/scripts/cluster.sh stop`
2. Wipe stale data: `.claude/skills/run-cluster/scripts/cluster.sh wipe`
3. Start fresh: `.claude/skills/run-cluster/scripts/cluster.sh start`
4. Wait for quorum (the script does this automatically)
5. Execute the reproduction steps (API calls, benchmark load, etc.)
6. Capture logs from `/tmp/confman-node1.log`, `/tmp/confman-node2.log`, `/tmp/confman-node3.log`
7. Run `dotnet test` to capture current test state

On **iteration 2+**, skip the wipe/start if the cluster is already running and the fix doesn't require a restart. Use judgement.

### Phase 2: Diagnose & Fix (Agent 2 — General-purpose agent)

Analyze logs and apply a minimal fix:

1. Read the captured logs from Phase 1 (focus on errors, warnings, exceptions)
2. Read relevant source files based on the error traces
3. Form a hypothesis about the root cause
4. **Present 2-3 ranked hypotheses** (even though the user won't see them mid-loop, log them for the final summary)
5. Apply a **minimal** code fix — change as few lines as possible
6. Do NOT refactor surrounding code, add features, or "improve" anything beyond the fix
7. Run `dotnet build -c Release -q 2>&1 | grep -v "^MSBUILD : error : Building target"` to verify the fix compiles

If the build fails, fix the compilation error before proceeding.

### Phase 3: Verify (Agent 3 — Bash agent)

Re-run tests and optionally the reproduction scenario:

1. Run `dotnet test` — all 66+ tests must pass
2. If the bug has a specific reproduction scenario, re-run it against the cluster
3. Collect pass/fail results and any new log output

### Iteration Decision

After Phase 3:
- **All tests pass AND reproduction scenario succeeds** → STOP, proceed to final summary
- **Tests fail or bug reproduces** → log what happened, start next iteration
- **5 iterations exhausted** → STOP, proceed to final summary with partial results

## Iteration Log

Maintain a structured log across iterations. After each iteration, record:

```
## Iteration N

### Hypothesis
[What you think is wrong and why]

### Changes Made
[Files modified, lines changed, brief description]

### Test Results
- dotnet test: PASS/FAIL (N passed, M failed)
- Reproduction: PASS/FAIL
- New errors observed: [any]

### Decision
[Continue / Stop — and why]
```

## Final Summary

When the loop ends (success or exhaustion), present:

### 1. Root Cause
Clear explanation of what caused the bug. If unresolved, state the most likely hypothesis and what evidence supports/contradicts it.

### 2. Fix Diff
Show `git diff` of all changes made. If multiple iterations made changes, show the cumulative diff.

### 3. Before/After Test Output
- Test results before any fix was applied (from iteration 1, phase 1)
- Test results after the final fix (from the last iteration, phase 3)

### 4. Iteration History
The full iteration log from above.

### 5. Confidence Level
Rate confidence in the fix: **High** (root cause confirmed, tests green), **Medium** (tests green but root cause is a hypothesis), **Low** (tests still failing or fix is a workaround).

## Key Project Context

Provide this context to all sub-agents:

- **Cluster ports**: 6100, 6200, 6300
- **Cluster script**: `.claude/skills/run-cluster/scripts/cluster.sh start|stop|status|wipe`
- **Log files**: `/tmp/confman-node1.log`, `/tmp/confman-node2.log`, `/tmp/confman-node3.log`
- **Test command**: `dotnet test`
- **Build command**: `dotnet build -c Release -q 2>&1 | grep -v "^MSBUILD : error : Building target"`
- **Source**: `src/Confman.Api/` (API, Raft, storage), `tests/Confman.Tests/` (unit tests)
- **Key files**: `ConfigStateMachine.cs`, `RaftService.cs`, `LiteDbConfigStore.cs`, `ClusterLifetime.cs`
- **Known flaky test**: `SetNamespaceCommand_UpdatesNamespace_WithAudit` — timestamp tie, re-run passes
- **MSBuild noise**: "Building target CoreCompile completely" is a false alarm, ignore it
- **DotNext is a local project reference** — changes to DotNext source are possible but should be a last resort
- **Election timeout**: 1000-3000ms — snapshot_time must stay below this
- **LiteDB semaphore**: `SemaphoreSlim(1)` serializes all DB access — this is intentional

## Guidelines

- **Minimal fixes only** — do not refactor, add features, or clean up code
- **Never blame DotNext without evidence** — read the actual source before hypothesizing about library bugs
- **Check the simple things first** — configuration errors, typos, wrong ports, missing null checks
- **Preserve the iteration log** — this is the user's audit trail of what was tried
- **If stuck after 3 iterations on the same hypothesis**, pivot to a different theory
- **Use `uv`** for any Python dependencies, never pip
