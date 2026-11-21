---
type: capability
title: "Demo Showcase Surfaces"
description: "Non-sellable demo/showcase endpoints used for product demonstrations."
resource: "honua://capability/demo.showcase"
tags: [capability, demo, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Demo Showcase Surfaces

Non-sellable demo/showcase endpoints used for product demonstrations.

| | |
| --- | --- |
| Capability key | `demo.showcase` |
| Category | Demo |
| Edition | Community |
| Status | experimental |
| Surface maturity | 2 experimental |
| Registry entries | 2 |
| Proving tests | 3 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Preview

This capability is **experimental**. It is not covered by the compatibility promise for the release - so it has no customer-facing page in this bundle, and that is deliberate rather than an omission. The counts above are real routes and real tests; what they are not is a commitment.

Status is **experimental**. Not every experimental capability is on by default, so read `demo.showcase` in `GET /api/v1/capabilities/manifest` before calling its routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until `Capabilities:Experimental:demo.showcase:Enabled` is `true` (or the global `Capabilities:Experimental:Enabled`). Set it in configuration or as the container environment variable `Capabilities__Experimental__demo.showcase__Enabled`; a POSIX shell cannot `export` a name containing `.` or `-`. Edition licensing still applies once it is enabled.

- [Editions and licensing](../../concepts/editions-and-licensing.md) — what Preview means here
- [All capabilities](README.md)
