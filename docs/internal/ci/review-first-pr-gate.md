# Review-first PR Gate

Tracking: [#3216](https://github.com/honua-io/honua-server/issues/3216), under
the evidence-driven CI program [#3213](https://github.com/honua-io/honua-server/issues/3213).

## Why this exists

The required `PR Gate` used to start its .NET build and tests on every pushed
head. Exact-head review frequently requested another commit, so the repository
paid for verification that could not be reused. The baseline attached to #3213
found 22 canceled runs among the latest 30 PR Gate runs.

Review-first makes `PR Gate` and `Review Gate` separate required contexts and
separates cheap admission from expensive verification:

1. A `pull_request` run creates the `PR Gate` check and performs admission.
2. Review events may wake a credential-free `Review Event Bridge`. Its
   completion is a latency hint that causes `Review Gate Attestation` to re-read
   exact-head Codex evidence using only the default branch's policy code.
3. The trusted workflow selects one completed PR Gate run for the current PR
   head. In enforce mode, it releases that run with `rerun-failed-jobs`.
4. Attempt 2 performs the existing build, format, fast/architecture tests, and
   serving-boundary fixtures. Only this attempt can turn required verification
   green; only the trusted attestation can turn required admission green.

The merge train remains the sole merge authority. Branch protection requires
both contexts, so PR-authored `PR Gate` workflow code cannot substitute for the
trusted exact-head review decision.

## Modes

`REVIEW_FIRST_MODE` appears once in each of `pr-gate.yml` and
`review-gate.yml`. `scripts/ci/validate-review-first-dispatch.sh` rejects drift.

| Mode | PR Gate attempt 1 | Required Review Gate | PR Gate attempt 2 |
|---|---|---|---|
| `observe` | Admission and current eager verification | Publishes exact-head admission; records `observe` without an Actions mutation | Not created by review-first |
| `enforce` | Admission only; intentionally fails at `Await exact-head review` | Publishes exact-head admission and reruns the exact failed job once | Runs expensive verification |

Rollout begins in `observe`. After at least 20 representative PR heads and the
#3216 invariants are verified, switch both values to `enforce` in one reviewed
commit. Collect at least 30 post-change heads before judging the latency and
runner-minute target from #3213.

## Trust boundary

- Attempt 1 and attempt 2 are ordinary `pull_request` workflows with read-only
  repository/package permissions. They may execute PR code but have no trusted
  write credential.
- `pull_request_review` events execute merge-branch workflow code. They therefore
  enter only `Review Event Bridge`, which has a read-only token, performs no
  checkout, and emits no status or mutation. The bridge is not a trust boundary:
  PR code can delay or suppress it.
- `Review Gate Attestation` wakes from the bridge's `workflow_run`, PR Gate
  completion, `pull_request_target`, or default-branch `issue_comment`. It has
  `actions: write`, but checks out only the default branch, persists no checkout
  credential, and executes no PR-authored code. It has no manual dispatch path
  that can select a PR ref.
- Exact-head review, unresolved threads, draft/hold/escalation state, pagination,
  workflow identity, event type, head SHA, PR association, run identity, run
  attempt, admission receipt, wait receipt, and skipped expensive steps all fail
  closed before an enforced rerun.
- Fork runs with an empty `pull_requests` payload are accepted only when the
  commit API resolves exactly one open associated PR.
- A final PR/head/label read occurs immediately before the Actions mutation.
  Normal PR Gate concurrency cancels the residual race if the head moves after
  that read.
- Live merge-train selection and pre-land validation independently fetch current
  reviews, threads, labels, and head identity from GitHub. They refresh
  `Review Gate` and fail closed on negative/truncated evidence, so a stale status
  or suppressed bridge can never authorize landing.
- Resolving a thread has no GitHub Actions event. The live train repairs that
  stale failure during selection; an operator can re-attest sooner with trusted
  default-branch code using:

  ```bash
  gh api --method POST repos/honua-io/honua-server/dispatches \
    -f event_type=review-gate-reattest -F client_payload[pr]=<PR>
  ```

## Idempotency and failures

Review, comment, status, and workflow completion events can all re-evaluate the
same head. Every event first resolves exactly one PR number, including fork
workflow completions, and trusted evaluations serialize on that PR number.
Once GitHub accepts the rerun,
the workflow's `run_attempt` becomes greater than one and every later event is a
no-op. A duplicate API rejection is ignored only after a fresh run read proves
that the rerun was already accepted; all other API errors fail visibly.

Bridge delivery is intentionally best-effort. It improves status freshness but
is never used as proof that review evidence is still valid. The merge train is
the sole merge authority and re-attests from source evidence twice.

Closing and reopening a PR can create multiple runs for an unchanged head. The
dispatcher deterministically selects the newest canonical run by GitHub
creation time and run ID; older runs remain historical evidence. An admission
policy failure never emits a successful `Admission receipt`, so it cannot be
promoted. A malformed run or ambiguous PR association leaves `PR Gate` red and
fails the trusted transition workflow with a precise reason.

## Rollback

Change both `REVIEW_FIRST_MODE` values back to `observe`. `PR Gate` resumes eager
verification, while required `Review Gate` continues to protect exact-head
admission. Workflow identity, permissions, and branch protection remain
unchanged. Do not disable Review Gate or remove it from branch protection as a
rollback shortcut.
