# OGC API - Maps Conformance Testing

This guide covers conformance verification for Honua's `OGC API - Maps` endpoints.

## Current Gate Strategy

As of **February 24, 2026**, the public OGC CITE packaging path for `ets-ogcapi-maps10` is not as consistently published as Features/Tiles/WMS/WMTS image workflows.

Until that is stable, Honua enforces Maps conformance with a dedicated integration conformance suite:

- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/OgcMapsConformanceTests.cs`
- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/OgcMapsBasicTests.cs`
- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/OgcMapsConformanceHandlerTests.cs`

## Local Run

Run the dedicated Maps conformance runner:

```bash
./scripts/run-ogc-maps-conformance-tests.sh --configuration Release
```

Artifacts are written to:

- `ogc-maps-results/ogc-maps-summary.md`
- `ogc-maps-results/ogc-maps-conformance.log`
- `ogc-maps-results/ogc-maps-conformance.trx`

## CI Gate

GitHub Actions workflow:

- `.github/workflows/ogc-maps-conformance.yml`

Failure conditions:

- no result summary produced
- zero tests executed
- one or more failing tests

## Production Audit Integration

Phase 2 protocol checks include:

- `ogc-maps-conformance-tests` via `scripts/run-production-audit.sh`

This means `./scripts/run-production-audit.sh --phase 2 --agents protocol` now blocks on OGC API - Maps conformance regressions the same way it blocks on other required protocol checks.
