---
description: |
  Fixes failed CI runs on honua-server where they happen. A failed required check
  on a pull request gets one fix attempt pushed to that pull request's branch; a
  failed trunk run gets a draft fix or revert pull request. Anything it cannot fix
  is labelled ci-stuck.

on:
  workflow_run:
    workflows: ["CI", "PR Gate", "Clean code", "Repository hygiene"]
    types: [completed]
    branches: [trunk, "fix/**", "feat/**", "test/**", "chore/**", "ci/**", "docs/**", "refactor/**", "perf/**"]

if: ${{ github.event.workflow_run.conclusion == 'failure' && github.event.workflow_run.head_repository.full_name == github.repository }}

permissions:
  actions: read
  contents: read
  issues: read
  pull-requests: read

tools:
  github:
    toolsets: [default, actions]

engine:
  id: claude
  model: opus

max-ai-credits: 300
timeout-minutes: 30

concurrency:
  group: ci-doctor-${{ github.event.workflow_run.head_sha }}
  cancel-in-progress: true

safe-outputs:
  github-app:
    app-id: ${{ secrets.HONUA_RELEASE_APP_ID }}
    private-key: ${{ secrets.HONUA_RELEASE_APP_PRIVATE_KEY }}
  push-to-pull-request-branch:
    target: "*"
    max: 1
    if-no-changes: "ignore"
    protected-files: fallback-to-issue
    excluded-files:
      - ".github/**"
      - "certification/**"
      - "**/*.sealed"
  create-pull-request:
    title-prefix: "fix(ci): "
    labels: [ci-doctor]
    draft: true
    base-branch: trunk
    max: 1
    protected-files: fallback-to-issue
  add-labels:
    allowed: [ci-stuck, ci-doctor]
    target: "*"
    max: 1
  add-comment:
    target: "*"
    max: 1
---

# CI doctor

A required workflow failed on honua-server. Find the cause and fix it, so nobody has to notice.

## Run

- Workflow run id: ${{ github.event.workflow_run.id }} in ${{ github.repository }}
- Head SHA: ${{ github.event.workflow_run.head_sha }}
- Look up the run's branch, event and pull request from the run id.

## Steps

0. First check whether this head SHA was already handled: an open pull request comment or issue containing `ci-doctor: <head SHA>`, or a draft pull request titled `fix(ci):` naming it. If so, stop with no output.
1. List the failed jobs and read the earliest failing step's log. Find the first real error, not its consequences.
2. Decide the class: code or test failure, generated-file or format drift, budget check (descriptor, shard time), workflow/config, runner or network flake, external service.
3. Runner, network or GitHub flake: change nothing; add one comment on the pull request (or nothing for trunk) saying it is a flake, so a rerun is enough, ending with the marker `ci-doctor: <head SHA>`.
4. Pull request run (the branch has an open pull request in this repository):
   - Check out the pull request head. Make the smallest change that fixes the failure, following AGENTS.md (clean-code rules, no history or ticket comments, tests stay meaningful: never delete, skip or weaken a test or a budget to go green).
   - Push it to that pull request's branch with a commit message `fix(ci): <what>`.
   - Add one short comment: the cause, the fix, and the log line that proves it, ending with the marker `ci-doctor: <head SHA>`.
   - If the fix is not clear, is larger than a few files, or touches security, authentication, approval, licensing or release logic: change nothing, add the `ci-stuck` label and a comment with the cause and the suggested fix.
5. Trunk run (branch trunk, event schedule, push or workflow_dispatch):
   - Find the merges since the last green CI run on trunk and identify the one that broke it.
   - Open a draft pull request against trunk with the fix; if the fix is not clear, open a draft pull request that reverts the breaking merge (`git revert -m 1 <merge>`), naming the pull request it reverts.
6. Only one fix attempt per head SHA. If this head SHA was produced by an earlier ci-doctor fix, do not push again: add `ci-stuck` instead.

## Rules

- Never print secrets, tokens or environment values. Never open certification/ai-arc/heldout*.sealed or certification/agent-e2e/pack.v2.json.
- Never change files under .github/, never merge, never approve, never resolve review threads.
- This repository is public: describe failures by behaviour; no exploit detail.
