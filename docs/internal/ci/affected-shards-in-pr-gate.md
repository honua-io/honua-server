# Affected shard families in PR Gate

Status: **enforcing**, required through the stable `PR Gate` context. Owner: `.github/workflows/pr-gate.yml`
(`affected-shards-select` / `affected-shard` / `affected-shards`).
Related: ADR-0037 (shard routing), ADR-0043 (affected projects), #3204, #3213.

## The gap this closes

`PR Gate` is a lean subset — build, fast unit, architecture — on one runner.
The server-test shard families in `.github/ci-shards.json` run per trunk tip
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
   routes on**. This includes telemetry contracts and GP, plugin and toolbox
   fixtures outside those trees. Their consumers must run when an input is
   the only changed file.
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
5. **Spend the cap and the time budget.** PR Gate passes `--time-budget`, which
   applies `pr_time_budget` from `.github/ci-shards.json`
   (`max_shard_minutes: 10`, `max_total_minutes: 45`). Walking the ranking, a
   shard whose `dispatch_rank` exceeds `max_shard_minutes` is deferred, a shard
   that would push the selection past `max_total_minutes` runner-minutes is
   deferred, and once six are selected the rest are dropped. Deferred families
   are listed under `deferred_to_trunk` in the receipt, the step summary and the
   `deferred_to_trunk` job output; the post-merge trunk run covers them.
6. **All deferred is green, not skipped.** When every candidate is deferred the
   selection skips with `affected_shards_deferred_to_trunk`, and
   `scripts/ci/affected-shard-verdict.py` reports a green `deferred` verdict
   that names the deferred families. It is not a test pass. A deferred skip that
   names no family, or that also selected one, fails closed as `untrusted_skip`,
   and a selected shard that fails or leaves no receipt still fails the context.

`dispatch_rank` is the measured p50 job duration in minutes over green trunk
full runs. Refresh it from measured runs whenever shards are split or merged:
the time budget is only as accurate as these numbers.

### Why `test_class_hits` is the term that matters

Shard `paths` name individual test *files* for the protocol-split projects, so
a diff that **adds** a test file to a directory a shard already owns files in is
claimed by no routing entry at all and escalates to `run_all` — leaving the cap
to choose among the full inventory with no routing signal. On the merge commits of
#4465 and #4489 this term moves the family that actually went red on trunk to
rank 4 and rank 2. Without it, #4465's owner sat at rank 10, outside the cap.

That routing gap is worth closing in `.github/ci-shards.json` itself (directory
entries rather than exact files for the protocol test projects), but doing so
changes trailing-matrix routing and belongs in its own change.

### Why the cap is not negotiable

A `run_all` answer includes the complete shard inventory. Fanning that out per pull request is the
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

This window predates the time budget. Minutes are `dispatch_rank`, the observed shard duration ci.yml dispatches by;
runner-minutes are the sum over selected shards and wall clock is the maximum,
since the shards run as a parallel matrix. The lane runs beside the required
`PR Gate / Build and tests` job (~18.7 min p50), and its selected executions contribute to the
required context's critical path. These measurements describe the original window,
not a guarantee about current gate duration.

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

## Required execution verdict

`AFFECTED_SHARDS_MODE` in `.github/workflows/pr-gate.yml` is `enforce`.
The per-shard test step preserves results with `continue-on-error`; its job
conclusion is not execution evidence. The aggregate reads exactly one timing
receipt per selected family and uses the existing `shard-execution-evidence.py`
verifier to re-read each passing receipt's exact TRX. The measured status,
counts and digest must agree. Failed, timed-out, missing or inconsistent results
fail `PR Gate / Affected shards` and the stable required `PR Gate` context through
`required.needs`. Branch protection requires no additional context.
The shard job can conclude success after a test failure; re-running only the
aggregate does not repeat the selected tests.

Only empty selections with `docs_only`, `validated_generated_only`,
`no_product_code` or `no_shards_selected` may skip successfully. An
`untrustworthy_base` or unknown skip reason fails closed in enforce mode.
A successful skip is not a test pass. The six-family fan-out cap and each
shard's existing test and job budgets are unchanged.

The founder authorized enforcement after real selected-shard failures merged
through report mode. The retained false-red audit still contains unresolved
classifications; this policy decision does not claim that its historical
promotion threshold passed. The evidence and disposition remain in
[the enforcement and promotion record](../../ci/affected-shards-promotion.md).

The verdict tool retains explicit `report` mode for historical replay. In that
mode red or unavailable evidence can publish a successful check, but
`test_pass` is always false. Reverting the workflow to that behavior permits
selected failures to merge and requires an explicit policy decision.

## Guardrails

`scripts/ci/fixtures/validate-affected-shards.py` runs against the real
`.github/ci-shards.json`, offline. It proves that a change under a uniquely
owned feature namespace selects that namespace's shard (17 families), that a
changed test file selects the shard whose filter runs it (70 families), that a
class no shard would run on tier credits nobody, that a changed test falling
through to the router's `no_path_match` default still selects the shard that
runs it, that every root the shard map routes outside `src/`/`tests/dotnet/`
counts as product code while the router's `infrastructure_paths` still do not,
that the cap holds against a `run_all` answer, that a `run_all` answer narrows
by the closure while a targeted one is never trimmed, that advisory shards stay
out, and that matrix entries carry every field the shard job and runner
consume. Each of those is paired with a failure injection, so a green run means
the check is still load-bearing. It takes about a minute, offline.

It runs in **both** places, and the PR one is the load-bearing one:
`affected-shards-select` executes it before consuming the selector, and
`CI Router Validation` (`scripts/ci/validate-ci-router.sh`) runs it on trunk.
`ci.yml` has no `pull_request` trigger, so the trunk copy alone would never see
the pull request that *changes* the selector — an under-scoped selector or
shard-map edit would make both selection and aggregation green and land, and
only the trailing workflow would discover the broken fixture. AGENTS.md already
treats a diff touching the selectors as unable to scope itself; a lane
consuming its own PR-authored selector is the same problem.
