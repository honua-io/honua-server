# GeoPackage 1.2 CITE Conformance Testing Guide

This document explains how to run OGC CITE GeoPackage 1.2 tests against Honua Server.

## Scope

Honua Server exports data as GeoPackage via the admin layer export endpoint. The GeoPackage 1.2 CITE suite validates the output file against the OGC GeoPackage 1.2 specification. This is a **format-level** validator — it validates the exported file against the specification schema rather than exercising a service API directly.

The GeoPackage is exported from layer 0 (`BasicPolygons`) of the cite service. The export endpoint requires admin authentication (`X-API-Key` header).

Unlike service-level conformance suites (WMS, WMTS, etc.), the GeoPackage 1.2 suite has a single validation pass. The `--profile` flag is accepted for CLI consistency but all values run the same tests.

## Run Locally

```bash
# Default run
./scripts/conformance/cite/run-cite-gpkg12-tests.sh

# Keep containers for debugging
./scripts/conformance/cite/run-cite-gpkg12-tests.sh --no-cleanup --verbose

# Interactive mode (services stay up for manual testing)
./scripts/conformance/cite/run-cite-gpkg12-tests.sh --interactive
```

## CI Execution

Workflow: `.github/workflows/cite-gpkg12-conformance.yml`

Triggered by:
- Weekly schedule (Saturday 03:00 UTC)
- Manual workflow dispatch

## CI Baseline

- `results_available` must be `true`
- `failed_tests` must be `0`
- Results are uploaded as artifacts, including markdown summary and raw TeamEngine outputs

## Artifacts

Results are written to `cite-gpkg12-results/`:
- `cite-gpkg12-summary.md`
- `export.gpkg` — captured GeoPackage file from the server
- TeamEngine session logs/XML/HTML outputs

## Troubleshooting

- Check app logs:
  `docker compose -f docker/cite/gpkg12/compose.yml logs honua-server`
- Check TeamEngine logs:
  `docker compose -f docker/cite/gpkg12/compose.yml logs cite-runner`
- Verify endpoint manually:
  `curl -H 'X-API-Key: cite-admin-password' 'http://localhost:8080/api/v1/admin/services/cite/layers/0/export?format=gpkg' -o export.gpkg`
- The admin password in the CITE environment is hardcoded to `cite-admin-password`. No secrets management is needed for this test infrastructure.
- The ETS Docker image (`ogccite/ets-gpkg12:latest`) uses `:latest` tag. If validation behavior changes unexpectedly, check for upstream image updates.
