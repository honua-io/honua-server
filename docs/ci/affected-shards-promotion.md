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
Superseded heads need explicit evidence: unchanged relevant inputs can establish
equivalence, or matching assertion and failure-cause evidence can confirm the
same red across changed inputs. A matching test name alone is insufficient.
Store that proof with the decision. If the tested trunk SHA differs from the
merge SHA, establish equivalence across that interval too; ancestry alone is
insufficient. Otherwise leave the head unresolved, whether the later run passes
or fails.
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

## Reconciliation follow-up, corrected on 2026-10-08

**Decision: keep `AFFECTED_SHARDS_MODE: report`.** The corrected evidence does
not establish a false-red rate below 2%. Two proven false-red runs give a
**2 / 206 = 0.97%** lower bound; 39 unresolved red runs give a **19.90%** upper
bound. The exact rate remains unknown. The lower bound does **not** by itself
reject promotion; unresolved comparisons and one unfinished run prevent it.
Do not add `PR Gate / Affected shards` to branch protection on this evidence.

The prior follow-up accepted 15 shard verdicts, including superseded heads
based on overlapping failing test names and later trunk results based only on
ancestry. Review of #5701 and #5702 found those standards insufficient. The
corrected record retains all observations and comparison metadata, but accepts
only five verdicts with the required evidence:

| Measurement | Corrected result |
|---|---:|
| Fixed original window / executed samples | Seven days / 206 |
| False-red runs / shard verdicts | 2 / 2 |
| Confirmed-red runs / shard verdicts | 2 / 3 |
| Unresolved runs / shard verdicts | 39 / 46 |
| False-red rate lower / upper bound | 0.97% / 19.90% |
| Promotion ready | No |

The [complete reconciliation record](affected-shards-reconciliation-20261007.json)
retains all 51 shard verdicts, including run/attempt/head, PR, landed head,
merge SHA, tested trunk SHA, trunk run/job, test-step conclusion, named failing
tests, verdict and reason. The
[accepted adjudications](affected-shards-adjudications-20261007.json) contain
only the five supported verdicts. The
[replayed audit](affected-shards-audit-20261007.json) preserves uncertainty and
returns `promotion_ready: false`; the replay command above exits 1.

### Comparisons requiring more evidence

Run 37490646062 tested trunk `3beef35b`, four commits after merge `8fabbf25`.
The intervening diff includes production, tests and `.github/ci-shards.json`.
Its later passing test step cannot establish that the earlier red was false.
The complete changed-path list is retained with the comparison.

The superseded STAC runs 37533561480 and 37569461538 and ImageServer run
37529226693 have overlapping failing test names on the landed revision, but
changed implementation or tests. A test can fail at a different assertion or
for another cause after such changes. Without input equivalence or matching
failure-cause evidence, those five shard verdicts remain unresolved.

The same merge-to-tested-trunk rule applies to runs 37607358792, 37298392860,
37118682741 and 37117187230. Their later comparisons also include intervening
production or shared-input changes without a retained equivalence proof for
that interval. They now remain unresolved. The pre-landing equivalence proof
for 37117187230 does not establish equivalence after the merge. Changed-path
lists and all previous comparison metadata remain available for further audit.

Run 37108636224 retains its confirmed-red classification: its comparison tests
the exact merge commit, and its pre-landing proof records identical production,
shared harness, routing and runner inputs. Only two attributes outside the
failing shard changed. The two exact-landed-head STAC verdicts for run
37614307268 also remain confirmed red at their exact merge commit.

### Proven false-red shards

| Shard | False-red count | PR-head run ID |
|---|---:|---|
| GeoServices Catalog and ImageServer Support | 1 | 37206830900 |
| gRPC Protocol and Scene | 1 | 37441285627 |

Both comparisons have a successful same-shard test step at the exact merge
commit and an audited head matching the landed head. These remain candidates
for focused flaky-test investigations. Unresolved rows are excluded from this
count, rather than assigned a guessed outcome.

### Enforcement and validation

Withdraw the proposed OData/STAC enforcement allowlist: those families now
have unresolved red comparisons. The current workflow retains one global
`report` mode. Any future per-shard enforcement proposal needs new evidence
and explicit handling of missing receipts; this correction changes no shard
selection, assertion, timeout, mode or branch-protection setting.

The offline auditor and selector/verdict fixtures validate the replay. A
consistency check matches all 51 unique run/attempt/head/shard identities to
the original observations, matches the five accepted decisions to their
reconciliation rows, and reproduces the retained audit as JSON values.
