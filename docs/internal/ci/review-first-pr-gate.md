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

The fleet's serialized per-PR lander is the routine merge authority; the merge
train remains available for manual/release-candidate batches. Branch protection requires
both contexts, so PR-authored `PR Gate` workflow code cannot substitute for the
trusted exact-head review decision.

## Modes

`REVIEW_FIRST_MODE` appears once in each of `pr-gate.yml` and
`review-gate.yml`. `scripts/ci/validate-review-first-dispatch.sh` rejects drift.

| Mode | PR Gate attempt 1 | Required Review Gate | PR Gate attempt 2 |
|---|---|---|---|
| `observe` | Admission and current eager verification | Publishes exact-head admission; records `observe` without an Actions mutation | Not created by review-first |
| `enforce` | Admission only; intentionally fails at `Await exact-head review` | Publishes exact-head admission and reruns the exact failed job once | Runs expensive verification |

The experiment remains in `observe`; it was not promoted to `enforce`.
The unused PR Gate enforcement structure is tracked separately under #7880.
Retiring report collection does not authorize a mode change or change either
required context.

## Retired promotion evidence ledger

The final successful daily audit on 2026-11-15,
[run 43752404798](https://github.com/honua-io/honua-server/actions/runs/43752404798),
recommended `observe-more`: 5 distinct current-policy heads and PRs against the
20-head minimum, 151 non-current-policy receipts, and 1,724 successful Review
Gate runs without receipts. The integrity count was zero, but the sample was
insufficient. Observation latency was 44.22 minutes at p50 and 59.60 at p90.
These are collection latencies, not measured PR cycle-time improvements.

The daily workflow, its promotion policy, report collector, and producer
artifacts are retired. The single audit job ran from 19:26:14Z to 19:28:50Z,
156 seconds (2.60 runner-minutes). Retirement removes one scheduled job per day;
no PR jobs are removed and producer artifact overhead was not separately
measured. Historical artifacts remain governed by their existing retention.
The production review decision and all final state revalidation remain active.

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
  `actions: write`, but checks out the immutable `github.workflow_sha`, persists
  no checkout credential, and executes no PR-authored code. That SHA identifies
  the trusted default-branch workflow policy for the event. It has no manual
  dispatch path that can select a PR ref.
- Exact-head review, unresolved threads, draft/hold/escalation state, pagination,
  workflow identity, event type, head SHA, PR association, run identity, run
  attempt, admission receipt, wait receipt, and skipped expensive steps all fail
  closed before an enforced rerun.
- Fork runs with an empty `pull_requests` payload are accepted only when the
  commit API resolves exactly one open associated PR.
- Two complete, identical bounded review snapshots are required for each
  attestation read. At the irreversible boundary, the workflow then requires two
  identical evaluations of the entire joint state: that stable review result,
  exact-head PR Gate runs, commit/PR association, selected run jobs, and the
  production decision. A dismissed review, newly unresolved thread, truncated
  query, hold, changed head, or canonical-run change fails closed. No awaited
  summary write occurs between the final joint snapshot and the rerun.
  Normal PR Gate concurrency cancels the residual race if the head moves after
  those reads.
- Resolving a thread has no GitHub Actions event. An operator re-attests with
  trusted default-branch code using:

  ```bash
  gh api --method POST repos/honua-io/honua-server/dispatches \
    -f event_type=review-gate-reattest -F client_payload[pr]=<PR>
  ```

## Idempotency and failures

Review, comment, status, and workflow completion events can all re-evaluate the
same head. Every event first resolves exactly one PR number, including fork
workflow completions, and trusted evaluations serialize on that PR number.
Repeated clean observe events report their decisions without retaining artifacts.
Once GitHub accepts the rerun,
the workflow's `run_attempt` becomes greater than one and every later event is a
no-op. A duplicate API rejection is ignored only after a fresh run read proves
that the rerun was already accepted; all other API errors fail visibly.

Bridge delivery is intentionally best-effort. It improves status freshness but
is never used as proof that review evidence is still valid.

### Shedding a superseded subject

A review event is worth trusted work only while its subject can still consume
the result, and there are two moments where that can stop being true:

1. **When the event arrives.** `resolve` (and the bridge) compares the event's
   subject SHA with the PR's live head and sheds a closed PR or a moved head
   before admitting `attest`.
2. **When the trusted job finally starts.** `attest` serializes on the PR number
   with `cancel-in-progress: false`, deliberately, so it can queue behind other
   events for minutes, and the pull request can *close* inside that queue.
   `attest` therefore re-reads the pull request as its first step — before the
   checkout and the bounded GraphQL snapshot — and sheds a
   subject that closed while it waited. Measured on trunk before this check
   existed, 27 of 63 sampled `attest` failures (43%) were
   `pull request is not open`: the whole trusted job spent to stamp a red
   `Review Gate` status onto an already-merged commit.

Both sheds are **neutral and one-directional**. They write no commit status,
fail no run, and record one job-summary line. They can only *withhold* an
attestation, never publish one, so shedding can never admit a pull request that
would otherwise have been blocked — it can only withhold a status that no
admission could have consumed. `reopened` re-fires the workflow.

**A closed subject is the only staleness `attest` may shed, and that asymmetry
is load-bearing.** Resolvers now serialize in a PR-wide queue without
cancelling earlier events. GitHub does not guarantee event delivery order, so a
late event may still describe an old head. The required status must bind to
the exact current head. `attest` therefore binds to the **live head**, never to
the subject its event named: shedding a moved subject there would leave that
head permanently unattested and the PR unmergeable until some unrelated comment
or label event happened to wake the gate again. For the same reason an event
that names no subject SHA — `issue_comment`, the highest-volume trigger — is
never shed on staleness anywhere: its entire purpose is that review evidence
moved while the head did not.

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
