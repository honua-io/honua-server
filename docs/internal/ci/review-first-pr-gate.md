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

Rollout begins in `observe`. After at least 20 representative PR heads have
immutable current-policy receipts, the evidence ledger is integrity-clean, and
the #3216 invariants are verified, a human may propose switching both values to
`enforce` in one reviewed commit. The ledger never performs that switch. Collect
at least 30 post-change heads before judging the latency and runner-minute target
from #3213.

## Promotion evidence ledger

Every successful `observe` decision now emits a bounded
`honua.review-first-observation/v1` artifact from the trusted Review Gate run.
The receipt contains the exact PR/head, the complete bounded PR Gate run and job
inputs used by the production dispatcher, the selected admission run, the
decision, the shared final full review/state/head/label and admission
revalidations, and a digest of all policy inputs. It records `mutation: none`.
Duplicate review events can emit duplicate receipts, but each exact head counts
once.

`Review-first Evidence Ledger` runs on the default branch daily or on manual
request. It downloads only artifacts attached to successful canonical Review
Gate runs, validates artifact/run identity and bounds, replays every receipt
through `evaluateReviewFirstDispatch`, separates changed policy digests into a
new cohort, and retains JSON plus Markdown for 30 days. It has only
`actions: read` and `contents: read`; its output is either `observe-more` or
`eligible-for-human-promotion-review`. It cannot publish a status, rerun a job,
change a label, change mode, trigger the train, or merge. The report shows both
distinct exact heads and distinct pull requests so a human can judge whether the
cohort is representative rather than accepting a burst of updates to one PR.
Workflow-run discovery divides the rolling window into 24-hour ranges, combines
and deduplicates their results, and fails closed if any partition reaches 1,000
runs. This avoids GitHub's 1,000-result cap on workflow-run searches that use a
`created` filter. Every observation uses the stable exact artifact name
`review-first-observation-v1`, so artifact discovery can ask GitHub for only that
name rather than scanning the repository's unrelated artifacts. It reads one
paginated repository artifact catalog and joins artifacts to trusted run IDs
locally; it never makes one artifact-list request per run. The promotion policy
caps the worst case at 300 run-query pages, three repository-artifact pages, and
300 receipt downloads: 603 requests under a 650-request policy ceiling, with at
least 350 requests reserved below the `GITHUB_TOKEN` limit of 1,000 per
repository per hour. Every page must report the same total, IDs must be unique,
the complete count must fit the page bound, and the selected receipt count must
fit the download bound. Any inconsistency or exhausted bound fails closed.

The pre-ledger audit found seven conservative API-derived candidates among 53
distinct heads. Those remain useful supporting evidence, but they are not
countable receipts: GitHub's historical PR association reflects the PR's current
head, and the combined commit-status endpoint exposes only the newest state.
Promotion therefore counts only the contemporaneous trusted receipts. Audits of
status history must use the paginated plural `/commits/{sha}/statuses` endpoint;
the singular combined-status response is not historical evidence.

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
- Observation artifacts are evidence, not authority. Their stable artifact name,
  producer workflow, run, attempt, event, producer policy SHA, receipt PR/head
  identity, policy digest, bounded snapshot, and replayed decision must all
  agree. Malformed or contradictory evidence fails the ledger's integrity gate
  and can never recommend promotion.
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
  summary write occurs between the final joint snapshot and the receipt/rerun.
  Normal PR Gate concurrency cancels the residual race if the head moves after
  those reads.
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
Repeated clean events may retain repeated observation artifacts, but the ledger
deduplicates by exact head and fails closed if one head maps to multiple PRs.
Once GitHub accepts the rerun,
the workflow's `run_attempt` becomes greater than one and every later event is a
no-op. A duplicate API rejection is ignored only after a fresh run read proves
that the rerun was already accepted; all other API errors fail visibly.

Bridge delivery is intentionally best-effort. It improves status freshness but
is never used as proof that review evidence is still valid. The merge train is
a manual batch merge authority and re-attests from source evidence twice.

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
