---
name: cluster-insights
description: Record and display Raft cluster performance insights. Use when the user says "remember this insight" to append a new insight, or "what are the insights" / "what are the performance insights" to display all recorded insights.
---

# Cluster Insights

Maintain a living knowledge base of Raft cluster performance insights in `CLUSTER_INSIGHTS.md` (co-located with this skill).

## File Location

The insights file is at: `.claude/skills/cluster-insights/CLUSTER_INSIGHTS.md`

## Recording an Insight ("remember this insight")

When the user says "remember this insight" (or similar: "add this insight", "note this insight", "save this insight"):

1. Read the current `CLUSTER_INSIGHTS.md` file.
2. Determine the next insight number from the existing table.
3. From the conversation context, extract:
   - **Area**: Short label (2-3 words, bold) describing the category
   - **Insight**: Technical description of what was discovered
   - **Impact**: What goes wrong if this isn't addressed
   - **Fix/Note**: What was done about it (or "Awareness — no code fix" for architectural limits)
4. Append a new row to the `## Insights` table.
5. If the insight relates to an existing reference section (Tuning Guidelines, Write Amplification), update that section too.
6. Confirm to the user what was recorded.

## Displaying Insights ("what are the insights")

When the user asks "what are the insights", "what are the performance insights", "show insights", or similar:

1. Read `CLUSTER_INSIGHTS.md`.
2. Display the full insights table formatted nicely.
3. Include any reference sections (Tuning Guidelines, Write Amplification Breakdown) that exist.

## Guidelines

- Keep insight descriptions concise but technically precise — someone reading this months later should understand the issue without needing additional context.
- Use backtick formatting for code references (class names, config keys, method names).
- Bold the Area column for scanability.
- If an insight supersedes or refines an earlier one, update the existing row rather than adding a duplicate.
