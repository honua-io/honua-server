# Review-first PR Gate

Tracking: [#3216](https://github.com/honua-io/honua-server/issues/3216), under
the evidence-driven CI program [#3213](https://github.com/honua-io/honua-server/issues/3213).

## Why this exists

The required `PR Gate` used to start its .NET build and tests on every pushed
head. Exact-head review frequently requested another commit, so the repository
paid for verification that could not be reused. The baseline attached to #3213
found 22 canceled runs among the latest 30 PR Gate runs.

Review-first keeps the same required context and separates its cheap admission
from expensive verification:

1. A `pull_request` run creates the `PR Gate` check and performs admission.
2. Exact-head Codex evidence is evaluated by `Review Gate Attestation`, using
   only the default branch's policy code.
3. The trusted workflow selects one completed PR Gate run for the current PR
   head. In enforce mode, it releases that run with `rerun-failed-jobs`.
4. Attempt 2 performs the existing build, format, fast/architecture tests, and
   serving-boundary fixtures. Only this attempt can turn the required check
   green.

No new check name or merge authority is introduced.

## Modes

`REVIEW_FIRST_MODE` appears once in each of `pr-gate.yml` and
`review-gate.yml`. `scripts/ci/validate-review-first-dispatch.sh` rejects drift.

| Mode | PR Gate attempt 1 | Review Gate after exact review | PR Gate attempt 2 |
|---|---|---|---|
| `observe` | Admission and current eager verification | Records `observe`; no Actions mutation | Not created by review-first |
| `enforce` | Admission only; intentionally fails at `Await exact-head review` | Reruns the exact failed job once | Runs expensive verification |

Rollout begins in `observe`. After at least 20 representative PR heads and the
#3216 invariants are verified, switch both values to `enforce` in one reviewed
commit. Collect at least 30 post-change heads before judging the latency and
runner-minute target from #3213.

## Trust boundary

- Attempt 1 and attempt 2 are ordinary `pull_request` workflows with read-only
  repository/package permissions. They may execute PR code but have no trusted
  write credential.
- `Review Gate Attestation` has `actions: write`, but checks out only the default
  branch, persists no checkout credential, and executes no PR-authored code.
- Exact-head review, unresolved threads, draft/hold/escalation state, pagination,
  workflow identity, event type, head SHA, PR association, run identity, run
  attempt, admission receipt, wait receipt, and skipped expensive steps all fail
  closed before an enforced rerun.
- Fork runs with an empty `pull_requests` payload are accepted only when the
  commit API resolves exactly one open associated PR.
- A final PR/head/label read occurs immediately before the Actions mutation.
  Normal PR Gate concurrency cancels the residual race if the head moves after
  that read.

## Idempotency and failures

Review, comment, status, and workflow completion events can all re-evaluate the
same head. Trusted evaluations serialize per PR. Once GitHub accepts the rerun,
the workflow's `run_attempt` becomes greater than one and every later event is a
no-op. A duplicate API rejection is ignored only after a fresh run read proves
that the rerun was already accepted; all other API errors fail visibly.

Closing and reopening a PR can create multiple runs for an unchanged head. The
dispatcher deterministically selects the newest canonical run by GitHub
creation time and run ID; older runs remain historical evidence. An admission
policy failure never emits a successful `Admission receipt`, so it cannot be
promoted. A malformed run or ambiguous PR association leaves `PR Gate` red and
fails the trusted transition workflow with a precise reason.

## Rollback

Change both `REVIEW_FIRST_MODE` values back to `observe`. The required context,
workflow identity, permissions, and branch protection remain unchanged; the
next PR event resumes the previous eager gate. Do not disable Review Gate or
remove the admission checks as a rollback shortcut.
