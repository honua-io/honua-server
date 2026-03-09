# Control Plane API Versioning and Deprecation Policy

This policy applies only to Honua control-plane/admin endpoints (`/api/v*/admin/*`).
It does not apply to standards APIs (FeatureServer, OGC, OData, WMS/WMTS).
For standards API versioning, see [STANDARDS_APIS.md](STANDARDS_APIS.md#versioning-and-compatibility-policy).

## Compatibility Contract

- Current major version: `v1` (`/api/v1/admin/*`)
- Backward-compatible (`v1`) changes may include:
- new optional request fields
- new response fields
- new endpoints/resources
- new optional authentication alternatives
- Breaking changes require a new major path (`/api/v2/admin/*`), except emergency security fixes.

## What Is Breaking

Examples considered breaking:
- removing an endpoint or HTTP method
- removing request/response fields
- making optional fields required
- removing supported media types or security scheme combinations
- changing field type/enum in a way older clients cannot parse

## Release Channels

### Stable

Fully supported with backward-compatibility guaranteed for the current major version path (`/api/v1/admin/*`). All changes within a major version are additive and non-breaking. This is the default channel for all consumers.

### Preview

Opt-in access to upcoming features before they graduate to stable. Preview features must be explicitly marked in the affected API documentation and release notes before release.

Preview guarantees:
- Preview features may change or be removed without a major version bump.
- Preview features must graduate to stable or be removed within **3 minor releases** of their introduction.
- Clients should not depend on preview behavior in production workflows.

### LTS (Long-Term Support)

When a major version is designated as LTS:
- It receives security fixes for a minimum of **12 months** after the LTS designation date.
- No new features are added; only security patches and critical bug fixes.
- The LTS designation and end-of-support date are published in release notes and this document.

## Deprecation Lifecycle

### 1. Announce deprecation
- Mark deprecated operations in `docs/api-specs/admin-api.json` (`deprecated: true`).
- Document in release notes and migration guide.
- Add a replacement endpoint/pattern.

### 2. Grace period
- Maintain deprecated behavior for at least **2 minor releases** or **90 calendar days**, whichever is longer.
- Keep examples and caveats up to date in `docs/user/CONTROL_PLANE_MIGRATION_GUIDE.md`.
- Deprecated endpoints return a `Sunset` response header ([RFC 8594](https://www.rfc-editor.org/rfc/rfc8594)) indicating the planned removal date.
- Deprecated endpoints emit a `Deprecation` response header linking to the migration guide.

### 3. Removal
- Remove only in next major API version path, unless emergency security remediation is required.
- Prior to removal, verify that usage telemetry (if available) shows minimal active consumers.

## OpenAPI Contract Governance

### Baseline contract

The authoritative OpenAPI specification is maintained at `docs/api-specs/admin-api.json` and served at runtime at `/api/v1/admin/openapi.json`.

### CI enforcement

- `openapi-contract-governance.yml` validates OpenAPI shape and compares the admin contract against the baseline ref on every PR.
- Potential breakages fail CI by default.
- Intentional breakages must be explicitly approved by setting `OPENAPI_ALLOW_BREAKING_CHANGES=true` in CI.
- PRs that set `OPENAPI_ALLOW_BREAKING_CHANGES=true` must update `docs/user/CONTROL_PLANE_MIGRATION_GUIDE.md` in the same PR.

## Governance in CI

- `openapi-contract-governance.yml` validates OpenAPI shape and compares admin contract against baseline ref.
- `control-plane-sdk-governance.yml` validates reproducible SDK generation from the admin OpenAPI spec.
- `proto-wire-governance.yml` validates protobuf wire compatibility (see below).
- Potential breakages fail CI by default.
- Intentional breakages must be explicitly approved by setting the appropriate environment variable and updating migration/deprecation docs in the same PR.

## gRPC and Wire Compatibility

Protobuf contracts live at `src/Honua.Core/Transport/Proto/geospatial/v1/`. The following rules govern wire-format evolution:

- **No field renumbering.** Once a field number is assigned, it must not change.
- **Additive message evolution only.** New fields must be optional and appended with new (higher) field numbers.
- **Enum values are append-only.** New enum values are appended at the end; existing values must not be reordered or renumbered.
- **Deprecated fields** are marked with `[deprecated = true]`. If a field is removed, its field number must be `reserved` to prevent accidental reuse.
- **Breaking wire changes** require explicit review plus a documented migration and rollout plan before merge.
- **CI enforcement** via `buf breaking` in `proto-wire-governance.yml`. Wire-incompatible changes fail CI unless `BUF_ALLOW_BREAKING_CHANGES=true` is set.

For detailed wire compatibility rules, see [proto/WIRE_COMPATIBILITY.md](../../proto/WIRE_COMPATIBILITY.md).

## Database Migration Compatibility

Schema migrations follow these constraints to preserve rollback safety and multi-version deployment compatibility:

- **Forward-only and additive** where possible. Prefer adding columns/tables over modifying or dropping existing ones.
- **Staged destructive changes.** Dropping columns or tables requires a staged migration across at least **2 releases**:
  1. First release: stop writing to the column/table, add deprecation notice.
  2. Second release: drop the column/table.
- **Rollback scripts** must accompany destructive migrations. Every `ALTER TABLE DROP COLUMN` or `DROP TABLE` must have a corresponding rollback script that can restore the schema (without data) if needed.
- Migration tooling and conventions are documented in [ADR-0005 (DbUp)](../contributor/adr/0005-dbup-migrations.md).

## Required Docs for Breaking PRs

- Update migration guidance in `docs/user/CONTROL_PLANE_MIGRATION_GUIDE.md`
- Update control-plane reference in `docs/user/CONTROL_PLANE_API.md`
- Include breaking-change notes in release checklist

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-08 | Added release channels (stable/preview/LTS), Sunset header requirement, OpenAPI contract governance details, gRPC/wire compatibility section, database migration compatibility section. |
| — | Initial skeleton: compatibility contract, breaking change definitions, deprecation lifecycle, CI governance. |
