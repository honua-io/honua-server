# SDK migration guide template

Minimum migration-guide structure that the JavaScript/TypeScript, Python, and
.NET SDK repositories follow for control-plane releases.

Use this template whenever an SDK release changes generated admin surfaces,
runtime compatibility checks, auth behavior, import workflows, or long-running
operation handling.

## Required Sections

Every SDK release that changes public behavior should include:

1. A version header such as `Migrating from X.Y to X.Z`.
2. Server compatibility guidance.
3. Breaking changes with before/after examples.
4. New capabilities worth adopting.
5. Deprecations with replacement guidance and sunset timing.
6. Validation steps for the workflows most likely to regress.

## Server Compatibility Block

Each SDK migration entry should call out:

- supported control-plane API major
- minimum supported Honua Server version or release line
- supported server release channels (`stable`, `beta`, `preview`, `LTS`)
- whether the release requires regenerating or re-downloading the SDK artifact

When the server exposes `GET /api/v1/admin/capabilities`, SDKs should use that
contract for runtime validation and feature detection.

## Changelog Expectations

SDK changelogs and release notes should explicitly state:

- the server release line the SDK was validated against
- whether all generated SDK families moved together
- added, deprecated, or removed fields/endpoints
- any auth, import, or long-running-operation behavior changes
- whether production users should upgrade immediately or only after staging
  validation

For `beta` and `preview` releases, mark unstable operations explicitly. For
breaking changes, link back to:

- [Server + SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md)
- [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md)
- [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md)

## Release Template

Use the following structure in SDK repos:

```markdown
## Migrating from X.Y to X.Z

### Server Compatibility
- Control-plane API major: `v1`
- Minimum server line: `stable v1` / `beta v1` / `preview v1`
- Runtime handshake: `GET /api/v1/admin/capabilities`

### Breaking Changes
- Describe breaking changes with before/after examples.

### New Features
- Describe new capabilities and any opt-in steps.

### Deprecations
- Describe deprecated APIs, replacements, and sunset timing.

### Validation Checklist
- auth/login flow
- connection management flow
- import/publish flow
- long-running operations you depend on
```

## Per-Repo Expectations

- `honua-sdk-js`: keep `MIGRATION.md` and `CHANGELOG.md` current in the repo
  root.
- `honua-sdk-python`: keep `MIGRATION.md` and `CHANGELOG.md` current in the
  repo root.
- `honua-sdk-dotnet`: keep a shared `MIGRATION.md` and `CHANGELOG.md` for
  `Honua.Sdk.Admin` and `Honua.Sdk.Grpc`.
