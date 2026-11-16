# Merge coordination runbook

Routine merging on `honua-server` is performed by the fleet's serialized per-PR
lander. It rechecks that the PR is open, non-draft, MERGEABLE, based on `trunk`,
not held or escalated, has exact-head successful `PR Gate` and `Review Gate`
contexts, and has no unresolved review threads immediately before merging. No
live Claude/Codex session needs to babysit a queue.

| Workflow | Trigger | Job |
|---|---|---|
| Fleet serialized per-PR lander | External serialized service | Routine merge authority: rechecks trunk base, exact-head `PR Gate` + `Review Gate`, review-thread, mergeability, draft, and hold/escalation admission immediately before landing. A deterministic trailing trunk CI failure pauses routine landings except fix-forward branches. |

The batch merge train (`merge-train.yml`, `merge-train-rerun-recovery.yml` and
`scripts/ci/merge-train/`) was retired in simplify A4 (2026-11-15); ADR-0055 is
superseded. No repository workflow or script can merge, and
`scripts/ci/validate-single-merge-authority.sh` enforces that.

## Gate model: what runs on a PR

There is no per-PR CI matrix; the full matrix has trailed on trunk since
2026-06-18. A PR's own events run:

- the cheap `PR Gate` (`.github/workflows/pr-gate.yml`): build, format, Fast unit tests and an
  architecture smoke, on one runner;
- CodeQL;
- GitBook previews;
- the drift and OpenAPI governance checks.

The full CI matrix runs only on batch CI, as trailing trunk runs, and on the nightly schedule.
That matrix is every server-test shard in `.github/ci-shards.json` plus the AOT, Docker,
browser, MCP and provider lanes.

**The required branch-protection checks are `PR Gate` and `Review Gate`.**

- `PR Gate` (app id `15368`) is the unprivileged verification context.
- `Review Gate` is the trusted, exact-head admission context, published only by default-branch
  policy.

Both are unfiltered and required. `CI Gate` remains the batch-CI aggregator and is intentionally
not a required per-PR context.

**Nothing in CI validates the PR template.** The `Validate PR Template Compliance` job lives in
`ci.yml`, which has no `pull_request` trigger. Its one real step is also gated on
`github.event_name == 'pull_request'`, so it never fires on a PR, and it was only advisory even
when it did. The template is a human contract:

- Fill every section, including the `Closes #N` or `Refs #N (<what remains>)` disposition.
- Tick the `scripts/ci/pre-pr-check.sh` box only after a real run. If you ran an equivalent
  instead, say what you ran under **Testing**.

`PR Gate` does read the commits and the PR body for one thing: the AI-attribution check,
`scripts/ci/check-commit-attribution.py`.

## Normal landing path: the per-PR lander

Keep a ready PR non-draft and let `PR Gate` complete. The routine merge authority is the fleet's
serialized per-PR lander. It continuously lands any open PR that meets all of these conditions:

- MERGEABLE and non-draft;
- not labelled `hold`, `train:hold` or `train:escalated`;
- currently based on `trunk`;
- `PR Gate` and `Review Gate` both green at its exact current head;
- zero unresolved review threads.

The lander rechecks every condition immediately before merging. You do not dispatch anything:

- Resolve review findings with evidence, never with bare resolutions.
- Keep the head green.

The full CI matrix trails on trunk after landings. Trunk runs are serialized and never cancelled
by a newer trailing dispatch. A deterministic trunk red pauses the lander for every branch except
`fix/`, `revert/` and `hotfix/` ones until a fix-forward PR lands. Never weaken tests to clear it.

**Batch verification (shadow, CI velocity plan v2).** The same lander also verifies candidate
batches of landable PRs:

1. It reproduces GitHub's sequential squash merges on a private `land/batch/<base7>/<id>` ref.
2. It dispatches `ci.yml` there with `selective_base` set to the last verified trunk head.
3. It judges the run by a verification receipt, never by a job count. See
   `docs/internal/ci/gate-model.md`, "Verification receipts and batch landing".

While batch verification runs in shadow, it only records would-land and would-quarantine
outcomes. Per-PR landing is unchanged, and the batch path never merges, labels or comments on a
PR. `land/batch/*` branches and `CI lander-batch-*` runs belong to the lander:

- do not push to, rerun or delete them;
- never read one as a verdict on trunk or on your PR.

Batch landing going live is a separate operator decision. Once it is live, a red batch
quarantines its members until a verified green subset containing them exists. A quarantine is
keyed to the head it was judged at, so pushing a fix releases it.

## Base every PR on `trunk`; do not stack (#3248)

Stacking is how reviewed, tested, nominally merged work disappears:

1. The stacked PR merges into its base branch.
2. GitHub marks it MERGED, and the linked issue often auto-closes. CI was green.
3. The base branch itself never lands.

The payload is then not in the product, and nothing signals it. This happened three times in five
weeks (#3116, #3113, #2835).

- **Retarget when the base merges.** If you must stack while a base is in review, retarget the
  stack member at `trunk` the moment its base merges (`gh pr edit <N> --base trunk`), then
  rebase. Never merge a PR into a base branch that has already landed or been deleted.
- **Neither the MERGED badge nor `git merge-base --is-ancestor <mergeCommitSha> origin/trunk`
  is evidence on its own.** The ancestry test checks commit identity, but a squash, a
  cherry-pick or an independent re-land puts the content on `trunk` under a different SHA. Use
  it to find candidates, then check the content. The first #3248 sweep filed three false
  positives out of four this way.
- **The weekly stranded-merge detector is the backstop.**
  `scripts/ci/detect-stranded-merges.py` runs from
  `.github/workflows/stranded-merge-detector.yml`.
  - It adjudicates each candidate by patch identity first, then blob equality, then added-line
    presence.
  - It classifies each candidate as one of:
    - `stranded` (files absent);
    - `edits-missing` (files present, the PR's lines absent);
    - `superseded`;
    - `landed`;
    - `indeterminate` (usually a force-pushed or deleted stack base; treated as actionable,
      never as a pass).
  - It also warns about open PRs whose base has already merged or been deleted.

  It is a backstop, not the control: retarget the stack member yourself.
- **Write the decision down in `scripts/ci/stranded-merge-dispositions.json`.** Each entry needs
  a `reason` and the exact `paths` it covers.
  - `abandoned` or `landed-elsewhere` moves the finding to the Adjudicated table.
  - `recovering` (with the recovery PR) keeps the finding actionable until that PR lands.

  A decision cannot absorb a path it never adjudicated, so a file later deleted from `trunk`
  re-opens the finding.

## Testing & validation

- `scripts/ci/validate-single-merge-authority.sh` proves no repository workflow or script can
  merge.
