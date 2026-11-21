---
type: capability
title: "Branch Versioning"
description: "Core branch versioning (Pro): named gdb versions, isolated edits, reconcile/post, and gdbVersion-scoped editing/querying. Organisational approval and policy controls remain Enterprise. Preview in 2026.1; the experimental runtime opt-in remains required."
resource: "honua://capability/editing.branch-versioning"
tags: [capability, editing, pro]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Branch Versioning

Core branch versioning (Pro): named gdb versions, isolated edits, reconcile/post, and gdbVersion-scoped editing/querying. Organisational approval and policy controls remain Enterprise. Preview in 2026.1; the experimental runtime opt-in remains required.

| | |
| --- | --- |
| Capability key | `editing.branch-versioning` |
| Category | Editing |
| Edition | Pro |
| Status | experimental |
| Surface maturity | 20 experimental |
| Registry entries | 20 |
| Proving tests | 96 |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## Documented in

- [GeoServices REST](../../reference/protocols/geoservices-rest.md)

## Availability

Status is **experimental**, so this capability can be off on a default install. Read `editing.branch-versioning` in `GET /api/v1/capabilities/manifest` before calling its routes: `available: false` with `reasonCode: experimental-disabled` means the routes return 404 until `Capabilities:Experimental:editing.branch-versioning:Enabled` is `true` (environment variable `Capabilities__Experimental__editing.branch-versioning__Enabled`). Edition licensing still applies once it is enabled.
