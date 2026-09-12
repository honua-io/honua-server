# Generated files on trunk

Repository-state projections are regenerated after merges by
`.github/workflows/generated-files-on-trunk.yml`, which opens or refreshes a
PR carrying the diff rather than writing trunk directly (see "Execution and
write contract" below). PR Gate runs the same generators before the
Fast/architecture validators and reports committed-file differences with
notices and a job summary. Generator failures and validation of authored
inputs still fail. Generator implementation and serialization are unchanged.

## Inventory

| Checked-in output | Existing generator | Cadence |
| --- | --- | --- |
| `docs/gis/data/feature-catalog.json` | `scripts/generate-feature-catalog.sh` → `FeatureCatalogEmitter` / `FeatureCatalogGenerator` | Every trunk push |
| `docs/gis/data/admin-openapi-operation-ids.json` and `admin-mcp-projection-manifest.json` | `scripts/generate-admin-operation-parity-exports.sh` → `AdminOperationParityExportTests` | Every trunk push |
| `docs/gis/data/geoservices-rest-parity.json` | `scripts/generate-geoservices-parity.sh` → `GeoServicesParityEmitter` / `GeoServicesParityGenerator` | Every trunk push |
| `docs/gis/data/capability-matrix.v1.json` | `scripts/ci/generate-capability-matrix.py` (after catalog and parity) | Every trunk push |
| `examples/manifest.json` | `scripts/examples/generate-manifest.py` | Every trunk push |
| `src/Honua.Core/Features/Infrastructure/Crs/Resources/geoparquet-crs-projjson.json` | `scripts/geoparquet/generate-projjson-catalog.py` | Explicit CRS/PROJ dependency update; depends on external pyproj/PROJ data, not trunk evidence |
| `docs/gis/gap-report.md`, `docs/internal/compatibility/cross-server-consume-gap-report.md` | `scripts/client-compat/diff-baselines.py`, `scripts/ci/generate-cross-server-gap-report.sh` | Evidence-run snapshots; require measured results, external checkouts or live servers |
| `tests/fixtures/curated-edge-corpus/v1/sea-surface-temperature.zarr/temperature/0.0.0` | `scripts/test-data/generate-curated-corpus-zarr.py` | Explicit refresh of fixed sample values |
| `tests/fixtures/external-format-corpus/v1/*` binary fixtures | `scripts/test-data/generate-external-format-corpus.sh` | Explicit GDAL/OGR fixture refresh from authored GeoJSON inputs |
| `tests/dotnet/Honua.Core.Tests/Raster/CogParser/Fixtures/*` TIFF/pixel fixtures | `scripts/raster/generate-cog-fixtures.py` | Explicit rasterio/GDAL fixture refresh |

There are **no tracked `*.generated.*` files**. Compiler-generated JSON/logging,
OpenAPI runtime output and SDK bindings are build artifacts or package inputs.
The committed OpenAPI documents are authored contracts; their drift validators
remain strict.

The CI router fixtures (`scripts/ci/fixtures/*`,
`scripts/ci/merge-train/fixtures/*`, and cases in `validate-ci-router.sh`) are
**hand-authored assertions**, not generator output. `.github/ci-shards.json`
is also authored routing policy. No router generator or checked-in generated
router snapshot exists in this inventory; these validators remain strict.

`public-interface-proof.json`, capability keys/mappings/allowlists/maturity
judgments, GeoServices judgments, certification receipts, and stranded-merge
dispositions are authored inputs or evidence. They are never auto-rewritten.
The review-first, impact-routing and server-test-prebuild evidence ledgers are
workflow artifacts produced by their existing ledger/audit scripts, not
checked-in projections. The SDK compatibility table/summary
(`scripts/ci/generate-sdk-compatibility-table.sh`), canonical CNG fixtures
(`scripts/conformance/cng/generate-canonical-fixtures.py`), migration evidence
packs and protocol-harness fragments are also run artifacts. Their provenance
and validation stay intact.

## Execution and write contract

The single allowlist is `scripts/ci/generated-files.sh`. Regeneration uses the
existing emitters in dependency order. Like the existing trailing foundation
build, the trunk writer skips the duplicate analyzer pass with
`/p:RunAnalyzers=false`; PR Gate still enforces analyzers and warnings-as-errors.
Compiler errors, generator execution, and authored-input tests remain strict.
PR Gate already builds their test assemblies, so its invocation uses
`--no-build --no-restore`. Existing byte
equality tests run against fresh projections and still catch nondeterminism;
proof-ledger, schema, route coverage, judgment and OpenAPI checks stay hard.
The trailing Server foundation family also refreshes before its validators,
so it cannot race the post-merge writer against stale committed projections.
The separate capability aggregation check now also reports drift with a notice.
The existing normalization producer/consumer remains in observation mode and
continues its bounded reproducibility checks without modifying PR branches.

Generation and publication run in a single job on one runner, with the
default read-only-to-`contents` `GITHUB_TOKEN` widened only to `contents:
write` / `pull-requests: write` (no new secret). It checks out trunk, runs the
generators, MSBuild and tests exactly as above, then hands any diff to
`scripts/ci/publish-generated-files.sh`.

**This workflow never writes trunk directly.** The first version of this
workflow (#4540) committed on the checked-out trunk ref and pushed straight to
`refs/heads/trunk` with a ruleset-bypass PAT (`MERGE_TRAIN_TOKEN`). Trunk's
trailing matrix caught it one merge later (#4653):
`scripts/ci/validate-single-merge-authority.sh` exists to prove trunk has
exactly one writer (the merge train / per-PR lander path), and a second script
with bypass authority to move trunk without review is precisely the
multi-writer condition it is written to reject. Weakening the guard to admit
that push would defeat its purpose, so this version instead commits the diff
on a fixed branch (`automation/regenerate-generated-files`, author and
committer `github-actions[bot]`), force-with-lease pushes only that branch,
and opens (or refreshes, if one is already open) a PR from it into trunk —
the same pattern `cross-server-consume-nightly.yml`'s gap-report refresh
already uses. The regenerated bytes then land through the ordinary PR Gate +
Review Gate + per-PR lander path, so there is no second trunk writer to prove
anything about, and the isolated-writer/untrusted-bundle machinery the first
version needed to make a direct trunk push safe is no longer necessary.

No diff produces no commit and no PR activity. Force-with-lease is confined to
the fixed automation branch, which can never resolve to trunk, so a concurrent
run can only ever race itself for that branch, never trunk. A push-triggered
run whose diff is byte-identical to what a previous run already proposed (for
example, the run triggered by that automation PR's own merge) finds nothing to
publish and exits at the diff check; no self-trigger provenance guard is
needed for that case, unlike the direct-push design it replaces.

## Local proof

Run from an isolated worktree (the generators overwrite the six projections):

```bash
python3 scripts/ci/fixtures/validate-generated-files.py
bash scripts/ci/validate-ci-router.sh
UseSharedCompilation=true bash scripts/ci/regenerate-generated-files.sh --configuration Release /p:RunAnalyzers=false
bash scripts/ci/report-generated-file-drift.sh
```

The explicit shared-compilation setting preserves the lane requirement even
when a host exports `UseSharedCompilation=false`; `dotnet` still resolves
through PATH and retains the lane CPU cap.

`report-generated-file-drift.sh` shows the candidate diff without staging,
committing, pushing, or opening a PR. The real-Git contract test proves no-op
handling, advisory drift, the commit identity, and the fixed-branch push/PR
lifecycle (create when none is open, refresh when one already is) against a
real local remote with a stubbed `gh`.

Branch validation on 2026-09-11 (re-land after #4540/#4653): all five real-Git,
lean-gate-command, and workflow-wiring contracts in
`scripts/ci/fixtures/validate-generated-files.py` passed, including a real
local Git remote exercising `scripts/ci/publish-generated-files.sh` end to
end with a stubbed `gh` — no-op on a clean diff, a commit + fixed-branch push
+ `gh pr create` on a real diff (trunk never moves), and `gh pr list`-driven
reuse of the same open PR on a second run instead of a duplicate. The complete
`scripts/ci/validate-ci-router.sh` suite passed against the real tree (1,433
Server test classes claimed, all 74 shard filters select tests, four
foundation families covering 26 projects), and both
`scripts/ci/validate-single-merge-authority.sh --self-test` and a real scan of
the tree passed — the load-bearing proof for this packet, since #4540's
version of this same tree failed exactly that scan. Generator implementation
and output contracts are byte-for-byte unchanged from #4540's original
validation (repeated here, not re-verified): the Release build, the three
emitters via PATH's lane-capped `dotnet`/`dotnet vstest`, both Python
generators, and CITE/OpenAPI/architecture validation against fresh
projections all passed then and are not touched by this packet. The full
pre-PR build/test matrix was not run for this CI-only change.
