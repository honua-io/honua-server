# Metadata v2 Release Readiness

This checklist is derived from the Metadata v2 epic,
[honua-server#1035](https://github.com/honua-io/honua-server/issues/1035), and
its child issues. It is not authoritative. Use the GitHub issues for acceptance
criteria, status, and closure decisions.

Use this document as a release review aid after implementation work lands. It
should help reviewers confirm that the issue-level gates have coherent evidence
across schema, runtime, UI, migration, and standards projection.

## Gate 1: Canonical Schema Exists

Derived from:

- [#1035](https://github.com/honua-io/honua-server/issues/1035)
- [#1036](https://github.com/honua-io/honua-server/issues/1036)
- [#1037](https://github.com/honua-io/honua-server/issues/1037)

Release evidence:

- There is one canonical Metadata v2 schema source in the repo.
- Runtime snapshots validate against that schema without patching.
- Root metadata includes schema version, revision, tenant, environment, and
  generated time.
- Data Resources are the canonical unit; service-specific identity lives on
  publications or target-specific overrides.

## Gate 2: Secret-Safe Metadata

Derived from:

- [#1038](https://github.com/honua-io/honua-server/issues/1038)
- [#1039](https://github.com/honua-io/honua-server/issues/1039)
- [#1045](https://github.com/honua-io/honua-server/issues/1045)

Release evidence:

- Connections use references for endpoints, credentials, or connection handles.
- Production validation rejects dev-only inline credentials.
- Admin APIs and Redis snapshots do not expose resolved connection strings or
  secrets.
- Health checks resolve secrets at runtime without mutating canonical metadata.

## Gate 3: Resource Meaning Projects Without Duplication

Derived from:

- [#1040](https://github.com/honua-io/honua-server/issues/1040)
- [#1041](https://github.com/honua-io/honua-server/issues/1041)
- [#1042](https://github.com/honua-io/honua-server/issues/1042)
- [#1043](https://github.com/honua-io/honua-server/issues/1043)

Release evidence:

- Canonical resource metadata can project to OGC Records, DCAT, STAC, ISO
  19115, Esri catalog/items, GeoServices REST, OGC API, and OData.
- Standards-specific data is sparse override data, not a competing source of
  truth.
- Field roles drive target mappings before advanced target-specific bindings.
- One resource can have multiple publications with target readiness reporting.

## Gate 4: Workflow-Based Admin UI

Derived from:

- [#1035](https://github.com/honua-io/honua-server/issues/1035)
- [#1044](https://github.com/honua-io/honua-server/issues/1044)
- [#1046](https://github.com/honua-io/honua-server/issues/1046)

Release evidence:

- The admin UI can navigate Metadata v2 through workflow screens without
  requiring raw schema editing.
- Primary screens use Connections, Data Resources, Source, Fields, Metadata,
  Publish, Access, Validation, Readiness, Projection Preview, and Advanced
  Overrides.
- Access presets summarize policy behavior in human-readable language.
- Validation Center groups source, schema, metadata, publishing, security,
  standards, and cache/runtime findings.

## Gate 5: Runtime Snapshot and Projection Cache Safety

Derived from:

- [#1043](https://github.com/honua-io/honua-server/issues/1043)
- [#1045](https://github.com/honua-io/honua-server/issues/1045)

Release evidence:

- Cache keys include tenant, environment, catalog, schema version, revision,
  projection target, and projection profile version where applicable.
- Runtime snapshots exclude secrets and runtime-only handles.
- Projection caches can be rebuilt independently from the canonical snapshot.
- Schema migrations invalidate old cache keys deterministically.

## Gate 6: v1 Migration Is Diagnosable

Derived from:

- [#1047](https://github.com/honua-io/honua-server/issues/1047)

Release evidence:

- Existing metadata snapshots can convert to Metadata v2.
- Migration reports warnings, blockers, and inferred defaults.
- Service-owned layers become resource publications.
- Raw connection strings are flagged and converted to required secret
  references.
- Migration output validates against the Metadata v2 schema.

## Gate 7: Release Notes and User Risk

Derived from:

- [#1035](https://github.com/honua-io/honua-server/issues/1035)
- [Release checklist](../RELEASE_CHECKLIST.md)

Release evidence:

- Any user-visible behavior change is reflected in release notes or migration
  guidance.
- Known caveats and workarounds cover incomplete target projections,
  validation warnings, migration blockers, or admin UI limitations.
- Follow-up issues are linked for deferred Metadata v2 work.

## Review Output

For a Metadata v2 release candidate, capture:

- GitHub issue list reviewed.
- Schema and migration validation evidence.
- Projection readiness evidence for each claimed target.
- Admin UI workflow evidence or design sign-off.
- Known caveats, waivers, and follow-up issues.
