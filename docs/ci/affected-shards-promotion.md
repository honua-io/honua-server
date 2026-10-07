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
shard annotation. Skipped selections, unavailable receipts, failed aggregates,
and unfinished runs are reported separately and do not inflate the floor.
The denominator is executed **runs**, matching the workflow's promotion rule;
the number of distinct heads is also reported. Reruns use only the catalog's
current attempt. Multiple red shards in one run count as one red run.

## Reconcile reds and replay

For each red, inspect the PR head, its merge commit, and the same shard's
trailing result. Record a human-audited reconciliation in a JSON array:

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

Use `false-red` for a flake, interference, or environment difference. The tool
checks identifiers and evidence-link shape, but a reviewer must verify the
linked trailing job actually belongs to the stated merge and shard. A green
rerun after a code fix cannot establish that the earlier failure was false.
Unmerged heads remain unresolved when no equivalent trailing result exists.
Duplicate, stale-attempt, wrong-head, and unmatched reconciliations are rejected.

```bash
python3 scripts/ci/audit-affected-shards.py \
  --observations /tmp/affected-shards-evidence/observations.json \
  --adjudications /tmp/affected-shards-adjudications.json \
  --output /tmp/affected-shards-audit.json
```

Exit status 0 means the measurement satisfies the sample and false-red gates;
1 means observe more. Unknown reds leave `false_red_rate` null. The upper bound
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
the 60-sample boundary, the strict 2% boundary, and invalid reconciliations.
It also runs in `validate-ci-router.sh`. The existing selector/verdict fixture
remains `python3 scripts/ci/fixtures/validate-affected-shards.py`.
