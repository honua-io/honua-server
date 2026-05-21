# API Standards Summary

This page is the single answer to "where does Honua Server stand on API standards
and conformance?" It consolidates pointers to the authoritative documents in one
place so audits, re-grades, and onboarding can move quickly. Each section links
to the source of truth; this file is a navigational aid, not a redefinition.

Last reviewed: 2026-05-20.

## OGC CITE Conformance

**Authoritative pass rate: 952 / 952 (100%) across 11 OGC CITE suites on `trunk`.**

Snapshot from the 2026-05-17 evidence run (`allPassed=true`, 0 failed, 0 skipped,
0 CantTell):

| Suite | Profile | Passed / Total | Pass Rate |
|---|---|---:|---:|
| OGC API Features 1.0 | `default` | 137 / 137 | 100% |
| OGC API Tiles 1.0 | `default` | 16 / 16 | 100% |
| GeoPackage 1.2 | `applicable` | 31 / 31 | 100% |
| GML 3.2 | `applicable` | 17 / 17 | 100% |
| KML 2.2 | `applicable` | 42 / 42 | 100% |
| WFS 1.0 | `basic` | 162 / 162 | 100% |
| WFS 1.1 | `basic` | 39 / 39 | 100% |
| WFS 2.0 | `basic` | 167 / 167 | 100% |
| WCS 2.0 | `core` | 82 / 82 | 100% |
| WMS 1.3 | `default` | 199 / 199 | 100% |
| WMTS 1.0 | `default` | 60 / 60 | 100% |

Sources of truth:

- [`docs/cite-status.md`](cite-status.md) — authoritative snapshot, re-grading guidance.
- [`docs/contributor/ogc-cite-conformance-evidence.md`](contributor/ogc-cite-conformance-evidence.md)
  — canonical, website-linkable evidence summary.
- [`docs/contributor/cite-runbook.md`](contributor/cite-runbook.md) — per-suite
  scope, scripts, and workflow files.
- [`docs/contributor/ogc-certification-path.md`](contributor/ogc-certification-path.md)
  — formal OGC certification posture.

The OpenAPI specs under `src/Honua.Server/*-openapi.json` carry the same totals
in an `x-honua-cite-compliance` vendor extension on the `info` object, so SDK
generators and contract diff tools see the conformance posture without leaving
the spec.

## gRPC Versioning Policy

The `Geospatial.V1` gRPC surface is the long-lived, source-available wire
contract for SDKs and integrators.

- [`docs/grpc-versioning-policy.md`](grpc-versioning-policy.md) — versioning
  rules, breaking-change matrix, deprecation policy, and proto evolution
  guidance.
- Stability tier: **stable**. Breaking changes require a new major package
  (`Geospatial.V2`) and a documented migration window.

## OpenAPI Drift Workflow

Admin/control-plane OpenAPI contracts are gated in CI and reproducible across
SDK generations.

- `.github/workflows/openapi-contract-governance.yml` — OpenAPI spec shape and
  breaking-change detection for admin endpoints (see
  [`docs/contributor/CI_QUALITY_GATES.md`](contributor/CI_QUALITY_GATES.md)).
- `.github/workflows/control-plane-sdk-governance.yml` — reproducible SDK
  generation from the admin OpenAPI spec, ensuring no silent drift between
  spec, server, and clients.

## API Versioning Strategy

Honua exposes two stable, separately versioned API tiers:

- **Admin / control plane (v1).** See
  [`docs/developer/CONTROL_PLANE_VERSIONING_POLICY.md`](developer/CONTROL_PLANE_VERSIONING_POLICY.md)
  for backward-compatibility commitments, deprecation timelines, and the
  process for introducing v2. Migration guidance lives in
  [`docs/developer/CONTROL_PLANE_MIGRATION_GUIDE.md`](developer/CONTROL_PLANE_MIGRATION_GUIDE.md).
- **OGC (stable).** Versioning is governed by the underlying OGC standard
  (e.g. WFS 2.0, WMS 1.3, OGC API Features 1.0). Honua does not invent its
  own version axis for these; conformance is asserted via CITE pass rates
  above.

## Related

- [`docs/gis/STANDARDS_APIS.md`](gis/STANDARDS_APIS.md) — every protocol Honua
  speaks, with endpoint, version, and coverage links.
- [`docs/gis/MVP_COMPATIBILITY_CONTRACT.md`](gis/MVP_COMPATIBILITY_CONTRACT.md)
  — the supported client × protocol matrix for MVP.
- [`docs/contributor/public-interface-quality-model.md`](contributor/public-interface-quality-model.md)
  — how public API surfaces are scored and gated.
