# Generated files on trunk

Repository-state projections are regenerated nightly (10:30 UTC) and on demand
(`workflow_dispatch`) by
`.github/workflows/generated-files-on-trunk.yml`, which opens or refreshes a
PR carrying the diff rather than writing trunk directly (see "Execution and
write contract" below). PR Gate runs the same generators before the
Fast/architecture validators and reports committed-file differences with
notices and a job summary. Generator failures and validation of authored
inputs still fail. Generator implementation and serialization are unchanged.

## Inventory

| Checked-in output | Existing generator | Cadence |
| --- | --- | --- |
| `docs/gis/data/feature-catalog.json` | `scripts/generate-feature-catalog.sh` → `FeatureCatalogEmitter` / `FeatureCatalogGenerator` | Nightly / on demand |
| `docs/gis/data/admin-openapi-operation-ids.json` and `admin-mcp-projection-manifest.json` | `scripts/generate-admin-operation-parity-exports.sh` → `AdminOperationParityExportTests` | Nightly / on demand |
| `docs/gis/data/geoservices-rest-parity.json` | `scripts/generate-geoservices-parity.sh` → `GeoServicesParityEmitter` / `GeoServicesParityGenerator` | Nightly / on demand |
| `docs/gis/data/capability-matrix.v1.json` | `scripts/ci/generate-capability-matrix.py` (after catalog and parity) | Nightly / on demand |
| `docs/okf/capabilities` | `scripts/ci/generate-capability-concepts.py` (after the capability matrix) | Nightly / on demand |
| `examples/manifest.json` | `scripts/examples/generate-manifest.py` | Nightly / on demand |
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

Generation and validation check out the triggering `github.sha`, including
on reruns, with read-only contents/packages permissions and no persisted Git
credentials. The final publication step uses the existing `MERGE_TRAIN_TOKEN`
for PR creation and the fixed automation-branch push. The default Actions token
cannot create PRs under the repository's Actions policy and suppresses the
push/PR events needed to start admission checks; there is no fallback to it.
The PAT is not passed to generators, MSBuild or tests. PR lookup and creation
call the REST pulls API (`gh api repos/<repo>/pulls`), never `gh pr`: that
account's GraphQL budget is shared with other automation, and three trunk runs
on 2026-09-13 pushed the branch and then failed `gh pr list` with a GraphQL
rate-limit error while REST quota remained (#4732).

Before committing a diff, publication compares that validated source with
remote trunk. If trunk has already advanced, it reports both identities and leaves the
automation PR untouched. Validation still proves the triggering source; it
does not check out or certify the later trunk revision. A new source/workflow
fix requires a new imaged release candidate and certification at that identity.
Network failures reading remote refs fail publication rather than masquerading
as an absent branch. The push uses an explicit lease and a temporary Git
credential helper; no PAT is persisted in the checkout. The automation-branch
lease is captured before observing trunk, so a racing newer publisher cannot
be overwritten. This is not an atomic trunk-freshness/admission guarantee:
trunk can advance during publication or review. As in #4695, these are ordinary
reviewed maintenance PRs and subsequent trunk pushes schedule another refresh.
The source trailer and triggering-SHA validation identify exactly what was
validated; neither claims that trunk stopped moving.

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
committer `Mike McDougall <mike@honua.io>`), force-with-lease pushes only that branch,
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

Run from an isolated worktree (the generators overwrite the seven projections):

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

## Validated generated-only admission

PR Gate can omit product builds, tests, format and affected shards when the
entire exact base-to-head diff changes only regular, non-executable generated
outputs. The base commit supplies both `scripts/ci/generated-files.sh` and the
executable `scripts/ci/generated-output-diff.py` policy; candidate policy edits
cannot authorize their own exemption. Renames, deletions, type/mode changes,
forks, source, generator, workflow and mixed changes retain ordinary checks.
A single publication commit must have the event's exact trunk base as its parent.

An allowlisted diff alone does not suffice. After its existing generation and
validation and final PR publication, the nightly producer uploads
`generated-output-proof-<run>-attempt-<attempt>`. The bounded JSON artifact binds
its triggering source, output commit and complete tree, run ID and attempt.
Admission reads it only from the canonical, successful completed trunk
schedule/manual producer in this repository. Branch names, authors, labels and
`Generated-From` trailers do not confer trust. Admission waits up to 90 seconds
for an already-running producer to finish publishing its receipt. Missing,
expired, failed, stale or unreadable proof falls back to ordinary verification;
it never dispatches another producer. An older producer without the artifact
also takes the ordinary path.

The named PR Gate jobs and stable `PR Gate` aggregate still run and complete.
Their existing lightweight admission contracts and exact-head Review Gate
requirements stay intact. The building-block conformance job completes without
building or running validators. The normalization producer completes without
repeating generation, and its trusted consumer independently proves the same
exemption before skipping envelope validation and mutation. The repository-managed `codeql.yml` source/workflow path filter excludes every
canonical generated output. GitHub's separate dynamic Code Quality service
(`dynamic/github-code-quality/codeql`) is outside that workflow: cancelled run
[37817472021](https://github.com/honua-io/honua-server/actions/runs/37817472021)
on #5725 ran C# analysis despite that filter. Its documented
[setup API](https://docs.github.com/en/rest/code-quality/code-quality) exposes
repository enablement, languages and runner settings, with no per-PR path
filter. This repository routing does not yet prevent that dynamic service from
starting; changing `codeql.yml` cannot establish that proof. Ordinary changes retain the existing product
assertions and gates; no workflow is disabled and publication still goes through
reviewed PR admission. Classification runs under read-only Actions/contents
credentials and never executes code from the PR head with a write credential.

Offline routing proof (no .NET build or manual regeneration):

```bash
python3 scripts/ci/fixtures/validate-generated-output-diff.py
python3 scripts/ci/fixtures/validate-generated-files.py
bash scripts/ci/validate-ci-router.sh
bash scripts/ci/validate-single-merge-authority.sh
```

The fixture applies the actual four-file #5725 patch in a scratch Git repository
and supplies mocked Actions producer receipts. It also rejects untrusted and
malformed diffs/proofs, exercises publication timing and classifier failures,
and checks named-context completion and ordinary product-step routing. This is
local contract evidence only. A fresh nightly generated-only PR after this fix
merges must provide the hosted proof that the expensive steps actually skip;
that live verification is not claimed by these fixtures.
