# Metadata Prevalidation Admin API

`POST /api/v1/admin/metadata/prevalidate` generates a server-owned compatibility report for a Metadata v2 release package against a named target environment. Console can call it before opening a Git PR, and CI can call the same endpoint after a release package is committed.

The endpoint is admin-authorized and does not execute data scripts. It compares the proposed package state with the target environment's current Metadata v2 graph snapshot.

## Request

Provide exactly one package source:

- `releasePackageId`: persisted `MetadataReleasePackage` identifier.
- `releasePackage`: inline `MetadataReleasePackage` payload for pre-PR drafts.

Required fields:

- `targetEnvironment`: target environment name.
- `dataScripts`: optional declared script contracts. Scripts may cover findings only when `beforeContract` matches current target state and `afterContract` satisfies the missing requirement.

## Response

The response is `ApiResponse<MetadataCompatibilityReport>`.

Report status values:

- `ready`: no findings block or warn.
- `warning`: no uncovered errors, but warnings or script-covered errors remain.
- `blocked`: at least one error finding is not covered by a declared script.
- `unknown`: source package state, target graph state, or comparable declared metadata is unavailable.

`canCreatePullRequest` and `canPromote` are `false` for `blocked` and `unknown` reports.

## Findings

Each finding includes a stable `code`, `severity`, `kind`, affected semantic id/kind, safe `expected` and `actual` details, `requiredAction`, and data-script coverage state.

Rollback readiness is classified as:

- `metadata-only`
- `service-revision`
- `script-reversible`
- `snapshot-required`
- `manual`
