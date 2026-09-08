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
| `docs/gis/gap-report.md`, cross-server gap report and SDK compatibility table snapshots | `scripts/client-compat/diff-baselines.py`, `scripts/ci/generate-cross-server-gap-report.sh`, `scripts/ci/generate-sdk-compatibility-table.sh` | Evidence-run outputs; require measured results, external checkouts or live servers |
| COG, canonical CNG and curated format corpus fixtures | `scripts/raster/generate-cog-fixtures.py`, `scripts/conformance/cng/generate-canonical-fixtures.py`, `scripts/test-data/generate-*` | Explicit fixture refresh with GDAL/external tooling; runtime test data, not trunk projections |

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
checked-in projections. Their provenance and validation stay intact.

## Execution and write contract

The single allowlist is `scripts/ci/generated-files.sh`. Regeneration uses the
existing emitters in dependency order. The PR Gate already builds their test
assemblies, so its invocation uses `--no-build --no-restore`. Existing byte
equality tests run against fresh projections and still catch nondeterminism;
proof-ledger, schema, route coverage, judgment and OpenAPI checks stay hard.
The separate capability aggregation check now also reports drift with a notice.
The existing normalization producer/consumer remains in observation mode and
continues its bounded reproducibility checks without modifying PR branches.

Trunk generation uses the existing `MERGE_TRAIN_TOKEN`, whose identity already
has ruleset bypass for the merge train. The workflow token remains read-only.
No new secret or branch-protection change is required. Generation runs only on
trusted trunk code. A serialized writer checks out current trunk, validates,
stages only the six outputs, and creates at most one commit with author and
committer `Mike McDougall <mike@honua.io>` and footer `Refs #3213`.

No diff produces no commit. The exact generated commit subject skips its own
push-triggered job. Reruns are idempotent for unchanged inputs. Pushes never
force or rebase generated blobs: if trunk advances during generation, the push
fails safely and the newer push's queued run regenerates from current trunk.
Transient push failures retry with 10/30/60/120-second backoff.

## Local proof

Run from an isolated worktree (the generators overwrite the six projections):

```bash
python3 scripts/ci/fixtures/validate-generated-files.py
bash scripts/ci/validate-ci-router.sh
bash scripts/ci/regenerate-generated-files.sh --configuration Release
bash scripts/ci/report-generated-file-drift.sh
bash scripts/ci/commit-generated-files.sh --dry-run
```

The final command shows the candidate diff without staging, committing, or
pushing. The real-Git contract test proves no-op handling, advisory drift,
identity, the output allowlist, staged-input rejection, repeat-run idempotence,
and rejection of a stale writer against a concurrently advanced trunk.
