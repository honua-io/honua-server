# WMTS 1.0 CITE Conformance Testing Guide

This document explains how to run OGC CITE WMTS 1.0 tests against Honua Server.

## Scope

Current WMTS support in Honua targets:
- `GetCapabilities`
- `GetTile` (KVP)

Test parameters are configured in `docker/cite-wmts-config/test-params.xml`.

## Run Locally

```bash
# Default profile label
./scripts/run-cite-wmts-tests.sh

# Override profile label
./scripts/run-cite-wmts-tests.sh --profile minimal

# Keep containers for debugging
./scripts/run-cite-wmts-tests.sh --no-cleanup --verbose
```

## CI Execution

Workflow: `.github/workflows/cite-wmts-conformance.yml`

Triggered by:
- Pull requests to `trunk`/`main` that touch WMTS/CITE files
- Pushes to `trunk`/`main` that touch WMTS/CITE files
- Weekly schedule (Thursday 06:00 UTC)
- Manual workflow dispatch

## CI Baseline

- `failed_tests` must be `0`
- `total_tests` must be greater than `0`
- Results are uploaded as artifacts, including markdown summary and raw TeamEngine outputs

## Artifacts

Results are written to `cite-wmts-results/`:
- `cite-wmts-summary.md`
- `capabilities.xml`
- TeamEngine session logs/XML/HTML outputs

## Troubleshooting

- Check app logs:
  `docker compose -f docker/cite-wmts-compose.yml logs honua-server`
- Check TeamEngine logs:
  `docker compose -f docker/cite-wmts-compose.yml logs cite-runner`
- Verify endpoint manually:
  `http://localhost:8080/rest/services/cite/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0`
