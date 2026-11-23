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
| Proving tests | 17 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [WMS, WFS, WCS, WMTS (classic OGC)](../../reference/protocols/wms-wfs-wcs-wmts.md)

## Availability

Status is **preview**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `serve.wmts`: `Capabilities:Experimental:serve.wmts:Enabled`, or container environment variable `Capabilities__Experimental__serve.wmts__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
