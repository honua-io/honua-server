# KML 2.2 CITE Conformance Testing Guide

This document explains how to run OGC CITE KML 2.2 tests against Honua Server.

## Scope

Honua Server produces KML output via `MapServer/generateKml`. The KML 2.2 CITE suite validates the output document against the OGC KML 2.2 specification schema. This is a **format-level** validator — it validates the fetched document against the specification schema rather than exercising a service API directly.

KML output is always EPSG:4326.

Unlike service-level conformance suites (WMS, WMTS, etc.), the KML 2.2 suite validates a concrete KML document rather than a service endpoint. The default `applicable` profile runs the official ETS Level 1 classes that apply to Honua's generated KML document and is the strict no-skip evidence profile. The raw ETS `default` profile remains available for diagnostics and can report skips for Level 2 and Level 3 checks.

## Run Locally

```bash
# Default strict no-skip applicable run
./scripts/conformance/cite/run-cite-kml22-tests.sh

# Raw ETS default diagnostic run
./scripts/conformance/cite/run-cite-kml22-tests.sh --profile default --verbose

# Keep containers for debugging
./scripts/conformance/cite/run-cite-kml22-tests.sh --no-cleanup --verbose

# Interactive mode (services stay up for manual testing)
./scripts/conformance/cite/run-cite-kml22-tests.sh --interactive
```

## CI Execution

Workflow: `.github/workflows/cite-kml22-conformance.yml`

Triggered by:
- Weekly schedule (Friday 03:00 UTC)
- Manual workflow dispatch

## CI Baseline

- `results_available` must be `true`
- `failed_tests` must be `0`
- `skipped_tests` must be `0`
- `canttell_tests` must be `0`
- Results are uploaded as artifacts, including markdown summary and raw TeamEngine outputs

## Artifacts

Results are written to `cite-kml22-results/`:
- `cite-kml22-summary.md`
- `output.kml` — captured KML document from the server
- TeamEngine session logs/XML/HTML outputs

## Troubleshooting

- Check app logs:
  `docker compose -f docker/cite/kml22/compose.yml logs honua-server`
- Check TeamEngine logs:
  `docker compose -f docker/cite/kml22/compose.yml logs cite-runner`
- Verify endpoint manually:
  `curl http://localhost:8080/rest/services/cite/MapServer/generateKml`
- The ETS Docker image (`ogccite/ets-kml22:latest`) uses `:latest` tag. If validation behavior changes unexpectedly, check for upstream image updates.
