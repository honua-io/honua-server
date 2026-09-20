---
type: reference
title: "CITE status"
description: "The authoritative snapshot of Honua's OGC CITE conformance runs."
---
# CITE Status — Authoritative Snapshot

Last reviewed: 2026-09-15
Owner: Honua Server platform

This page is the single fixed-path answer to "what is the current OGC CITE
pass rate for each protocol on `trunk`?" It exists so that number has one address.

Every row is backed by a run of the CITE Evidence Report workflow, which runs
weekly and fails if any suite regresses or this page goes stale.

## Current Per-Protocol Status

Every row is backed by
[CITE Evidence Report run 34994919144](https://github.com/honua-io/honua-server/actions/runs/34994919144)
and its `cite-conformance-evidence-21` bundle, built from source commit
`b8ea218d07a52fe025d9382063d11b9b2c17c922` and started 2026-09-15. It reports
1138 passed, 0 failed, 0 skipped, and 0 CantTell across 14 suites
(`allPassed=true`). OGC API Features includes the complete Part 2 CRS class.

| Suite | Profile | Passed / Total | Pass Rate | Last Evidence Run |
|---|---|---:|---:|---|
| OGC API Features 1.0 | `default` | 137 / 137 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| OGC API Tiles 1.0 | `default` | 16 / 16 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| GeoPackage 1.2 | `applicable` | 31 / 31 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| GML 3.2 | `applicable` | 17 / 17 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| KML 2.2 | `applicable` | 42 / 42 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WFS 1.0 | `basic` | 162 / 162 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WFS 1.1 | `basic` | 39 / 39 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WFS 2.0 | `basic` | 167 / 167 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WFS 2.0 Transactional | `transactional` | 25 / 25 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WCS 2.0 | `core` | 82 / 82 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WPS 2.0 | `basic-async` | 21 / 21 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WMS 1.1.1 | `default` | 126 / 126 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WMS 1.3 | `default` | 213 / 213 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |
| WMTS 1.0 | `default` | 60 / 60 | 100% | [2026-09-15](https://github.com/honua-io/honua-server/actions/runs/34994919144) |

The WFS 2.0 transactional leg (`cite-wfs20-transactional-results`) measures
TransactionalWFS independently from the `basic` leg; LockFeature is not
advertised by this server and is not part of the profile. WMS 1.1.1 is likewise a first-class evidence leg (`cite-wms11-results`);
the runner exercises version negotiation, 1.1.1 axis order,
`WMT_MS_Capabilities`, `application/vnd.ogc.se_xml` exceptions, `X`/`Y`
GetFeatureInfo, and `application/vnd.ogc.gml` GML FeatureInfo.

## Profile Scope, In One Line Each

- **OGC API Features `default`** — Part 1 Core on the seeded fixture. Part 2,
  Part 4, and the specialized CQL2 classes are not included in the public
  conformance declaration until an exact-candidate lane proves their complete
  classes.
- **OGC API building blocks** — the exact-candidate
  `ogc-api-building-block-conformance.yml` lane runs the complete vendored CQL2,
  MVT/TMS 2.0, Maps, and Schemathesis validator set. Its artifact is evidence
  for the narrower Features Part 3/queryables claim; CQL2/filter probes remain
  blocking regression coverage but do not certify complete CQL2 classes. It is
  not folded into the official ETS totals above.
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
- **WFS 2.0 `transactional`** — the dedicated TransactionalWFS
  conformance-class leg. Locking is not advertised by this server.
- **WCS 2.0 `core`** — official ETS core profile, with preflight on
  `GetCapabilities`, `DescribeCoverage`, and `GetCoverage`.
- **WPS 2.0 `basic-async`** — official ETS Basic and Asynchronous conformance
  classes against the canonical process/job runtime.
- **WMS 1.1.1 / 1.3 `default`** — the official ETS default profiles.
- **WMTS 1.0 `default`** — the official ETS default profile. It does **not**
  schema-validate the capabilities document. This profile reported 60/60 while
  `GetCapabilities` failed
  `schemas.opengis.net/wmts/1.0/wmtsGetCapabilities_response.xsd` at every layer:
  `MaxTileRow` and `MaxTileCol` are `xs:positiveInteger`, and the server emitted
  `0` for tile matrix 0. Found by client certification, not by CITE. A green run
  here therefore does not imply a schema-valid document, so protocol-level XSD
  assertions belong in the `Honua.Protocols.OgcClassic.Tests` WMTS suite.

## OGC API surfaces without an official CITE ETS

Some OGC API standards do not (yet) have an official CITE Executable Test Suite,
so they are not part of the 1138/1138 suite count above. They are still shipped as
conformant protocol adapters and proven with targeted integration tests plus an
accurate `/conformance` declaration:

- **OGC API – Styles (Part 1)** — `/ogc/styles`. Phase 1 adapter over Honua's
  per-layer style storage (ADR-0048, issue #1388). Declares `core`,
  `mapbox-styles`, `sld-10`, `sld-11`, `style-validation`, and
  `manage-styles` (Phase 2 promoted POST-create / DELETE to full CRUD; the
  Phase 1 disclosure that POST/DELETE returned `501` no longer applies).
  MapLibre is served from canonical storage; SLD 1.0/1.1 are derived on demand.
  **Conformance status: there is no official OGC API – Styles CITE/ETS
  executable test suite yet**, so this surface is not part of the 1138/1138 count
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

### WPS 2.0.2 evidence

WPS 2.0 Basic and Async conformance is included in the authoritative aggregate
above: 21/21 selected assertions passed, with 22/22 raw assertions passing when
the unselected Sync class is included. The ETS source is pinned to
`e2acc691440fad98d32e873a6b7237c9d759b8df`.
