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
| Proving tests | 156 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [React to feature changes](../../guides/edit/react-to-changes.md)

## Availability

Status is **experimental**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `realtime.feature-streams`: `Capabilities:Experimental:realtime.feature-streams:Enabled`, or container environment variable `Capabilities__Experimental__realtime.feature-streams__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
