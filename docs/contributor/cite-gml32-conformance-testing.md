# GML 3.2 CITE Conformance Testing Guide

This document explains how to run OGC CITE GML 3.2 tests against Honua Server.

## Scope

Honua Server emits GML 3.2 via OGC API Features content negotiation. The GML 3.2 CITE suite validates the output document against the OGC GML 3.2 specification schema. This is a **format-level** validator — it validates the fetched document against the specification schema rather than exercising a service API directly.

The GML document is fetched from the `cite:BasicPolygons` collection using `Accept: application/gml+xml; version=3.2` content negotiation.

Unlike service-level conformance suites (WMS, WMTS, etc.), the GML 3.2 suite validates a concrete GML document rather than a service endpoint. The default `applicable` profile runs the official ETS TestNG classes that apply to Honua's polygon GML document and is the strict no-skip evidence profile. The raw ETS `default` profile remains available for diagnostics and can report skips for optional GML object families that are not present in the polygon document.

## Run Locally

```bash
# Default strict no-skip applicable run
./scripts/conformance/cite/run-cite-gml32-tests.sh

# Raw ETS default diagnostic run
./scripts/conformance/cite/run-cite-gml32-tests.sh --profile default --verbose

# Keep containers for debugging
./scripts/conformance/cite/run-cite-gml32-tests.sh --no-cleanup --verbose

# Interactive mode (services stay up for manual testing)
./scripts/conformance/cite/run-cite-gml32-tests.sh --interactive
```

## CI Execution

Workflow: `.github/workflows/cite-gml32-conformance.yml`

Triggered by:
- Weekly schedule (Saturday 06:00 UTC)
- Manual workflow dispatch

## CI Baseline

- `results_available` must be `true`
- `failed_tests` must be `0`
- `skipped_tests` must be `0`
- `canttell_tests` must be `0`
- Results are uploaded as artifacts, including markdown summary and raw TeamEngine outputs

## Artifacts

Results are written to `cite-gml32-results/`:
- `cite-gml32-summary.md`
- `output.gml` — captured GML document from the server
- TeamEngine session logs/XML/HTML outputs

## Troubleshooting

- Check app logs:
  `docker compose -f docker/cite/gml32/compose.yml logs honua-server`
- Check TeamEngine logs:
  `docker compose -f docker/cite/gml32/compose.yml logs cite-runner`
- Verify endpoint manually:
  `curl -H 'Accept: application/gml+xml; version=3.2' http://localhost:8080/ogc/features/collections/cite:BasicPolygons/items`
- If GML content negotiation fails, verify that `OgcFeatures` is in `enabledProtocols` for the cite service in the seed data.
- The ETS Docker image (`ogccite/ets-gml32:latest`) uses `:latest` tag. If validation behavior changes unexpectedly, check for upstream image updates.
