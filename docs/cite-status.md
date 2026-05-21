# CITE Status — Authoritative Snapshot

Last reviewed: 2026-05-20
Owner: Honua Server platform

This page is the single fixed-path answer to "what is the current OGC CITE
pass rate for each protocol on `trunk`?" It exists so re-grading agents and
auditors can find an authoritative number without spelunking workflow artifacts.

**Source of truth.** This page mirrors the totals in
[`docs/contributor/ogc-cite-conformance-evidence.md`](contributor/ogc-cite-conformance-evidence.md),
which is the canonical, website-linkable summary. When the two diverge, the
contributor doc wins and this page must be re-synchronized.

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
[CITE Evidence Report run 26005533282](https://github.com/honua-io/honua-server/actions/runs/26005533282)
on `trunk@cf05fae8509a4b8b2989425e54cc58f7ab7c8f1f`, completed
2026-05-17T23:33:07Z. The bundle reported `allPassed=true`: 952 passed, 0
failed, 0 skipped, 0 CantTell.

| Suite | Profile | Passed / Total | Pass Rate | Last Evidence Run |
|---|---|---:|---:|---|
| OGC API Features 1.0 | `default` | 137 / 137 | 100% | 2026-05-17 |
| OGC API Tiles 1.0 | `default` | 16 / 16 | 100% | 2026-05-17 |
| GeoPackage 1.2 | `applicable` | 31 / 31 | 100% | 2026-05-17 |
| GML 3.2 | `applicable` | 17 / 17 | 100% | 2026-05-17 |
| KML 2.2 | `applicable` | 42 / 42 | 100% | 2026-05-17 |
| WFS 1.0 | `basic` | 162 / 162 | 100% | 2026-05-17 |
| WFS 1.1 | `basic` | 39 / 39 | 100% | 2026-05-17 |
| WFS 2.0 | `basic` | 167 / 167 | 100% | 2026-05-17 |
| WCS 2.0 | `core` | 82 / 82 | 100% | 2026-05-17 |
| WMS 1.3 | `default` | 199 / 199 | 100% | 2026-05-17 |
| WMTS 1.0 | `default` | 60 / 60 | 100% | 2026-05-17 |

The WFS 2.0 explicit transactional slice is tracked separately and passes
25 / 25 with 0 failed and 0 skipped as of the same run.

### Common Re-Grading Mistakes To Avoid

- **"WFS 2.0 CITE is 75% pass."** Incorrect. The `basic` profile is 167/167
  (100%) on the 2026-05-17 evidence run. The 75% figure does not match any
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
  spatial filters, response paging, transactions, and managed stored queries
  for the advertised profile. Locking, feature versioning, and spatial joins
  are not advertised and not in scope.
- **WCS 2.0 `core`** — official ETS core profile, with preflight on
  `GetCapabilities`, `DescribeCoverage`, and `GetCoverage`.
- **WMS 1.3 `default`** — the official ETS default profile.
- **WMTS 1.0 `default`** — the official ETS default profile.

## How To Refresh This Page

1. Trigger the
   [`CITE Evidence Report` workflow](https://github.com/honua-io/honua-server/actions/workflows/cite-evidence-report.yml)
   on `trunk`.
2. Wait for `allPassed=true` (the workflow fails otherwise).
3. Copy the per-suite totals from the
   `cite-conformance-evidence-*` artifact's `conformance-summary.md` into the
   table above.
4. Update "Last reviewed", the run number, the commit SHA, and the run date.
5. If a suite regresses, both this page and
   `docs/contributor/ogc-cite-conformance-evidence.md` must be updated in the
   same commit, and the public claim should be downgraded until the regression
   clears.

## Related Documents

- [`docs/contributor/ogc-cite-conformance-evidence.md`](contributor/ogc-cite-conformance-evidence.md)
  — canonical, website-linkable summary (source of truth).
- [`docs/contributor/cite-runbook.md`](contributor/cite-runbook.md) —
  per-suite scope, scripts, workflow files, and open issues.
- [`docs/contributor/ogc-certification-path.md`](contributor/ogc-certification-path.md)
  — decision record on formal OGC certification posture.
- [`docs/contributor/CI_QUALITY_GATES.md`](contributor/CI_QUALITY_GATES.md)
  — gate model that triggers each `cite-*-conformance.yml` workflow.
- [`docs/evidence/README.md`](evidence/README.md) — top-level evidence index.
