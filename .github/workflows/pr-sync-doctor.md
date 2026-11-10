---
description: |
  Keeps honua-server pull requests mergeable: when trunk moves, it merges trunk
  into open pull requests that now conflict, and into green pull requests that
  have fallen behind, resolving conflicts.

on:
  push:
    branches: [trunk]
  schedule:
    - cron: "17 * * * *"
  workflow_dispatch:

permissions:
  contents: read
  pull-requests: read

tools:
  github:
    toolsets: [default]
  bash: [":*"]

engine:
  id: claude
  model: opus

max-ai-credits: 400
timeout-minutes: 40

concurrency:
  group: pr-sync-doctor
  cancel-in-progress: false

safe-outputs:
  push-to-pull-request-branch:
    target: "*"
    max: 2
    if-no-changes: "ignore"
    check-branch-protection: false
    protected-files: fallback-to-issue
    excluded-files:
      - ".github/**"
      - "certification/**"
      - "**/*.sealed"
  add-comment:
    target: "*"
    max: 2
  add-labels:
    allowed: [conflict-stuck]
    target: "*"
    max: 2
---

# Sync doctor

Keep open pull requests in ${{ github.repository }} mergeable with trunk.

## Steps

1. List open, non-draft pull requests from branches in this repository. Pick at most two, in this order: first those whose mergeable state is conflicting; then those whose required checks are all green, whose branch is behind trunk, AND where trunk's commits since the branch point change files in the same projects (directories containing a .csproj, or the same top-level folder for non-.NET files) as the pull request (oldest first). Leave behind-but-unaffected pull requests alone. Skip pull requests labelled `conflict-stuck`.
2. For each: check out its head branch, merge origin/trunk into it, and resolve conflicts by keeping both sides' intent: keep trunk's moves and renames, re-apply the pull request's changes on top, regenerate generated files with the repository's own scripts when a generated file conflicts, and union allowlist-style JSON entries.
3. Build check: run `dotnet build` only for projects whose files you changed while resolving (if the toolchain is available); never weaken tests to resolve a conflict.
4. Push the merge result to that pull request's branch, and add one comment: "Merged trunk <short sha>; conflicts in <files> resolved by <one line each>."
5. If a conflict needs a design decision (both sides changed the same behaviour differently), touches security, authentication, approval, licensing or release logic, or spans more than a handful of files: push nothing for it, add a comment naming the conflicting files and the decision needed, and add the `conflict-stuck` label.

## Rules

- Never print secrets, tokens or environment values. Never open certification/ai-arc/heldout*.sealed or certification/agent-e2e/pack.v2.json.
- Never change files under .github/, never merge pull requests, approve or resolve threads.
