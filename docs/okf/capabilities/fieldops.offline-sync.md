---
type: capability
title: "Offline/Field Sync"
description: "Use disconnected field sync, form offline policy discovery, GeoServices replica delta sync delivered as Esri JSON, and FieldCollection cursor/change exchange."
resource: "honua://capability/fieldops.offline-sync"
tags: [capability, fieldops, pro]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Offline/Field Sync

Use disconnected field sync, form offline policy discovery, GeoServices replica delta sync delivered as Esri JSON, and FieldCollection cursor/change exchange.

| | |
| --- | --- |
| Capability key | `fieldops.offline-sync` |
| Category | FieldOps |
| Edition | Pro |
| Status | preview |
| Surface maturity | 18 preview |
| Registry entries | 18 |
| Proving tests | 201 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Forms](../../reference/admin-api/forms.md)

## Availability

Status is **preview**. Not every preview capability is on by default, so read `fieldops.offline-sync` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:fieldops.offline-sync:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__fieldops.offline-sync__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
