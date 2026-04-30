# WMTS 1.0 CITE Conformance Testing Guide

This document explains how to run OGC CITE WMTS 1.0 tests against Honua Server.

## Scope

Current WMTS support in Honua targets:
- `GetCapabilities`
- `GetTile` (KVP)

Test parameters are configured in `docker/cite/wmts10/config/test-params.xml`.

## Run Locally

```bash
# Default profile label
./scripts/conformance/cite/run-cite-wmts-tests.sh

# Override profile label
./scripts/conformance/cite/run-cite-wmts-tests.sh --profile minimal

# Keep containers for debugging
./scripts/conformance/cite/run-cite-wmts-tests.sh --no-cleanup --verbose
```

## CI Execution

Workflow: `.github/workflows/cite-wmts-conformance.yml`

Triggered by:
- Weekly schedule (Thursday 06:00 UTC)
- Manual workflow dispatch

## CI Baseline

- `failed_tests` must be `0`
- `total_tests` must be greater than `0`
- Results are uploaded as artifacts, including markdown summary and raw TeamEngine outputs

## 2026-04-30 Failure Triage

The latest retained failing artifact at triage time was GitHub Actions run
`24824149578` (`cite-wmts-conformance-results-56`), which reported 60 total
tests, 56 passed, and 4 failed. The concrete TeamEngine failure was
`Server.KVP.GET.GetCapabilities.Response.TileMatrixSet.WellKnownScaleSet`: the
`WebMercatorQuad` tile matrix set advertised
`urn:ogc:def:crs:EPSG::3857`, but the WMTS 1.0 ETS expects
`urn:ogc:def:crs:EPSG:6.18:3:3857` when paired with
`urn:ogc:def:wkss:OGC:1.0:GoogleMapsCompatible`.

Issue #870 updates the capabilities CRS URN. The lane remains not
certification-ready until the scheduled or workflow-dispatch CITE run is rerun
and retained with a clean summary. The newer scheduled attempt, GitHub Actions
run `25155545936` on 2026-04-30, failed at workflow startup before producing
jobs or artifacts; Issue #870 also restores the WMTS caller permissions expected
by the reusable conformance workflow.

## Artifacts

Results are written to `cite-wmts-results/`:
- `cite-wmts-summary.md`
- `capabilities.xml`
- TeamEngine session logs/XML/HTML outputs

## Troubleshooting

- Check app logs:
  `docker compose -f docker/cite/wmts10/compose.yml logs honua-server`
- Check TeamEngine logs:
  `docker compose -f docker/cite/wmts10/compose.yml logs cite-runner`
- Verify endpoint manually:
  `http://localhost:8080/rest/services/cite/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0`
