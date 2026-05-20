# Migration Performance Evidence

Last reviewed: 2026-05-19

This page is the website-linkable home of Honua's measured migration cost and
performance evidence. It documents the
`honua.migration.performance-evidence` artifact that the release-gated
[Release Migration Performance Evidence workflow](../../.github/workflows/release-migration-performance.yml)
emits and explains how reviewers should consume it.

The artifact ships under issue
[honua-server#1033](https://github.com/honua-io/honua-server/issues/1033)
("Add migration cost and performance evidence"). It is published in five
incremental slices:

| Slice | Scope | Status |
|---|---|---|
| 1 | Metric schema + instrumentation (`MigrationRunMetricsArtifact`) | Merged in [#1092](https://github.com/honua-io/honua-server/pull/1092) |
| 2 | Fixture sizing + baseline thresholds (`MigrationMetricBaseline`, evaluator) | Merged in [#1110](https://github.com/honua-io/honua-server/pull/1110) |
| 3 | Retry / resume / idempotency tests + checkpoint store | Merged in [#1112](https://github.com/honua-io/honua-server/pull/1112) |
| 4 | **Artifact generation + documentation (this page)** | This slice |
| 5 | Admin / SDK display of performance evidence | Deferred |

## Why this artifact exists

The buyer-facing migration claim says Honua migration is _both_ functional and
predictable enough to cost out. Functional parity is proven by the migration
acceptance suite ([#1024](https://github.com/honua-io/honua-server/issues/1024)
and the [Compatibility and Migration Evidence](../contributor/compatibility-and-migration-evidence.md)
index). Predictability requires _measured_ duration, throughput, retry
behavior, resource usage, database growth, artifact size, and manual-review
ratio against a deterministic fixture. That is what slice 4 publishes.

## What the artifact contains

`MigrationPerformanceEvidenceArtifact` bundles four release-safe pieces:

1. **Slice-1 raw run metrics** (`MigrationRunMetricsArtifact`). Per-phase and
   aggregate measurements collected during a scan / manifest / apply / import
   run on a deterministic fixture. URLs, credentials, query strings, and
   source feature payloads are stripped at recording time.
2. **Slice-2 baseline classification** (`MigrationRunMetricsBaselineArtifact`).
   Each metric is classified `Pass`, `Warn`, or `Fail` against the per-fixture
   baseline bands seeded in `MigrationFixtureBaselineCatalog` and the run
   carries an aggregate status.
3. **Fixture metadata** (`MigrationFixtureSizeProfile`). The expected envelope
   (resources, features, coverages, duration, bytes, source requests) the
   baseline was calibrated against. Reviewers cite this when explaining what
   the run actually exercised.
4. **SHA-256 fingerprint** over a canonical view of the three inputs above.
   The fingerprint lets release reviewers prove the published artifact
   summarizes the same measurements and baseline the workflow evaluated.
   Building the same inputs twice produces an identical fingerprint regardless
   of formatting or machine. Use the fingerprint when correlating workflow
   logs with the published JSON.

A fifth field, `Redaction`, carries the deny-by-default privacy posture for
the artifact: `sourceUrlsIncluded`, `credentialValuesIncluded`,
`sourceDataIncluded`, and `operatorIdentitiesIncluded` are all `false`, and
`omittedFields` lists the categories the builder excludes.

## How the workflow runs it

The
[`release-migration-performance.yml`](../../.github/workflows/release-migration-performance.yml)
workflow:

- Restores and builds `tests/dotnet/Honua.Core.Tests`.
- Runs the fixture-driven baseline check (the slice-1 metrics builder ->
  slice-2 baseline evaluator -> slice-4 evidence builder pipeline is exercised
  by `MigrationPerformanceEvidenceBuilderTests` and
  `MigrationRunMetricsBaselineEvaluatorTests`).
- Uploads the resulting JSON artifact plus the `trx` test log as the workflow
  artifact `migration-performance-evidence-${{ github.run_id }}` with 90-day
  retention.

The workflow triggers on `workflow_dispatch`, the GitHub `release.published`
event, and a nightly `04:00 UTC` schedule so the evidence stays fresh between
releases.

Currently seeded fixture coverage:

- GeoServer REST small (`geoserver-small-v1`): up to 20 workspaces, 200
  layers, 10k features, 60s wall-clock envelope. Medium and large GeoServer
  bands plus ArcGIS, OGC Features, OGC map/tile-metadata, and coverage
  baselines are deferred to follow-on slices so each can be calibrated
  against a real fixture run.

## Latest passing run

The first cut of this artifact ships with this PR. The latest passing
release-gated run will be linked here once the workflow has executed on
trunk. Reviewers should always cite the most recent successful
`migration-performance-evidence-*` workflow run when using the website-safe
"minimal-cost migration" wording below.

## Website-safe claim wording

Once a passing release-gated artifact is linked above, the
"minimal-cost migration" sentence in the
[Compatibility and Migration Evidence](../contributor/compatibility-and-migration-evidence.md#claim-2-automated-migration)
page may cite this page directly. Until then, keep the existing
"current import tests prove correctness slices, not migration duration ..."
wording.

The artifact's `Status` field maps to claim language as follows:

| Artifact status | Claim wording allowed |
|---|---|
| `Pass` | "measured migration cost is within the published baseline for the named source family and fixture size" |
| `Warn` | "measured migration cost is within the published baseline with X warnings; see linked artifact for review" |
| `Fail` | Claim must be withdrawn or scoped down until a `Pass` or `Warn` run is published |

## Schema stability

The artifact schema is pinned by
`MigrationPerformanceEvidenceBuilderTests.ArtifactSchema_TopLevelProperties_AreStable`
and
`RedactionPosture_TopLevelProperties_AreStable`. Adding a field is allowed;
renaming or removing a field is a deliberate contract change and must update
this page in the same commit.

## Related evidence

- [Compatibility and Migration Evidence](../contributor/compatibility-and-migration-evidence.md) -
  cross-cutting migration claim governance.
- [Import and Migration Capability Evidence](../contributor/import-capability-evidence.md) -
  per-source-family scan / inventory / apply evidence.
- [Process Migration Evidence](../contributor/process-migration-evidence.md) -
  geoprocessing workload migration slice.
- [Evidence index](README.md) - the rest of Honua's evidence tree.
