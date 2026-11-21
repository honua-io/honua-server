---
type: capability
title: "WMTS 1.0"
description: "Serve pre-rendered tile pyramids through WMTS 1.0."
resource: "honua://capability/serve.wmts"
tags: [capability, serve, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# WMTS 1.0

Serve pre-rendered tile pyramids through WMTS 1.0.

| | |
| --- | --- |
| Capability key | `serve.wmts` |
| Category | Serve |
| Edition | Community |
| Status | preview |
| Surface maturity | 1 preview |
| Registry entries | 1 |
| Proving tests | 16 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [WMS, WFS, WCS, WMTS (classic OGC)](../../reference/protocols/wms-wfs-wcs-wmts.md)

## Availability

Status is **preview**, so this capability can be off on a default install. Read `serve.wmts` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:serve.wmts:Enabled` is `true` (environment variable `Capabilities__Experimental__serve.wmts__Enabled`). Edition licensing still applies once it is enabled.
