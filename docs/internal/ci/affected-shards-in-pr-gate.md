# Affected shard families in PR Gate

Status: **report-only**, measuring. Owner: `.github/workflows/pr-gate.yml`
(`affected-shards-select` / `affected-shard` / `affected-shards`).
Related: ADR-0037 (shard routing), ADR-0043 (affected projects), #3204, #3213.

## The gap this closes

`PR Gate` is a lean subset — build, fast unit, architecture — on one runner.
The 71 server-test shard families in `.github/ci-shards.json` run per trunk tip
in the trailing matrix (p50 44 min, p90 65 min, plus a rerun to classify), so a
shard regression is found *after* the merge that caused it.

On 2026-09-07 trunk was red for roughly 15 of the previous 24 hours. Every real
regression in that window sat in a shard family whose own routing entries the
offending change had touched. The routing information needed to predict them
pre-merge already existed in `.github/ci-shards.json`; nothing on the pull
request consumed it.

## The selection rule

`scripts/ci/compute-affected-shards.py` runs on the pull request's merge ref
and answers one question: *which shard families is this diff most likely to turn
red?* It is a selector, not a second router — every routing decision is
delegated to machinery that already gates trunk.

1. **Skip when nothing can move a shard.** A diff touching no product-code path
   selects nothing and costs no runners (`no_product_code`). Product code is
   `src/` and `tests/dotnet/` **unioned with every root the shard map itself
   routes on** — `.github/ci-shards.json` also claims `observability/`,
   `samples/gp/` and `tests/fixtures/toolbox-translation/`, which are shard
   inputs (`SloMetricContractTests` reads `observability/slo-metric-contract.json`)
   that would otherwise be skipped while the trailing matrix went red on them.
   `infrastructure_paths` is deliberately excluded, so a workflow-only diff
   still costs nothing. `tests/dotnet/` is included deliberately: shard filters
   select test classes by fully-qualified name, so a test-only change is
   exactly the kind of diff that reddens a shard without touching `src/`.
2. **Ask the router.** `scripts/ci/honua-server-targeted-tests.sh` is invoked
   verbatim with `--stdin`, so PR Gate can never route differently from the
   trailing matrix it is trying to predict. Its answer is the candidate set.
3. **Narrow a `run_all` answer by the project closure.**
   `scripts/ci/compute-affected-projects.sh` walks `<ProjectReference>` in
   reverse, which is the source-project → test-project mapping this lane needs.
   Under `run_all`, a shard whose test assembly the diff cannot reach is
   dropped. A force-full (`ALL`) closure carries no narrowing information and is
   never read as one. A **targeted** router answer is authoritative and is never
   trimmed by the closure — that would make the lane predict something other
   than what the matrix will run. The one exception is the router's
   `no_path_match` **default** (`default_shards_when_no_match`), which is not a
   targeted answer at all: for a changed test file no `paths` entry claims, the
   shards whose filters actually run that test are added alongside it. Without
   that, editing
   `tests/dotnet/Honua.Protocols.OData.Tests/Source/ODataFeatureProviderResolverTests.cs`
   selected only the Core shard — a different assembly — and never ran `OData
   Core`, the family that owns the changed test.
4. **Rank largest-impact first**, by:
   1. `test_class_hits` — changed test files declaring a class this shard's own
      `dotnet test --filter` actually selects, evaluated with
      `scripts/ci/check-server-test-shard-coverage.py`'s filter evaluator and
      class inventory. Exact, not heuristic: a hit means this shard literally
      runs the test the diff touched. Scored against the **effective** runner
      filter: `scripts/ci/run-server-test-shard.sh` composes every shard filter
      with `&Tier!=Slow&Tier!=Fast`, so a changed class whose tests are all Fast
      (Fast runs once per CI run in `dotnet-foundation-tests`) or all Slow
      (nightly) credits nobody. The tier is resolved from the source — literal
      `[Trait("Tier", …)]` and the TestKit attributes that emit one — because
      the coverage guard's filter evaluator returns neutral-true for every
      non-`FullyQualifiedName` clause by design, which makes `(f)&Tier!=Fast`
      and `f` indistinguishable to it.
   2. `path_hits` — how many of the diff's files this shard's `paths` claim.
   3. `project_affected` — the shard's test project is in the closure.
   4. `dispatch_rank` descending — the observed shard duration ci.yml already
      dispatches by. Front-loading the longest shard is what keeps six parallel
      shards near one shard of wall clock.
   5. Shard name, for determinism.
5. **Truncate to six.** The dropped families are named in the receipt.

### Why `test_class_hits` is the term that matters

Shard `paths` name individual test *files* for the protocol-split projects, so
a diff that **adds** a test file to a directory a shard already owns files in is
claimed by no routing entry at all and escalates to `run_all` — leaving the cap
to choose among 71 families with no routing signal. On the merge commits of
#4465 and #4489 this term moves the family that actually went red on trunk to
rank 4 and rank 2. Without it, #4465's owner sat at rank 10, outside the cap.

That routing gap is worth closing in `.github/ci-shards.json` itself (directory
entries rather than exact files for the protocol test projects), but doing so
changes trailing-matrix routing and belongs in its own change.

### Why the cap is not negotiable

A `run_all` answer is 71 shard jobs. Fanning that out per pull request is the
2026-06-18 runner-starvation spiral that #2865 removed and that
`.github/actions/lean-gate` is written to avoid. This lane buys a *bounded*
probability of catching the regression pre-merge; the trailing matrix remains
the complete backstop. Raising the cap to "cover more" gives that back.

## Measured cost

Offline dry run over the 20 most recently merged pull requests
(`--base <merge>^ --head <merge>`, cap 6):

| | |
|---|---:|
| PRs analysed | 20 |
| Selected nothing (`no_product_code`) | 14 |
| Ran shards | 6 |
| — router `targeted` / `run_all` | 1 / 5 |
| Runner-minutes per *running* PR (mean / median / max) | 89.8 / 88.8 / 104.7 |
| Wall-clock minutes per *running* PR (mean / median / max) | 20.8 / 19.2 / 26.9 |
| Amortised over all 20 PRs | 26.9 runner-min, 6.2 wall-clock-min |

Minutes are `dispatch_rank`, the observed shard duration ci.yml dispatches by;
runner-minutes are the sum over selected shards and wall clock is the maximum,
since the shards run as a parallel matrix. The lane runs beside the required
`PR Gate / Build and tests` job (~18.7 min p50), so it does not extend the
required context's critical path.

That window is unusually CI-heavy — 14 of 20 were Dependabot action bumps,
Lambda-certification fixes, or docs. A week weighted toward product changes
lands closer to the 89.8 runner-minute per-running-PR figure.

### Would it have caught the 2026-09-07 reds?

The family that owns each PR's changed tests was inside the cap in all four:

| PR | Router | Owning family | Rank |
|---|---|---|---:|
| #4463 | `run_all` narrowed | FeatureServer Endpoints Query Services and Replication | 1 |
| #4465 | `run_all` narrowed | FeatureServer Tiles and Replica | 4 |
| #4466 | `targeted` | STAC Protocol / Server Features Miscellaneous | 1 / 3 |
| #4489 | `run_all` | Server Features Console and Alerts | 2 |

The dry run builds its test-class inventory from the current working tree
rather than each commit's tree, so a class deleted since would be missed. None
of the four depends on one.

## Report-only, and how it gets promoted

`AFFECTED_SHARDS_MODE` in `.github/workflows/pr-gate.yml` starts at `report`:

- the per-shard test failure is swallowed at the step (the ci.yml advisory-shard
  pattern — a red check-run on a *non-required* job still forces the pull
  request to `UNSTABLE` and stalls the clean-only merge train), and
- the aggregate context **`PR Gate / Affected shards`** publishes the verdict in
  its step summary and as `HONUA_AFFECTED_SHARD_*` annotations while concluding
  success.

Missing evidence is mode-aware, because promotion is only those two changes and
the context must not be able to pass by failing to look. In `report` an
unavailable selection, an absent receipt, or a receipt set smaller than the
selection warns and passes; in `enforce` each of those fails the context
(`HONUA_AFFECTED_SHARD_UNAVAILABLE`). In particular a *partial* receipt set is
never green: one shard dying before `run-server-test-shard.sh` writes its timing
file while another passes would otherwise report every family it heard from as
green and hide the one it was spending a runner to learn about. A confirmed red
is still reported as a red rather than as infrastructure.

**Promotion rule: make the context required when its false-red rate over 7 days
of runs is below 2%.** A false red is a run emitting
`HONUA_AFFECTED_SHARD_RED` for a head whose change did not in fact break that
shard — flake, cross-shard interference, or an environment difference from
`ci.yml::server-tests`. Below 2% is the bar because the lane spends a bounded
fan-out on every product-code PR; a noisier detector spends that budget on
re-running green shards by hand.

Collect the window with the GitHub CLI:

```bash
gh run list --repo honua-io/honua-server --workflow pr-gate.yml \
  --created "$(date -u -d '7 days ago' +%Y-%m-%d)..$(date -u +%Y-%m-%d)" \
  --json databaseId --jq '.[].databaseId' \
| while read -r run_id; do
    gh run view "${run_id}" --repo honua-io/honua-server --log \
      | grep -o 'HONUA_AFFECTED_SHARD_[A-Z]* .*' || true
  done
```

Then reconcile each `HONUA_AFFECTED_SHARD_RED` against the same shard's result
in the trailing matrix run for that PR's merge commit. Promotion is two audited
changes: flip `AFFECTED_SHARDS_MODE` to `enforce`, and add
`PR Gate / Affected shards` to branch protection. Rollback is the inverse
one-line flip.

If the rate is above the bar, leaving the mode at `report` is a successful
measurement, not a failed change.

## Guardrails

`scripts/ci/fixtures/validate-affected-shards.py` runs against the real
`.github/ci-shards.json`, offline. It proves that a change under a uniquely
owned feature namespace selects that namespace's shard (17 families), that a
changed test file selects the shard whose filter runs it (70 families), that a
class no shard would run on tier credits nobody, that the cap holds against a
`run_all` answer, that a `run_all` answer narrows by the closure while a
targeted one is never trimmed, that advisory shards stay out, and that matrix
entries carry every field the shard job and runner consume. Each of those is
paired with a failure injection, so a green run means the check is still
load-bearing.

It runs in **both** places, and the PR one is the load-bearing one:
`affected-shards-select` executes it before consuming the selector, and
`CI Router Validation` (`scripts/ci/validate-ci-router.sh`) runs it on trunk.
`ci.yml` has no `pull_request` trigger, so the trunk copy alone would never see
the pull request that *changes* the selector — an under-scoped selector or
shard-map edit would make both selection and aggregation green and land, and
only the trailing workflow would discover the broken fixture. AGENTS.md already
treats a diff touching the selectors as unable to scope itself; a lane
consuming its own PR-authored selector is the same problem.
