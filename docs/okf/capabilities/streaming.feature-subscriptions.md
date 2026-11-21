---
type: capability
title: "Real-Time Feature Streams"
description: "Subscribe to WebSocket and SSE feature-change streams with filters and replay cursors."
resource: "honua://capability/streaming.feature-subscriptions"
tags: [capability, streaming, pro]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Real-Time Feature Streams

Subscribe to WebSocket and SSE feature-change streams with filters and replay cursors.

| | |
| --- | --- |
| Capability key | `streaming.feature-subscriptions` |
| Category | Streaming |
| Edition | Pro |
| Status | experimental |
| Surface maturity | 7 experimental |
| Registry entries | 7 |
| Proving tests | 153 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [React to feature changes](../../guides/edit/react-to-changes.md)

## Availability

Status is **experimental**. Not every experimental capability is on by default, so read `streaming.feature-subscriptions` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:streaming.feature-subscriptions:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__streaming.feature-subscriptions__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.
