---
name: commit
description: Build-verified git commit workflow. Use when the user says "commit", "commit changes", or invokes /commit. Builds the project, shows a diff summary, generates a conventional commit message, asks for confirmation, and updates the changelog.
---

# Commit

A safe, build-verified commit workflow for the Confman project.

## Workflow

Execute these steps in order. Stop and report if any step fails.

### Step 1: Build Verification

Run the .NET build to ensure no compilation errors:

```bash
dotnet build -c Release -q 2>&1 | grep -v "^MSBUILD : error : Building target"
```

If the build fails, show the errors and **stop** — do not commit broken code.

### Step 2: Diff Summary

Show what will be committed:

```bash
git diff --stat
git diff --cached --stat
```

Also run `git status` (without `-uall`) to show untracked files. Present a clean summary of:
- Modified files
- Added files
- Deleted files
- Untracked files (suggest staging relevant ones)

### Step 3: Generate Commit Message

Generate a [Conventional Commits](https://www.conventionalcommits.org/) message based on the actual changes:

- **Format**: `type(scope): description`
- **Types**: `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `chore`, `build`, `ci`
- **Scope**: The primary area affected (e.g., `raft`, `api`, `dashboard`, `cluster`, `bench`)
- **Description**: Imperative mood, lowercase, no period, under 72 chars
- **Body** (if needed): Explain the "why", not the "what" — the diff shows the what

Read the last 5 commit messages with `git log --oneline -5` to match the repository's existing style.

### Step 4: Confirm with User

Present the proposed commit message and the list of files to be staged. Use `AskUserQuestion` to ask the user to confirm or edit the message. Options:

1. **Commit as-is** — proceed with the generated message
2. **Edit message** — let the user provide a custom message
3. **Abort** — cancel the commit

Do NOT commit without explicit user confirmation.

### Step 5: Commit

Stage the relevant files (prefer specific filenames over `git add -A`) and commit:

```bash
git add <specific files>
git commit -m "$(cat <<'EOF'
type(scope): description

Optional body explaining why.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

### Step 6: Update Changelog

After a successful commit, update `CHANGELOG.md` in the repo root. If the file doesn't exist, create it.

#### Format

```markdown
# Changelog

## [Unreleased]

### Added
- Description of new features

### Changed
- Description of changes to existing functionality

### Fixed
- Description of bug fixes

### Performance
- Description of performance improvements

### Removed
- Description of removed features
```

- Append the new entry under `[Unreleased]` in the appropriate subsection
- Use the same description from the commit message body
- Keep entries concise (one line each)
- Do NOT create a separate commit for the changelog update — inform the user that CHANGELOG.md was updated and they can include it in a future commit or amend

## Guidelines

- Never use `git add -A` or `git add .` — always stage specific files
- Never commit files that may contain secrets (`.env`, `credentials.json`, `appsettings.*.json` with connection strings)
- Never skip pre-commit hooks (`--no-verify`)
- Never amend a previous commit unless the user explicitly asks
- If the build step reveals warnings (not errors), note them but proceed
- The MSBuild "CoreCompile" message is a false alarm — ignore it
