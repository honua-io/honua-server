---
type: capability
title: "Map Collaboration Sessions"
description: "Collaborative saved-map session operations (comments, activity, shared editing coordination)."
resource: "honua://capability/collaboration.map-sessions"
tags: [capability, collaboration, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Map Collaboration Sessions

Collaborative saved-map session operations (comments, activity, shared editing coordination).

| | |
| --- | --- |
| Capability key | `collaboration.map-sessions` |
| Category | Collaboration |
| Edition | Community |
| Status | experimental |
| Surface maturity | 9 experimental |
| Registry entries | 9 |
| Proving tests | 66 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [Feature locks (collaborative editing)](../../reference/collaboration/feature-locks.md)
- [Saved-map collaborative edit operation log](../../reference/saved-map-collaboration-op-log.md)

## Availability

Status is **experimental**, so this capability can be off on a default install. Read `collaboration.map-sessions` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:collaboration.map-sessions:Enabled` is `true` (environment variable `Capabilities__Experimental__collaboration.map-sessions__Enabled`). Edition licensing still applies once it is enabled.
