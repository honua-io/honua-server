# WMS 1.3 CITE Conformance Testing Guide

This document explains how to run OGC CITE WMS 1.3 tests against Honua Server.

## Scope

Current WMS support in Honua targets:
- `GetCapabilities`
- `GetMap`

The CITE suite is executed with a profile-oriented parameter set (`minimal`, `default`, `full`) from `docker/cite/wms13/config/test-params.xml`.

## Run Locally

```bash
# Default profile
./scripts/conformance/cite/run-cite-wms-tests.sh

# Minimal profile
./scripts/conformance/cite/run-cite-wms-tests.sh --profile minimal

# Full profile
./scripts/conformance/cite/run-cite-wms-tests.sh --profile full

# Keep containers for debugging
./scripts/conformance/cite/run-cite-wms-tests.sh --no-cleanup --verbose
```

## CI Execution

Workflow: `.github/workflows/cite-wms-conformance.yml`

Triggered by:
- Weekly schedule (Wednesday 06:00 UTC)
- Manual workflow dispatch

## CI Baseline

- `failed_tests` must be `0`
- `total_tests` must be greater than `0`
- Results are uploaded as artifacts, including markdown summary and raw TeamEngine outputs

## Artifacts

Results are written to `cite-wms-results/`:
- `cite-wms-summary.md`
- `capabilities.xml`
- TeamEngine session logs/XML/HTML outputs

## Troubleshooting

- Check app logs:
  `docker compose -f docker/cite/wms13/compose.yml logs honua-server`
- Check TeamEngine logs:
  `docker compose -f docker/cite/wms13/compose.yml logs cite-runner`
- Verify endpoint manually:
  `http://localhost:8080/rest/services/cite/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0`
