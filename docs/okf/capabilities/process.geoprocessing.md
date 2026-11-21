---
type: capability
title: "Geoprocessing Task Execution"
description: "Submit and poll geoprocessing tasks through the Esri GeoServices GPServer surface. Print/export-specific tasks are gated by printing.* entitlement keys."
resource: "honua://capability/process.geoprocessing"
tags: [capability, process, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Geoprocessing Task Execution

Submit and poll geoprocessing tasks through the Esri GeoServices GPServer surface. Print/export-specific tasks are gated by printing.* entitlement keys.

| | |
| --- | --- |
| Capability key | `process.geoprocessing` |
| Category | Process |
| Edition | Community |
| Status | preview |
| Surface maturity | 19 preview |
| Registry entries | 19 |
| Proving tests | 260 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Run geoprocessing](../../guides/query-analyze/run-geoprocessing.md)
- [Geoprocessing operations](../../reference/geoprocessing-operations.md)
- [Custom-code worker runtime](../../reference/gp-customcode-runtime.md)
- [GP harness events (honua.gp.events/v1)](../../reference/gp-harness-events.v1.md)

## Availability

Status is **preview**, so this capability can be off on a default install. Read `process.geoprocessing` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:process.geoprocessing:Enabled` is `true` (environment variable `Capabilities__Experimental__process.geoprocessing__Enabled`). Edition licensing still applies once it is enabled.
