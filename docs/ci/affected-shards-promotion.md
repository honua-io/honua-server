# Affected-shard promotion evidence

`PR Gate / Affected shards` stays in `report` until a complete seven-day
measurement has at least 60 executed samples and a false-red rate strictly
below 2%. Reconcile every red with the same shard's trailing matrix result for
the PR's merge commit. An unresolved red is an unknown measurement, not a
confirmed regression or a false red. A successful aggregate in report mode
can contain either outcome.

Promotion is the two changes documented in `pr-gate.yml`: flip
`AFFECTED_SHARDS_MODE` to `enforce` and add `PR Gate / Affected shards` to branch
protection. Keep the shard cap, timeouts, selection, and advisory shards as
they are. Roll back by flipping the mode to `report`.

## Collect a complete window

The older annotation-collection example uses `gh run list` without `--limit`.
That defaults to 20 runs, below the 60-sample floor, and therefore cannot
establish promotion readiness. Increasing that limit alone still needs to
handle GitHub's 1,000-result query ceiling and current-attempt annotations.
Use the read-only auditor instead:

```bash
python3 scripts/ci/audit-affected-shards.py \
  --collect /tmp/affected-shards-evidence \
  --as-of 2026-10-07T16:45:00Z \
  --output /tmp/affected-shards-audit.json
```

Choose the exclusive UTC window end for each measurement; omitting `--as-of`
uses the current time. The tool uses the impact-routing collector's complete,
time-sliced pagination and five-minute transport backoff. It reads all jobs and
annotations for the catalog's current attempt. It never mutates GitHub state.
Each run's raw annotations are retained locally; `observations.json` is written
only after the whole collection completes. Collection failures are errors,
never partial passing measurements. The complete replay file is the audit's
source of truth; individual run files are diagnostic checkpoints.

Executed samples are runs with a green shard annotation or at least one red
shard annotation. Skipped selections, unavailable receipts, aggregates without a shard verdict,
and unfinished runs are reported separately and do not inflate the floor.
Every run in the window must be completed before promotion can pass; queued
and in-progress runs block it even when the executed-sample floor is met.
The denominator is executed **runs**, matching the workflow's promotion rule;
the number of distinct heads is also reported. Reruns use only the catalog's
current attempt. Multiple red shards in one run count as one red run.

## Reconcile reds and replay

For each red, inspect the PR head, its merge commit, and the same shard's
trailing result. When the merge commit has no completed shard test step, use
the first subsequent completed full trunk matrix containing the merge; verify
Git ancestry with complete history. Record both the merge SHA and the tested
trunk SHA. Record a human-audited reconciliation in a JSON array:

```json
[
  {
    "run_id": 123,
    "run_attempt": 1,
    "head_sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "shard": "STAC Protocol",
    "verdict": "confirmed-red",
    "merge_sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
    "trailing_job_url": "https://github.com/honua-io/honua-server/actions/runs/456/job/789",
    "reason": "The same STAC test failed on the merge commit."
  }
]
```

Use `false-red` for a flake, interference, or environment difference. Require
the same shard's **test step** to succeed; a successful job that skipped tests
or swallowed an advisory test failure is insufficient. The tool checks
identifiers and evidence-link shape, but a reviewer must verify the linked
trailing job actually belongs to the stated merge and shard. A green rerun
after a code fix cannot establish that the earlier failure was false.
Superseded heads need explicit evidence: unchanged shard inputs can establish
equivalence, or the same named test still failing on the landed revision can
confirm a red. Store that proof with the decision. Otherwise leave the head
unresolved, even if the later revision passes.
Unmerged heads remain unresolved when no equivalent trailing result exists.
Duplicate, stale-attempt, wrong-head, and unmatched reconciliations are rejected.

```bash
python3 scripts/ci/audit-affected-shards.py \
  --observations /tmp/affected-shards-evidence/observations.json \
  --adjudications /tmp/affected-shards-adjudications.json \
  --output /tmp/affected-shards-audit.json
```

Exit status 0 means the measurement satisfies the completion, sample and false-red gates;
1 means observe more. Unknown reds leave `false_red_rate` null. The lower bound
counts only reconciled false reds. The upper bound
treats every unresolved red run as false, once per run. This does not classify
unknown reds. It makes the uncertainty explicit.

The separate `impact-routing-evidence-ledger.yml` and
`audit-impact-routing-evidence.py` measure docs-only gate and native-image
routing receipts. Their seven-day integrity/loss trend and routing sample
cohorts are useful context, but cannot substitute for this lane's shard
false-red measurement. Current `/v4` routing policy sets docs-only and native
minimums to 20 each; the 60 executed-sample floor applies to this shard audit.

## Verification

`python3 scripts/ci/audit-affected-shards.test.py` covers collection beyond 20
runs, paged catalogs and annotations, current attempts, unavailable evidence,
the 60-sample boundary, unfinished runs above that floor, the strict 2% boundary,
and invalid reconciliations.
It also runs in `validate-ci-router.sh`. The existing selector/verdict fixture
remains `python3 scripts/ci/fixtures/validate-affected-shards.py`.

## Initial measurement on 2026-10-07 (PR #5696)

The complete window is **2026-09-30 16:45:00 UTC through 2026-10-07 16:45:00
UTC**, exclusive at the end. The audit collected 313 PR Gate runs:

| Measurement | Result |
|---|---:|
| Executed samples / distinct heads | 206 / 206 |
| Required sample floor / shortfall | 60 / 0 (146 above the floor) |
| Green runs | 163 |
| Red runs / red shard verdicts | 43 / 51 |
| Reconciled false-red runs | 2 |
| Reconciled confirmed-red runs | 1 (two STAC shards) |
| Unresolved red runs / shard verdicts | 40 / 47 |
| Unavailable / skipped / unfinished runs | 23 / 83 / 1 |
| False-red rate lower / upper bound | 0.97% / 20.39% |

**Initial decision: keep `report`.** The sample floor passes, but 47 shard verdicts
still lack a merge-commit reconciliation and one run is unfinished. Both block
promotion. The measured range cannot establish
the required rate **below 2%**. At this denominator, at most four false-red
runs are allowed; two are already reconciled. Do not describe the upper bound
as a measured false-red rate. Branch protection stays unchanged.

The [retained observations](affected-shards-observations-20261007.json) include
only protocol verdict annotations and run identities. The
[initial four reconciliations](https://github.com/honua-io/honua-server/blob/11f884dac9099fd1ce68ff532cb2856db8a7eea7/docs/ci/affected-shards-adjudications-20261007.json) link the
same-shard trailing jobs for the exact merge commits. Replay the observations
with the expanded reconciliation set below:

```bash
python3 scripts/ci/audit-affected-shards.py \
  --observations docs/ci/affected-shards-observations-20261007.json \
  --adjudications docs/ci/affected-shards-adjudications-20261007.json \
  --output /tmp/affected-shards-audit.json
# Expected exit status: 1 (promotion not ready).
```

For context, [routing ledger run 37516501121](https://github.com/honua-io/honua-server/actions/runs/37516501121)
reports 58 native heads, zero docs-only heads, zero integrity failures, and
2 missing receipts of 404 owed (0.50%, below its 5% budget). Replaying its
discovery catalogs reproduces all 402 indexed receipts. Replaying eight
retained daily ledgers gives **8 / 7 consecutive integrity/loss-green days**
with worst loss 2.52%; the published trend reported only 1 / 7. The latest
routing cohort still fails its docs-only floor by 20 heads, its serving savings
cohort by three heads, and its worker reuse cohort by two heads (worker
avoidance is zero of three). Its native floor is 20, which passes. These
image-routing results do not resolve the affected-shard false-red gate.

A full historical receipt replay on 2026-10-07 can download 376 of those 402
archives; the other 26 have expired under their seven-day retention. The
remaining archives reproduce 56 native heads and the same savings shortfalls,
but cannot reproduce the original zero-integrity result without the expired
bytes. Archive expiry after the original audit is not a new emission failure.

## Reconciliation follow-up on 2026-10-07

**Decision: keep `AFFECTED_SHARDS_MODE: report`.** Five proven false-red runs
already exceed the strict 2% limit: **5 / 206 = 2.43%**. The exact rate remains
unknown because some heads have no valid trailing comparison. Its bounds are
**2.43%–17.48%**; the lower bound alone rejects global promotion. Do not add
`PR Gate / Affected shards` to branch protection for this report-only rollout.
That remains the exact context to require after a successful future promotion.

The follow-up examines **every one of the original 47 unresolved shard
verdicts** and retains the original four decisions. It resolves 11 additional
verdicts across nine runs. The remaining 36 cannot honestly be called either
false reds or confirmed reds using the required evidence:

| Measurement | Follow-up result |
|---|---:|
| Fixed original window / executed samples | Seven days / 206 |
| False-red runs / shard verdicts | 5 / 5 |
| Confirmed-red runs / shard verdicts | 7 / 10 |
| Unresolved runs / shard verdicts | 31 / 36 |
| False-red rate lower / upper bound | 2.43% / 17.48% |
| Promotion ready | No |

The original observation snapshot still has one unfinished run and retains its
206-run denominator. Even adding that run as another green sample would leave
five false reds at 5 / 207 = 2.42%, above the threshold. It cannot reverse this
decision. The later trunk comparisons may fall after the observation window;
they reconcile heads already sampled and do not add samples.

The [complete reconciliation record](affected-shards-reconciliation-20261007.json)
contains all 51 shard verdicts, including run/attempt/head, PR, landed head,
merge SHA, tested trunk SHA, trunk run/job, test-step conclusion, named failing
tests, verdict and reason. Null trunk evidence is explicit. The
[accepted adjudications](affected-shards-adjudications-20261007.json) contain
only the 15 proven verdicts; the
[replayed audit](affected-shards-audit-20261007.json) preserves uncertainty and
returns `promotion_ready: false`. The replay command above still exits 1.

The comparison used current-attempt, fully paged jobs and test-step outcomes.
For next-full-matrix comparisons, the trunk run contains the merge commit by
Git ancestry in complete, unshallow history. Skipped tests, partial matrices,
and green advisory jobs with failed test steps do not establish a false red.
The retained proof uses protocol test names and identities; raw logs remain
outside the public documentation.

Two superseded heads have explicit input-equivalence proofs. For run
37117187230 (PR 5384), the only subsequent changes are certification documents.
For run 37108636224 (PR 5381), only `Theory` → `UnitTheory` attributes change in
two tests outside the failing shard: one in another project, and one in the
PrintingTools namespace explicitly excluded by the shard filter. Production,
shared harness, shard configuration and runner-script Git objects are identical
for both comparisons. The first is a false red; the second remains red on its
merge commit and is confirmed red. The proofs and complete changed-path lists
are retained with their decisions. Superseded STAC and ImageServer heads are
confirmed only where the same named failing tests remain red on the landed
revision, with overlapping test names retained in the evidence.

### False-red shards ranked by count

Counts are affected-shard **runs**, not individual test failures. Tied shards
are ordered by name. These are the proven false reds; uncertainty is excluded.

| Shard | False-red count | Failing test | PR-head run IDs |
|---|---:|---|---|
| Server Features Miscellaneous | 2 | `TileExportDurableRecoveryRedisTests.Retry_WithFreshKey_ReusesCompletedPackageCheckpoint` | 37117187230, 37118682741 |
| gRPC Protocol and Scene | 2 | `GrpcApplyEditsDistributedIdempotencyTests.WriteOutlastingTheReservationWindow_KeepsIt_AndAnotherReplicaReplaysTheResult` | 37441285627, 37490646062 |
| GeoServices Catalog and ImageServer Support | 1 | `ImageServerMosaicIntegrationTests.Identify_MultiBandRasterAtProjectedPoint_ReturnsEsriValueAndLocationSpatialReference` | 37206830900 |

Cut one flaky-test investigation packet per row, using the PR and trailing job
links in the reconciliation record. The tile-export test repeats completed
package work; the gRPC test loses its edit reservation; ImageServer identify
returns a no-data result where the raster fixture expects band values. Keep
assertions and the existing capacity/time budgets intact while reproducing
and repairing each failure. The misc shard also has one confirmed red and one
unresolved older head; ImageServer has two confirmed reds and one unmerged
head. Those observations are separate from the proven false-red count.

### Remaining evidence gaps

| Reason | Shard verdicts | PRs |
|---|---:|---|
| PR still open; no merge commit | 13 | 5428, 5641, 5644, 5652, 5655, 5669 |
| PR closed without merging | 4 | 5653, 5657, 5660, 5661 |
| Superseded head; production, tests or shared inputs changed before the later passing result | 17 | 5355, 5396, 5486, 5518, 5526, 5543 |
| No completed same-shard comparison on the merge or a subsequent full trunk matrix at collection time | 2 | 5664 |

Every row has the individual run identity, failing test names (or an explicit
shard timeout), and its available comparison in the JSON record. A later fixed
revision cannot retroactively prove a false red. For example, the nine caching
shard reds in PR 5355 fail `SiteRoot RedirectsToTheServicesDirectory`; the
landed revision changes the site-root endpoint. Its green matrix is therefore
not nine flakes. The MapServer export and capability-manifest heads similarly
change relevant implementation/tests before passing. Preserve these evidence
gaps rather than replacing them with invented outcomes. A complete numeric
false-red rate would require equivalent trailing results for these old heads;
some unmerged heads will never acquire them through normal landing.

### Proposed per-shard enforcement

The current workflow has **one global mode and no enforcement allowlist**.
The shard selection allowlist chooses which tests run; it cannot choose which
red receipts fail the aggregate. Setting `enforce` today would enforce every
selected shard, including the three proven false-red families.

Propose an initial enforcement allowlist of **OData Core, STAC Protocol, and
STAC Items and Collections**. These are the families with observed reds, zero
false reds, and no remaining unresolved red verdicts in this window: one OData
verdict and three verdicts in each STAC family, all confirmed red. Other
families with zero proven false reds still have unresolved reds; green-only
families have no retained per-shard exposure counts, so zero observations do
not prove a per-shard rate.

A separate workflow change would need an explicit list of stable shard names
at the aggregate verdict boundary. Continue selecting/running the same shards
and publishing every verdict. Fail the aggregate only for non-passing receipts
in the enforcement list. Carry selected shard identities from the selector so
missing receipts for enforced families fail closed, and reject invalid list
entries; receipt count alone cannot identify which family is missing. Keep
selector/configuration failure fail-closed while that mixed mode is required.
Test enforced reds, report-only reds, mixed outcomes, missing enforced receipts,
and docs-only skips. After that implementation lands, require the exact
`PR Gate / Affected shards` context through the operator's branch-protection
change. No allowlist, selection, capacity, timeout or workflow-mode change is
made by this evidence follow-up.

### Follow-up validation

The eight offline auditor tests and the real affected-shard selector/verdict
fixture pass. The focused `docs/ci/` link check passes all five targets with
its rot allowlist restricted to that same directory. An independent consistency
check matches all 51 unique run/attempt/head/shard identities to the original
annotations, matches all 15 accepted decisions to their evidence rows, checks
false/confirmed reds against successful/failed test steps, and reproduces the
retained audit byte-for-byte as JSON values. No .NET project changes require
formatting or a solution build.
