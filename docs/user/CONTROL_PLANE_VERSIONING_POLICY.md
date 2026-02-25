# Control Plane API Versioning and Deprecation Policy

This policy applies only to Honua control-plane/admin endpoints (`/api/v*/admin/*`).
It does not apply to standards APIs (FeatureServer, OGC, OData, WMS/WMTS).

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

## Deprecation Lifecycle

1. Announce deprecation
- Mark deprecated operations in `docs/api-specs/admin-api.json` (`deprecated: true`).
- Document in release notes and migration guide.
- Add a replacement endpoint/pattern.

2. Grace period
- Maintain deprecated behavior for at least 2 minor releases or 90 days, whichever is longer.
- Keep examples and caveats up to date in `docs/user/CONTROL_PLANE_MIGRATION_GUIDE.md`.

3. Removal
- Remove only in next major API version path, unless emergency security remediation is required.

## Governance in CI

- `openapi-contract-governance.yml` validates OpenAPI shape and compares admin contract against baseline ref.
- Potential breakages fail CI by default.
- Intentional breakages must be explicitly approved by setting `OPENAPI_ALLOW_BREAKING_CHANGES=true` in CI and updating migration/deprecation docs in the same PR.

## Required Docs for Breaking PRs

- Update migration guidance in `docs/user/CONTROL_PLANE_MIGRATION_GUIDE.md`
- Update control-plane reference in `docs/user/CONTROL_PLANE_API.md`
- Include breaking-change notes in release checklist
