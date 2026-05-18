# Metadata v2 Backlog Index

This is a local navigation and sequencing guide for Metadata v2 planning. GitHub
issues remain authoritative for scope, status, acceptance criteria, and closure:

- Epic: [honua-server#1035](https://github.com/honua-io/honua-server/issues/1035)
- Admin UI design handoff: [honua-server#1046](https://github.com/honua-io/honua-server/issues/1046)

Use this file to decide which issue to read next, not to replace the issue body.
When this file disagrees with GitHub, update this file or ignore it.

## Reading Order

1. Start with [#1035](https://github.com/honua-io/honua-server/issues/1035)
   for the epic principle: resource first, service second.
2. Read [ADR-0023](../adr/0023-metadata-architecture.md) for the existing
   metadata resource-model direction.
3. Use the [roadmap](metadata-v2-roadmap.md) for milestone grouping.
4. Use the [release-readiness gates](metadata-v2-release-readiness.md) before
   treating the Metadata v2 surface as shippable.
5. Use the [admin UI information model](metadata-v2-admin-ui-information-model.md)
   when handing workflow design to Claude Design or another UI agent.

## Issue Map

| Issue | Planning Role | Read When |
|---|---|---|
| [#1035](https://github.com/honua-io/honua-server/issues/1035) | Epic and source of truth | Any Metadata v2 scope, status, or gate question |
| [#1036](https://github.com/honua-io/honua-server/issues/1036) | Canonical root schema and runtime snapshot contract | Starting schema work or cache-safe serialization |
| [#1037](https://github.com/honua-io/honua-server/issues/1037) | Resource-first model | Mapping current layers, rasters, tables, processes, styles, or external assets |
| [#1038](https://github.com/honua-io/honua-server/issues/1038) | Storage and capability model | Connecting relational, raster, object, tile, file, or external API sources |
| [#1039](https://github.com/honua-io/honua-server/issues/1039) | Secret-reference model | Touching connections, credentials, health checks, snapshots, or admin responses |
| [#1040](https://github.com/honua-io/honua-server/issues/1040) | Catalog metadata semantics | Projecting resource meaning to standards and catalog targets |
| [#1041](https://github.com/honua-io/honua-server/issues/1041) | Field roles and bindings | Mapping fields into protocol, catalog, query, edit, or sensitivity behavior |
| [#1042](https://github.com/honua-io/honua-server/issues/1042) | Publications | Linking one resource to many service or catalog targets |
| [#1043](https://github.com/honua-io/honua-server/issues/1043) | Projection profiles | Building standards outputs, preview, health, or projection caches |
| [#1044](https://github.com/honua-io/honua-server/issues/1044) | Policy-based access | Reworking RBAC, access presets, field restrictions, or readable policy summaries |
| [#1045](https://github.com/honua-io/honua-server/issues/1045) | Redis snapshots and projections | Versioning normalized runtime snapshots and derived projection caches |
| [#1046](https://github.com/honua-io/honua-server/issues/1046) | Admin UI information model | Designing admin workflows without exposing raw schema complexity |
| [#1047](https://github.com/honua-io/honua-server/issues/1047) | v1-to-v2 migration | Converting current metadata and reporting diagnostics |

## Suggested Sequence

### Foundation

Work starts with #1036 and #1037. These define the schema root and the
resource-first model that every later slice depends on.

### Source and Safety

Then sequence #1038 and #1039 so source bindings, capabilities, connections,
and secret references are settled before publication, validation, or cache work
depends on them.

### Meaning

Sequence #1040 and #1041 after the resource and source model. These issues
define the resource metadata and field semantics that projections and UI
readiness need.

### Publication and Projection

Sequence #1042 and #1043 once resources, metadata, fields, and capabilities are
stable enough to decide whether a target is ready, warning, blocked, or not
applicable.

### Access and Runtime

Sequence #1044 and #1045 with publication/projection work. Access decisions and
cache keys both need the canonical entity boundaries, but they should be
validated before release readiness.

### UI and Migration

#1046 can begin from the issue body and this design handoff, but it should
refresh after any naming or workflow change in the foundation issues. #1047 is
the bridge from existing metadata into the v2 shape and should run against the
same release gates as new v2 authoring.

## Local Artifacts

- [Metadata v2 roadmap](metadata-v2-roadmap.md)
- [Metadata v2 release-readiness gates](metadata-v2-release-readiness.md)
- [Metadata v2 admin UI information model](metadata-v2-admin-ui-information-model.md)
