---
type: capability
title: "Alert Evaluation Engine"
description: "Background worker for evaluating geofence rules against feature changes."
resource: "honua://capability/alerts.evaluation"
tags: [capability, alerts, pro]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Alert Evaluation Engine

Background worker for evaluating geofence rules against feature changes.

| | |
| --- | --- |
| Capability key | `alerts.evaluation` |
| Category | Alerts |
| Edition | Pro |
| Status | preview |
| Surface maturity | 18 preview |
| Registry entries | 18 |
| Proving tests | 110 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Preview

This capability is **preview**. It is not covered by the compatibility promise for the release - so it has no customer-facing page in this bundle, and that is deliberate rather than an omission. The counts above are real routes and real tests; what they are not is a commitment.

Status is **preview**. Not every preview capability is on by default, so read `alerts.evaluation` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:alerts.evaluation:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__alerts.evaluation__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.

- [Editions and licensing](../../concepts/editions-and-licensing.md) — what Preview means here
- [All capabilities](README.md)
