ci: cap the three workflow jobs added since the last timeout sweep
## Why

An uncapped job that hangs holds a runner slot for GitHub's default of **6 hours** and shows up downstream as an unexplained queue. Phase 0 of the CI efficiency plan is to make sure no job can do that. #4505 capped every runner-consuming job on trunk as of 2026-09-07; three new uncapped jobs have landed since.

## Enumeration

Scripted sweep (`yaml.safe_load` over every `.github/workflows/*.yml`, jobs with neither `timeout-minutes` nor `uses:`) on trunk `c98cde3203`:

- **3** runner-consuming jobs had no cap. This PR fixes all three.
- Reusable-workflow callers (`uses: ./.github/workflows/...`) are excluded: `timeout-minutes` is not a valid key on a caller job; their called workflows are already capped (see #4505).

Re-running the sweep after this change: **0** uncapped runner jobs.

## Caps applied

Observed p99 over the last 30 days via the Actions API (completed, non-skipped, non-cancelled jobs), rounded up to the next 5 minutes, minimum 10, never below 1.5x p99; no history -> 60.

| Workflow | Job | Samples (30d) | p99 | Median | Cap |
|---|---|---:|---:|---:|---:|
| `cloud-post-apply-validation-nightly.yml` | `operator-preflight` | 16 | 0.07 min | 0.05 min | **10** |
| `standing-aws-cert-plan.yml` | `controls` (Plan-only workflow controls (offline)) | 21 | 0.25 min | 0.20 min | **10** |
| `cng-candidate.yml` | `identity` | 0 (no runs in window) | - | - | **60** |

## Proof

- Sweep: 0 uncapped runner jobs after the change.
- `actionlint` 1.7.7 on the three changed files: clean. Repo-wide findings (e.g. `portal-group-discovery-contract.yml` `optimize-disk` input) are in untouched files and pre-existing on trunk.
- `scripts/cloud/test-standing-cert-plan-only.py` (the contract test for `standing-aws-cert-plan.yml`), in `python:3.13-slim`: 18 tests OK.
- `tests/python/unit/test_capacity_runner_routing.py`, in `python:3.13-slim`: 51 passed.
- `scripts/ci/validate-docker-gate-timeouts.py`: `job=30m, bounded_steps=10` valid.
- `scripts/ci/validate-tracked-file-encoding.py`: clean.

No other workflow changes.

## Follow-up

- New jobs keep landing uncapped between sweeps; a contract check that fails on any runner job without `timeout-minutes` would make this sweep permanent (not added here to keep the change to caps only).

Refs #3213
