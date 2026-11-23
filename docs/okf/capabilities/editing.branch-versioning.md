---
type: capability
title: "Branch Versioning"
description: "Core branch versioning (Pro): named gdb versions, isolated edits, reconcile/post, and gdbVersion-scoped editing/querying. Organisational approval and policy controls remain Enterprise. Experimental and off by default in 2026.1; enable with Capabilities:Experimental:versioning.branch:Enabled=true."
resource: "honua://capability/editing.branch-versioning"
tags: [capability, editing, pro]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Branch Versioning

Core branch versioning (Pro): named gdb versions, isolated edits, reconcile/post, and gdbVersion-scoped editing/querying. Organisational approval and policy controls remain Enterprise. Experimental and off by default in 2026.1; enable with Capabilities:Experimental:versioning.branch:Enabled=true.

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

Status is **experimental**.
Read the following runtime IDs in `GET /api/v1/capabilities/manifest` before calling their routes. `available: true` means the routes are served. `available: false` with `reasonCode: experimental-disabled` means they return 404 until the corresponding switch below is `true` (or the global `Capabilities:Experimental:Enabled`). Edition licensing still applies.

- `versioning.branch`: `Capabilities:Experimental:versioning.branch:Enabled`, or container environment variable `Capabilities__Experimental__versioning.branch__Enabled`.

A POSIX shell cannot `export` a name containing `.` or `-`.
