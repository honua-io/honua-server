# CITE Status — Authoritative Snapshot

Last reviewed: 2026-07-25
Owner: Honua Server platform

This page is the single fixed-path answer to "what is the current OGC CITE
pass rate for each protocol on `trunk`?" It exists so re-grading agents and
auditors can find an authoritative number without spelunking workflow artifacts.

**Source of truth.** This page is the single canonical snapshot of OGC CITE
per-suite pass rates. [`docs/contributor/ogc-cite-conformance-evidence.md`](internal/contributor/ogc-cite-conformance-evidence.md)
is the stable, website-linkable evidence-run narrative (workflow links,
artifact contents, refresh steps) and links here for the numbers rather than
restating them — see that page. The `x-honua-cite-compliance` vendor
extension in `src/Honua.Server/openapi.json` and the other four
`*-openapi.json` files also declares this page as its `authoritativeSource`,
and an architecture test
(`tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/CiteStatusComplianceDriftTests.cs`)
gates every one of those five files against the table below so they can
never silently drift from it.

**Local results note.** The per-suite result directories
(`cite-results/`, `cite-wfs20-results/`, `cite-tiles-results/`,
`cite-wms-results/`, `cite-wmts-results/`, `cite-gpkg12-results/`,
`cite-gml32-results/`, `cite-kml22-results/`, and `.tmp-cite/`) are gitignored.
The authoritative artifacts live in the GitHub Actions
[`CITE Evidence Report`](https://github.com/honua-io/honua-server/actions/workflows/cite-evidence-report.yml)
workflow runs. Do not infer the current pass rate from an empty local
directory — check the workflow.

## Current Per-Protocol Status

Snapshot copied from
[CITE Evidence Report run 30136438451](https://github.com/honua-io/honua-server/actions/runs/30136438451)
on `fix/3017-cite-evidence@5574f5bc82c2eb4b9753e91da1882ab2280fa301`, completed
2026-07-25T00:57:50Z. The `cite-conformance-evidence-3` bundle reported
`allPassed=true`: 1117 passed, 0
failed, 0 skipped, 0 CantTell.

| Suite | Profile | Passed / Total | Pass Rate | Last Evidence Run |
|---|---|---:|---:|---|
| OGC API Features 1.0 | `default` | 137 / 137 | 100% | 2026-07-25 |
| OGC API Tiles 1.0 | `default` | 16 / 16 | 100% | 2026-07-25 |
| GeoPackage 1.2 | `applicable` | 31 / 31 | 100% | 2026-07-25 |
| GML 3.2 | `applicable` | 17 / 17 | 100% | 2026-07-25 |
| KML 2.2 | `applicable` | 42 / 42 | 100% | 2026-07-25 |
| WFS 1.0 | `basic` | 162 / 162 | 100% | 2026-07-25 |
| WFS 1.1 | `basic` | 39 / 39 | 100% | 2026-07-25 |
| WFS 2.0 | `basic` | 167 / 167 | 100% | 2026-07-25 |
| WFS 2.0 Transactional | `transactional` | 25 / 25 | 100% | 2026-07-25 |
| WCS 2.0 | `core` | 82 / 82 | 100% | 2026-07-25 |
| WMS 1.1.1 | `default` | 126 / 126 | 100% | 2026-07-25 |
| WMS 1.3 | `default` | 213 / 213 | 100% | 2026-07-25 |
| WMTS 1.0 | `default` | 60 / 60 | 100% | 2026-07-25 |

The WFS 2.0 transactional leg (`cite-wfs20-transactional-results`) measures the
Transaction + LockFeature conformance classes independently from the `basic`
leg. WMS 1.1.1 is likewise a first-class evidence leg (`cite-wms11-results`);
the runner exercises version negotiation, 1.1.1 axis order,
`WMT_MS_Capabilities`, `application/vnd.ogc.se_xml` exceptions, `X`/`Y`
GetFeatureInfo, and `application/vnd.ogc.gml` GML FeatureInfo.

### Common Re-Grading Mistakes To Avoid

- **"WFS 2.0 CITE is 75% pass."** Incorrect. The `basic` profile is 167/167
  (100%) on the 2026-07-25 evidence run. The 75% figure does not match any
  published or archived result on `trunk` — likely a confusion with a
  partial-run diagnostic, an older branch, or the GML 3.2 `default` profile
  that intentionally loads inapplicable classes.
- **"No CITE results in the repo, so CITE is unimplemented."** Incorrect.
  Result directories are gitignored (see `.gitignore`); they only exist as CI
  artifacts. The workflows, runners, and Docker compositions all live under
  `.github/workflows/cite-*.yml`, `scripts/conformance/cite/`, and
  `docker/cite/` and are functional.
- **"Each `applicable` profile leaves classes skipped, so the suite is
  incomplete."** Incorrect for the public claim. Honua's public CITE evidence
  standard requires every reported assertion to pass and skipped/failed/CantTell
  to be zero in the chosen profile. The skipped counts that the raw ETS
  `default` profile shows for KML 2.2, GeoPackage 1.2, GML 3.2, etc. are for
  optional class families that Honua's exported documents do not exercise; the
  contributor doc explains each per-suite scope.

## Profile Scope, In One Line Each

- **OGC API Features `default`** — Part 1 Core, Part 2 CRS, Part 3 Filtering on
  the seeded fixture.
- **OGC API Tiles `default`** — vector + raster tiles against the seeded tile
  matrix sets.
- **GeoPackage 1.2 `applicable`** — core and feature classes for Honua's
  feature-only GeoPackage export; tile/extension/RTree/WebP classes are out of
  scope because the export does not include those families.
- **GML 3.2 `applicable`** — schema, feature-component, XML Schema validation,
  generic Schematron, property-value, and surface-geometry classes for the
  polygon GML document.
- **KML 2.2 `applicable`** — Level 1 classes for the generated KML document.
- **WFS 1.0 / 1.1 / 2.0 `basic`** — read, capabilities, temporal filters,
  spatial filters, response paging, and managed stored queries for the
  advertised profile. Locking, feature versioning, and spatial joins are not
  advertised by the `basic` profile and not in scope.
- **WFS 2.0 `transactional`** — the dedicated Transaction + LockFeature
  conformance-class leg.
- **WCS 2.0 `core`** — official ETS core profile, with preflight on
  `GetCapabilities`, `DescribeCoverage`, and `GetCoverage`.
- **WMS 1.1.1 / 1.3 `default`** — the official ETS default profiles.
- **WMTS 1.0 `default`** — the official ETS default profile.

## OGC API surfaces without an official CITE ETS

Some OGC API standards do not (yet) have an official CITE Executable Test Suite,
so they are not part of the 1117/1117 suite count above. They are still shipped as
conformant protocol adapters and proven with targeted integration tests plus an
accurate `/conformance` declaration:

- **OGC API – Styles (Part 1)** — `/ogc/styles`. Phase 1 adapter over Honua's
  per-layer style storage (ADR-0048, issue #1388). Declares `core`,
  `mapbox-styles`, `sld-10`, `sld-11`, `style-validation`, and
  `manage-styles` (Phase 2 promoted POST-create / DELETE to full CRUD; the
  Phase 1 disclosure that POST/DELETE returned `501` no longer applies).
  MapLibre is served from canonical storage; SLD 1.0/1.1 are derived on demand.
  **Conformance status: there is no official OGC API – Styles CITE/ETS
  executable test suite yet**, so this surface is not part of the 1117/1117 count
  above and there is no external pass-rate to report. Honua's status is proven
  by internal integration tests that exercise every claimed conformance class —
  `GetConformance_ListsThePhase1ConformanceClasses` asserts all six classes are
  declared, and sibling tests cover the read path (MapLibre + derived SLD
  1.0/1.1), `/metadata`, `style-validation` (`Prefer: handling=strict`), and the
  `manage-styles` PUT/POST/DELETE lifecycle — in
  `tests/dotnet/Honua.Server.Tests/Features/Styling/OgcStylesEndpointTests.cs`.
  When the official OGC Styles ETS becomes available it will be wired in like the
  other suites and reflected here (issue #1417 item 3). The canonical `styleId`
  surface supersedes the deprecated layerId-keyed style aliases
  (`/api/styles/{layerId}.json`, admin `…/layers/{layerId}/style`), which remain
  working but emit advisory `Deprecation`/`Sunset` headers pending removal.
  See [`docs/gis/style-engine-protocol-consumption.md`](guides/style/style-maps.md).

## Pending WPS 2.0.2 lane

Issue #2933 adds an official `ets-wps20` 1.1 harness for the Basic plus
Asynchronous or Synchronous certification paths. WPS is not included in the
table or the public pass total above until a complete selected-profile run has
nonzero tests with zero failures, skips, and indeterminate results. Harness
availability must not be presented as OGC certification or passing evidence.

## How To Refresh This Page

The
[`CITE Evidence Report` workflow](https://github.com/honua-io/honua-server/actions/workflows/cite-evidence-report.yml)
now runs on a weekly schedule (Friday 08:00 UTC, after the Wed/Thu per-suite
crons) in addition to `workflow_dispatch` (honua-server#2944). A scheduled or
manual run also asserts this page's "Last reviewed" date is no more than 14
days old (`scripts/ci/check-cite-status-freshness.sh`) — the workflow fails
and opens/updates a `cite-evidence` issue when either a suite regresses or
this page has gone stale, since the automated run does not itself rewrite the
hand-maintained table below.

1. Trigger the workflow on `trunk` (or wait for the weekly schedule).
2. Wait for `allPassed=true` (the workflow fails otherwise).
3. Copy the per-suite totals from the
   `cite-conformance-evidence-*` artifact's `conformance-summary.md` into the
   table above.
4. Update "Last reviewed", the run number, the commit SHA, and the run date.
5. If a suite regresses, update this page (the canonical numbers), the
   evidence-run narrative in `docs/contributor/ogc-cite-conformance-evidence.md`,
   and the `x-honua-cite-compliance` vendor extension in the affected
   `*-openapi.json` file(s) in the same commit — `CiteStatusComplianceDriftTests`
   fails the build if any of them disagree. Downgrade the public claim until
   the regression clears.

## Related Documents

- [`docs/contributor/ogc-cite-conformance-evidence.md`](internal/contributor/ogc-cite-conformance-evidence.md)
  — stable, website-linkable evidence-run narrative; see this page for the
  canonical per-suite numbers.
- [`docs/contributor/cite-runbook.md`](internal/contributor/cite-runbook.md) —
  per-suite scope, scripts, workflow files, and open issues.
- [`docs/contributor/ogc-certification-path.md`](internal/contributor/ogc-certification-path.md)
  — decision record on formal OGC certification posture.
- [`docs/contributor/CI_QUALITY_GATES.md`](internal/contributor/CI_QUALITY_GATES.md)
  — gate model that triggers each `cite-*-conformance.yml` workflow.
- [`docs/evidence/README.md`](internal/evidence/README.md) — top-level evidence index.
