# Production Geospatial Audit Playbook

This playbook defines the production-readiness audit workflow for Honua Server MVP deployment.

It implements a five-agent model:
- `architecture`: dependency flow, API patterns, AOT readiness, API surface enforcement
- `security`: authentication/authorization, input validation, encryption, hardening checks
- `geodesy`: CRS/SRID handling, coordinate transforms, spatial math correctness
- `performance`: query/tile latency, cache behavior, memory pressure, scale checks
- `protocol`: GeoServices/OGC/OData/WMTS/WMS/MVT conformance and integration behavior

## Phased Gates

| Phase | Focus | Blocking gate |
|---|---|---|
| 1 | Critical spatial + security | All required architecture, security, and geodesy checks pass |
| 2 | Performance + protocol compliance | All required performance and protocol checks pass |
| 3 | Integration + observability | All required integration/observability checks pass; manual client validation completed |

## Run The Audit

Full production run:

```bash
./scripts/run-production-audit.sh --mode full
```

Phase-focused run:

```bash
./scripts/run-production-audit.sh --phase 1 --agents architecture,security,geodesy
```

Quick preflight run:

```bash
./scripts/run-production-audit.sh --mode quick
```

Dry-run (planned checks only):

```bash
./scripts/run-production-audit.sh --mode full --dry-run
```

## Artifacts

Each run writes timestamped artifacts to:

```
.audit/runs/<run-id>/
```

Produced files:
- `summary.md`: human-readable gate report
- `summary.json`: machine-readable results
- `results.tsv`: tabular check output
- `logs/*.log`: per-check execution logs

## Required Manual Validation

Automated checks are necessary but not sufficient for production sign-off. Complete these manual validations for each release candidate:

1. GIS client compatibility:
- QGIS against OGC API Features/Tiles
- ArcGIS Pro against FeatureServer/MapServer
- Web client (Leaflet/MapLibre) against MVT/WMTS/WMS

2. Geodetic reference verification:
- Validate representative `ST_Transform` outputs for common CRSs (WGS84, Web Mercator, UTM/State Plane in scope) against authoritative EPSG/PROJ results.
- Validate axis-order behavior for CRS84 vs EPSG:4326 request/response flows.
- Validate geometry precision at envelope edges and tile boundaries.

3. Security verification:
- API key and OIDC auth path checks (including bypass/negative tests)
- Spatial query injection attempts (`where`, CQL2, WKT/GeoJSON payload paths)
- Transport hardening checks behind proxy (TLS termination, forwarded headers, security headers)

## Production Exit Criteria

A release candidate is production-audit ready only when all criteria below hold:

1. `./scripts/run-production-audit.sh --mode full` reports zero required check failures.
2. CITE suites used in phase 2 report zero failed conformance tests.
3. Coverage and architecture gates remain green in CI (`TreatWarningsAsErrors`, API surface coverage, coverage thresholds).
4. Manual GIS compatibility and geodetic verification evidence is attached to the release/PR.
