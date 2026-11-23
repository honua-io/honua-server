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
| Proving tests | 207 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Forms](../../reference/admin-api/forms.md)

## Availability

Status is **preview**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `sync.offline`: `Capabilities:Experimental:sync.offline:Enabled`, or container environment variable `Capabilities__Experimental__sync.offline__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
