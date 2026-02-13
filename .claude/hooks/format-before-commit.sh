#!/bin/bash
INPUT=$(cat)
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

# Only intercept git commit commands
if ! echo "$COMMAND" | grep -qE '^git\s+commit'; then
  exit 0
fi

# Run dotnet format on the API project (scoped, not whole solution)
dotnet format src/Confman.Api --no-restore 2>&1
FORMAT_EXIT=$?

if [ $FORMAT_EXIT -ne 0 ]; then
  echo "dotnet format failed. Fix errors before committing." >&2
  exit 2  # Block the commit
fi

# If format changed any files, stage them so the commit includes fixes
if ! git diff --quiet; then
  echo "dotnet format fixed style issues — re-staging affected files."
  git add -u
fi

exit 0
