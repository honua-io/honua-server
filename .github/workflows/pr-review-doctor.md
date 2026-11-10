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

checkout:
  fetch-depth: 0

steps:
  - name: Preserve doctor commit messages through excluded-file filtering
    run: python3 scripts/ci/preserve-doctor-commit-message.py "${RUNNER_TEMP}/gh-aw/actions/generate_git_patch.cjs" "${RUNNER_TEMP}/gh-aw/safeoutputs/generate_git_patch.cjs"
  - name: Collect review threads from trusted reviewers
    env:
      GH_TOKEN: ${{ github.token }}
      PR_NUMBER: ${{ github.event.pull_request.number }}
      REVIEW_BOTS: '["chatgpt-codex-connector", "copilot-pull-request-reviewer", "github-actions", "honua-io-bot"]'
    run: |
      mkdir -p /tmp/gh-aw/review
      pr_args=(-F owner="${GITHUB_REPOSITORY%/*}" -F repo="${GITHUB_REPOSITORY#*/}" -F number="$PR_NUMBER")
      gh api graphql "${pr_args[@]}" -f query='
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) { pullRequest(number: $number) {
            reviews(last: 100) { nodes { author { login } authorAssociation state body } }
          } }
        }' > "$RUNNER_TEMP/reviews.json"
      gh api graphql --paginate "${pr_args[@]}" -f query='
        query($owner: String!, $repo: String!, $number: Int!, $endCursor: String) {
          repository(owner: $owner, name: $repo) { pullRequest(number: $number) {
            reviewThreads(first: 100, after: $endCursor) {
              pageInfo { hasNextPage endCursor }
              nodes { isResolved isOutdated path line
                comments(last: 100) { nodes { databaseId author { login } authorAssociation body createdAt } } }
            }
          } }
        }' > "$RUNNER_TEMP/threads.json"
      jq -n --argjson bots "$REVIEW_BOTS" --slurpfile reviews "$RUNNER_TEMP/reviews.json" --slurpfile pages "$RUNNER_TEMP/threads.json" '
        def trusted: (.authorAssociation | IN("OWNER", "MEMBER", "COLLABORATOR")) or ((.author.login // "") | IN($bots[]));
        def entry: {author: .author.login} + (del(.author, .authorAssociation));
        {reviews: [$reviews[0].data.repository.pullRequest.reviews.nodes[] | select(trusted) | entry],
         threads: [$pages[].data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved | not)
           | .comments = [.comments.nodes[] | select(trusted) | entry]
           | select(.comments | length > 0) | del(.isResolved)]}' > /tmp/gh-aw/review/threads.json

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
  cancel-in-progress: false
  queue: max

safe-outputs:
  github-app:
    client-id: ${{ secrets.BOT_APP_ID }}
    private-key: ${{ secrets.BOT_APP_PRIVATE_KEY }}
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

## Code and threads

- The pull request's head branch is already checked out at `$GITHUB_WORKSPACE` with full history and `origin/trunk`. The sandbox has no network for git: never run `git fetch`, `git pull` or `git push`. Read the change with `git diff origin/trunk...HEAD`. `git status` shows `.github/` and agent-instruction files restored from the base branch: leave them and commit only the files you change.
- `/tmp/gh-aw/review/threads.json` holds the pull request's reviews and unresolved threads (file, line, comment ids and bodies) from owners, members, collaborators and the review bots. Read threads from it: the GitHub tools withhold review threads.

## Steps

1. List the unresolved review threads from `threads.json`. Ignore threads that already have a reply from this workflow (author `honua-io-bot`, or `github-actions` in older runs) after the latest comment, and ignore resolved threads.
2. For each thread decide: valid and in scope (a real correctness, safety, test or clean-code problem in the changed code), or not (wrong, out of scope, or a matter of taste).
3. Valid ones: make the smallest fix that addresses the finding, following AGENTS.md (clean-code rules, no history or ticket comments; never delete, skip or weaken a test). Add or update a test when the finding is a behaviour bug. Commit all fixes as one commit `fix(review): <summary>` and call `push_to_pull_request_branch`. Clean-code and small test fixes are yours to make: push them rather than only suggesting them.
4. Add one comment on the pull request that lists every thread you handled: the thread's file and line, "fixed in <short sha>" or "not changed: <one-sentence reason>". The pull request's owner resolves the threads after checking.
5. If a finding needs a design decision, touches security, authentication, approval, licensing or release logic, or the fix would exceed a few files: change nothing for it, say so in the comment, and add the `review-stuck` label.

## Rules

- Never print secrets, tokens or environment values. Never open certification/ai-arc/heldout*.sealed or certification/agent-e2e/pack.v2.json.
- Never change files under .github/, never merge, approve or resolve threads.
- This repository is public: describe problems by behaviour; no exploit detail.
