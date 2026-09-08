# Generated files on trunk

Repository-state projections are regenerated after merges by
`.github/workflows/generated-files-on-trunk.yml`. PR Gate runs the same generators
before the Fast/architecture validators and reports committed-file differences
with notices and a job summary. Generator failures and validation of authored
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

Trunk generation uses the existing `MERGE_TRAIN_TOKEN`, whose identity already
has ruleset bypass for the merge train. The workflow token remains read-only.
No new secret or branch-protection change is required. Generation runs only on
trusted trunk code. A serialized writer checks out current trunk, validates,
stages only the six outputs, and creates at most one commit with author and
committer `Mike McDougall <mike@honua.io>` and footer `Refs #3213`.

No diff produces no commit. The generated commit subject plus its
`Generated-From` trailer skip its own push-triggered job; a similarly titled
ordinary merge still regenerates.
Concurrency is scoped to that job, so skipped self-events cannot replace a
pending real merge in the writer queue. Reruns are idempotent for unchanged
inputs. Pushes never force or rebase generated blobs: if trunk advances during generation, the push
fails safely and the newer push's queued run regenerates from current trunk.
Transient push failures retry with 10/30/60/120-second backoff.

## Local proof

Run from an isolated worktree (the generators overwrite the six projections):

```bash
python3 scripts/ci/fixtures/validate-generated-files.py
bash scripts/ci/validate-ci-router.sh
UseSharedCompilation=true bash scripts/ci/regenerate-generated-files.sh --configuration Release /p:RunAnalyzers=false
bash scripts/ci/report-generated-file-drift.sh
bash scripts/ci/commit-generated-files.sh --dry-run
```

The explicit shared-compilation setting preserves the lane requirement even
when a host exports `UseSharedCompilation=false`; `dotnet` still resolves
through PATH and retains the lane CPU cap.

The final command shows the candidate diff without staging, committing, or
pushing. The real-Git contract test proves no-op handling, advisory drift,
identity, the output allowlist, staged-input rejection, repeat-run idempotence,
and rejection of a stale writer against a concurrently advanced trunk.

Branch validation on 2026-09-07: the four generated-file contracts, the
lean-gate command contract, actionlint for all three affected workflows, shell
syntax checks, and all 12 capability-matrix generator unit tests passed.
The complete `validate-ci-router.sh` suite also passed: 1,383 Server test
classes are claimed, all 73 shard filters select tests, and the four foundation
families cover 26 projects.

The local Release build and `FeatureCatalogEmitter` also passed using PATH's
lane-capped `dotnet`, shared compilation, and the trunk writer's analyzer policy.
All three emitters then passed through PATH's `dotnet vstest` with the same
environment variables and test filters as the existing generator wrappers.
This reused the completed binaries because the host's build-slot limiter also
queues `dotnet test --no-build`. Admin parity verification and both Python
generators passed (115 capabilities). The publication dry run reported only
`examples/manifest.json` drift: one candidate commit, without staging or pushing.
Restoring the committed snapshots and rerunning all emitters reproduced all six
SHA-256 hashes exactly. Against those fresh projections, CITE/OpenAPI validation,
67 focused architecture tests (catalog, GeoServices parity, and public-interface
proof ledger), and all three admin parity tests passed. Dry-run snapshots were
restored afterward; this change carries no generated-file or generator-source
edits. The full pre-PR build/test matrix was not run for this CI-only change.
