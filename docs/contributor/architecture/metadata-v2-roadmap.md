# Metadata v2 Roadmap

This roadmap is derived from the Metadata v2 GitHub issues and is not
authoritative. Use [honua-server#1035](https://github.com/honua-io/honua-server/issues/1035)
and its child issues for current scope, status, and acceptance criteria.

The roadmap groups issues into milestones so contributors can reason about
dependencies and review shape. It does not create new requirements.

## Milestone 0: Contract Orientation

Goal: align contributors on the Metadata v2 model before implementation starts.

Inputs:

- [#1035](https://github.com/honua-io/honua-server/issues/1035) epic principle
- [ADR-0023](../adr/0023-metadata-architecture.md) metadata resource model
- [Backlog index](metadata-v2-backlog.md)

Exit signals:

- Contributors can explain "resource first, service second."
- Planning docs point to GitHub as the source of truth.
- UI handoff uses workflow vocabulary instead of internal schema terms.

## Milestone 1: Canonical Model Foundation

Issues:

- [#1036](https://github.com/honua-io/honua-server/issues/1036) canonical root schema and runtime snapshot contract
- [#1037](https://github.com/honua-io/honua-server/issues/1037) resource-first model

Purpose:

- Establish the root entities and metadata envelope.
- Make Data Resource the canonical unit for datasets, rasters, tables, tile
  sets, processes, styles, documents, and external resources.
- Keep service-specific aliases, paths, indexes, and overrides outside the
  canonical resource.

## Milestone 2: Source, Capability, and Secret Safety

Issues:

- [#1038](https://github.com/honua-io/honua-server/issues/1038) storage and capability model
- [#1039](https://github.com/honua-io/honua-server/issues/1039) secret references

Purpose:

- Normalize source attachments and computed capabilities.
- Separate storage/source type from service or publication type.
- Ensure cacheable metadata and admin responses never contain resolved secrets.

## Milestone 3: Resource Meaning

Issues:

- [#1040](https://github.com/honua-io/honua-server/issues/1040) canonical catalog metadata semantics
- [#1041](https://github.com/honua-io/honua-server/issues/1041) field semantic roles and standard bindings

Purpose:

- Store resource meaning once.
- Use canonical metadata and field roles before target-specific overrides.
- Make metadata and field completeness reviewable by standard target.

## Milestone 4: Publication and Projection

Issues:

- [#1042](https://github.com/honua-io/honua-server/issues/1042) publications and distributions
- [#1043](https://github.com/honua-io/honua-server/issues/1043) projection profiles

Purpose:

- Link one resource to many services and catalogs.
- Make readiness visible per publication target.
- Support projection health, projection preview, and target-specific output
  caching.

## Milestone 5: Access and Runtime Read Models

Issues:

- [#1044](https://github.com/honua-io/honua-server/issues/1044) policy-based access
- [#1045](https://github.com/honua-io/honua-server/issues/1045) Redis snapshots and projections

Purpose:

- Replace ad hoc RBAC metadata with deterministic policy attachments and simple
  UI presets.
- Version runtime snapshots and derived projections with explicit cache-key
  dimensions.
- Ensure runtime read models exclude secrets and runtime-only handles.

## Milestone 6: Admin UI Handoff

Issue:

- [#1046](https://github.com/honua-io/honua-server/issues/1046) Claude Design admin UI information model

Purpose:

- Design admin workflows around Connections, Data Resources, Source, Fields,
  Metadata, Publish, Access, Validation, and Readiness.
- Keep raw schema terminology in advanced diagnostics only.
- Provide the screens and states listed in the
  [admin UI information model](metadata-v2-admin-ui-information-model.md).

## Milestone 7: Migration and Diagnostics

Issue:

- [#1047](https://github.com/honua-io/honua-server/issues/1047) v1-to-v2 migration adapter and diagnostics

Purpose:

- Convert existing metadata into the Metadata v2 shape.
- Report warnings, blockers, and inferred defaults.
- Validate migrated output against the v2 schema and release-readiness gates.

## Review Rhythm

During implementation, review this roadmap against the issue bodies during the
weekly backlog cadence. If issue scope changes, GitHub wins and this document
should be updated as a navigation aid.
