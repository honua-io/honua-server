#!/usr/bin/env bash
# autofix-agent.sh — the TRAIN_AUTOFIX_STEP_CMD wrapper the orchestrator invokes
# to run the roll-forward AI fix-agent (Claude via Bedrock). Called as:
#     autofix-agent.sh <batch-branch> <request-file>
#
# It runs the Claude Code agent HEADLESS in Bedrock mode against the request
# prompt, with the batch branch already checked out, so the agent edits + commits
# the fix in place. This is the same engine anthropics/claude-code-action wraps;
# we invoke the CLI directly so it composes inside train.sh's loop (a GitHub
# composite action cannot be called mid-bash-step).
#
# Bedrock wiring (mirrors anthropics/claude-code-action docs/cloud-providers.md):
#   * CLAUDE_CODE_USE_BEDROCK=1               — route the model to AWS Bedrock.
#   * AWS creds: supplied by the workflow via aws-actions/configure-aws-credentials
#     (the BEDROCK_AWS_* access-key secrets) into the ambient AWS_* env.
#   * AWS_REGION=us-west-2                     — the Bedrock region (overridable).
#   * ANTHROPIC_MODEL=<TRAIN_AUTOFIX_MODEL>    — a Sonnet-class fix model.
#
# ASSUMPTION (stated for review): the `claude` CLI is on PATH in the workflow
# (installed by the autofix step, e.g. `npm i -g @anthropic-ai/claude-code`), and
# honors CLAUDE_CODE_USE_BEDROCK + ANTHROPIC_MODEL + the AWS chain, matching the
# action's documented Bedrock behavior. If the CLI is absent, this wrapper exits
# 0 WITHOUT committing, so train_autofix_committed sees no new commit and the
# train safely falls back to escalation (never wedges).
#
# This wrapper makes NO commit of its own — the agent commits. The orchestrator
# (train.sh) detects the new commit via train_autofix_committed.

set -uo pipefail

BATCH="${1:?batch branch required}"
REQUEST_FILE="${2:?request file required}"

REPO_ROOT="${TRAIN_REPO_ROOT:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"

# Ensure the batch branch is checked out so the agent edits the right tree.
git -C "${REPO_ROOT}" checkout -q "${BATCH}" 2>/dev/null || true

if ! command -v claude >/dev/null 2>&1; then
  echo "::warning::autofix-agent: 'claude' CLI not found on PATH; skipping AI fix (train will escalate)." >&2
  exit 0
fi

# Bedrock routing for the headless agent.
export CLAUDE_CODE_USE_BEDROCK=1
export AWS_REGION="${AWS_REGION:-us-west-2}"
export ANTHROPIC_MODEL="${TRAIN_AUTOFIX_MODEL:-us.anthropic.claude-sonnet-4-5-20250929-v1:0}"

# Run headless. --print streams the agent's response; --permission-mode
# acceptEdits lets it edit + commit without interactive approval. The request
# file is the full fix-forward prompt (failing tests + error output + batch diff
# + the "commit as Mike McDougall, no bot attribution, touch only what's needed"
# instructions). We cap wall time so a hung agent can't stall the batch.
PROMPT="$(cat "${REQUEST_FILE}")"
TIMEOUT="${TRAIN_AUTOFIX_TIMEOUT:-900}"

( cd "${REPO_ROOT}" && \
  timeout "${TIMEOUT}" claude \
    --print \
    --permission-mode acceptEdits \
    --allowed-tools "Edit,Write,Read,Bash" \
    "${PROMPT}" ) \
  || echo "::warning::autofix-agent: claude headless run exited non-zero or timed out; train will check for a partial commit." >&2

# Intentionally exit 0: success/failure is judged by whether a NEW commit landed
# on the batch branch (train_autofix_committed), not by this wrapper's rc.
exit 0
