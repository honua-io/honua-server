# Merge coordination runbook (webhook-driven)

Merge-queue coordination on `honua-server` is **fully event-driven (webhook)**.
Session-side polling â€” a Claude `/loop` heartbeat or a `gh`-polling Monitor babysitting
the queue â€” is **no longer required to drain the queue**. Three workflows cooperate, all
triggered by GitHub events, none needing a live session:

| Workflow | Trigger | Job |
|---|---|---|
| `merge-train.yml` | 15-minute schedule or explicit live dispatch | Sole merge authority: exact-head admission, batch assembly/CI, and compare-and-swap landing. |
| `auto-rerun-flaky.yml` | `workflow_run` (CI) completed, `run_attempt == 1` | Gives a PR's CI exactly **one** retry when only known-flaky shards failed (40P01 deadlock, Testcontainers/Docker). Never reruns a real gate. |
| `ci-failure-triage.yml` | `workflow_run` (CI) completed, `conclusion == failure` | The missing piece (#2021): **AI triage of genuinely-real failures** + autonomous rerun-orchestration backstop. Does *not* merge â€” that stays in the train. |

## What `ci-failure-triage.yml` does

1. Resolves the associated PR(s) from the CI run's head SHA (incl. fork PRs, via a head-SHA search).
2. Runs the **shared deterministic classifier** (`scripts/ci/ci-failure-classifier.js`) â€” the
   single source of the leaf/aggregator + FLAKY-shard/SOLID-gate regex sets, also consumed by
   `auto-rerun-flaky.yml` so the two can never drift.
3. Acts on the verdict:
   - **clean** (only aggregator roll-ups failed) â†’ nothing; the train handles merge/freshen.
   - **flake-only** â†’ ensures a rerun happened **at most once** (only on `run_attempt == 1`,
     after `auto-rerun-flaky` owns the first attempt â€” this is the backstop; a second rerun of
     the same attempt is rejected by GitHub, which is the desired "no storms" behavior).
   - **real-failure** (a SOLID gate failed, or an unrecognized leaf) â†’ gathers the failing
     job's log tail, calls **Bedrock** for a triage verdict `{classification, rootCause,
     suggestedAction}`, posts it as a PR comment, and applies the `ci-needs-triage` label
     (created on first use). **AI is used only here.**
4. Skips PRs carrying the `hold` label (same escape hatch the train respects). Concurrency is
   serialized per head branch (`cancel-in-progress: false`). Uses `secrets.MERGE_TRAIN_TOKEN`
   so it can comment/label PRs whose delta touches `.github/workflows/**`.

## Bedrock auth in CI â€” and graceful degradation

The triage AI step talks to Claude on **Amazon Bedrock** via the **Converse API** using the
standard **AWS credential chain** â€” no API key. Today there is **no CI Bedrock credential path**
(the only `aws-actions/configure-aws-credentials` usage in the repo is in `deploy.yml` /
`deploy-platform-images.yml`, with static ECR push keys, not Bedrock). So the AI step
**degrades gracefully**: when no creds are present it skips the model call, still labels the PR
`ci-needs-triage`, and posts a "triage skipped" notice instead of hard-failing.

To **enable** AI triage, add these repository (or environment) **variables** + an OIDC role:

| Setting | Kind | Purpose |
|---|---|---|
| `BEDROCK_TRIAGE_ROLE_ARN` | repo variable | IAM role for GitHub OIDC to assume; needs `bedrock:InvokeModel` on the chosen model/profile. When set, the workflow runs `configure-aws-credentials` (OIDC, `id-token: write`) and installs `@aws-sdk/client-bedrock-runtime`. |
| `BEDROCK_TRIAGE_REGION` | repo variable | Bedrock region (default `us-west-2`). |
| `BEDROCK_TRIAGE_MODEL` | repo variable | Bedrock model id / cross-region inference profile (e.g. `us.anthropic.claude-sonnet-4-5-20250929-v1:0`). Defaults to that profile if unset. |

The IAM trust policy must allow `token.actions.githubusercontent.com` for this repo. This mirrors
how the AI studio flows authenticate to Bedrock (see `docs/guides/run-studio-ai-on-bedrock.md`):
Converse API + IAM credential chain, model is a Bedrock id / inference profile.

## Testing & validation

- `scripts/ci/ci-failure-classifier.js` is unit-tested (`node --test scripts/ci/ci-failure-classifier.test.js`)
  against flake-only / real-gate / mixed / unknown job-name sets â€” proving it matches
  `auto-rerun-flaky`'s intent.
- `scripts/ci/bedrock-triage.js` returns `available: false` with a notice when no AWS creds are
  present (graceful skip), so the workflow is safe with Bedrock unconfigured.
- `workflow_run`-triggered workflows only execute once on the default branch, so live validation
  is **post-merge** (the same as `auto-rerun-flaky` when it landed). First-firing watch plan: on
  the next red PR-CI, confirm (a) flake-only runs get at most one rerun, (b) a real gate failure
  gets the `ci-needs-triage` label + a triage comment, and (c) with no Bedrock role set, the
  comment is the graceful-skip notice (not a job failure).
