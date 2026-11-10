---
description: |
  Answers review feedback on honua-server pull requests where it happens: when a
  reviewer (human or review bot) leaves a comment or review, it fixes valid
  findings on the pull request branch and replies to each one.

on:
  pull_request_review:
    types: [submitted]
  pull_request_review_comment:
    types: [created]
  bots: ["chatgpt-codex-connector[bot]", "copilot-pull-request-reviewer[bot]", "github-actions[bot]"]

permissions:
  contents: read
  issues: read
  pull-requests: read

tools:
  github:
    toolsets: [default]

engine:
  id: claude
  model: opus

max-ai-credits: 300
timeout-minutes: 30

concurrency:
  group: pr-review-doctor-${{ github.event.pull_request.number }}
  cancel-in-progress: true

safe-outputs:
  push-to-pull-request-branch:
    max: 1
    if-no-changes: "ignore"
    check-branch-protection: false
    protected-files: fallback-to-issue
    excluded-files:
      - ".github/**"
      - "certification/**"
      - "**/*.sealed"
  add-comment:
    max: 1
  add-labels:
    allowed: [review-stuck]
    max: 1
---

# Review doctor

A review was left on pull request #${{ github.event.pull_request.number }} in ${{ github.repository }}. Make every unresolved review thread actionable now.

## Steps

1. List the pull request's unresolved review threads. Ignore threads that already have a reply from this workflow after the latest comment, and ignore resolved threads.
2. For each thread decide: valid and in scope (a real correctness, safety, test or clean-code problem in the changed code), or not (wrong, out of scope, or a matter of taste).
3. Valid ones: make the smallest fix that addresses the finding, following AGENTS.md (clean-code rules, no history or ticket comments; never delete, skip or weaken a test). Add or update a test when the finding is a behaviour bug. Push all fixes as one commit `fix(review): <summary>` to the pull request branch.
4. Add one comment on the pull request that lists every thread you handled: the thread's file and line, "fixed in <short sha>" or "not changed: <one-sentence reason>". The pull request's owner resolves the threads after checking.
5. If a finding needs a design decision, touches security, authentication, approval, licensing or release logic, or the fix would exceed a few files: change nothing for it, say so in the comment, and add the `review-stuck` label.

## Rules

- Never print secrets, tokens or environment values. Never open certification/ai-arc/heldout*.sealed or certification/agent-e2e/pack.v2.json.
- Never change files under .github/, never merge, approve or resolve threads.
- This repository is public: describe problems by behaviour; no exploit detail.
