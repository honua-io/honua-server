# OGC API - Maps Conformance Testing

This guide covers conformance verification for Honua's `OGC API - Maps` endpoints.

## Current Gate Strategy

As of **February 24, 2026**, the public OGC CITE packaging path for `ets-ogcapi-maps10` is not as consistently published as Features/Tiles/WMS/WMTS image workflows.

Until that is stable, Honua enforces Maps conformance with a dedicated integration conformance suite:

- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/OgcMapsConformanceTests.cs`
- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/OgcMapsBasicTests.cs`
- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/OgcMapsConformanceHandlerTests.cs`
- `tests/dotnet/Honua.Server.Tests/Features/Protocols/Ogc/Api/Maps/OgcMapsTemporalMosaicTests.cs`

## Conformance Classes Advertised

`OgcMapsConformanceHandler` declares the following classes (verified by `OgcMapsConformanceHandlerTests`):

- `…/conf/core`, `…/conf/collection-map`, `…/conf/dataset-map`, `…/conf/collections-selection`
- `…/conf/datetime` — temporal raster mosaic via the `datetime` query parameter on `/ogc/maps/collections/{id}/map` and `/ogc/maps/map`. Accepts an RFC 3339 instant (`2024-02-15T00:00:00Z`), a closed interval (`start/end`), or half-open intervals (`../end`, `start/..`); Honua selects the newest effective acquisition batch within the bounded range across the addressed collection or dataset before applying bbox windowing. Pro+ editions evaluate the timestamp; Community returns `402 Payment Required`. Inverted intervals (start > end) and unparsable values return `400 Bad Request`.
- `…/conf/crs`, `…/conf/png`, `…/conf/jpeg`, `…/conf/tiff`, `…/conf/scaling`

## Local Run

Run the dedicated Maps conformance runner:

```bash
./scripts/conformance/ogc/run-ogc-maps-conformance-tests.sh --configuration Release
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

- `ogc-maps-conformance-tests` via `scripts/conformance/run-production-audit.sh`

This means `./scripts/conformance/run-production-audit.sh --phase 2 --agents protocol` now blocks on OGC API - Maps conformance regressions the same way it blocks on other required protocol checks.
