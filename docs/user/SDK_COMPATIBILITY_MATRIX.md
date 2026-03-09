# Server + SDK Compatibility Matrix

This page is the canonical compatibility contract for Honua Server control-plane
SDKs. It applies to the generated JavaScript/TypeScript, Python, and .NET
admin clients backed by `/api/v*/admin/*`.

Use this page first when you need to:
- choose an SDK artifact for a server release
- plan a server or SDK upgrade
- decide whether release notes require SDK regeneration

Use it together with:
- [Control Plane API](CONTROL_PLANE_API.md)
- [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md)
- [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md)
- [SDK Migration Guide Baseline](SDK_MIGRATION_GUIDE_BASELINE.md)

## Scope and Invariants

- This matrix applies only to the control-plane/admin API. It does not cover
  FeatureServer, OGC, OData, WMS, WMTS, or other standards APIs.
- Current admin API major in this repo: `v1`.
- JavaScript/TypeScript, Python, and .NET SDK artifacts are generated from the
  same curated admin OpenAPI contract and should be treated as one versioned
  release set.
- Server and SDK must stay on the same admin API major.
- Production deployments should stay on `stable` or a designated `LTS` line.

## SDK Families

| SDK family | Generated client | Example surface | Compatibility note |
|---|---|---|---|
| JavaScript / TypeScript | `typescript-fetch` | generated TypeScript client consumed from app code or automation | Follow the same server major and release channel as the target server. |
| Python | OpenAPI Python client | `honua_control_plane_sdk` package namespace | Generated from the same contract snapshot as the JS/.NET artifacts. |
| .NET | OpenAPI C# client | `Honua.ControlPlane.Sdk.*` namespaces | Generated from the same contract snapshot as the JS/Python artifacts. |

## Current Published SDK Lines

These are the current package lines in sibling SDK repositories as of the
March 2026 compatibility baseline:

| Package | Current line | Status | Intended server line |
|---|---|---|---|
| `@honua/sdk-js` | `0.0.1-alpha.x` | alpha | `v1` preview/beta control-plane line |
| `honua-sdk` | `0.0.1a*` | alpha | `v1` preview/beta control-plane line |
| `Honua.Sdk.Admin` | `0.1.0-alpha.x` | alpha | `v1` preview/beta control-plane line |
| `Honua.Sdk.Grpc` | `0.1.0-alpha.x` | alpha | `v1` preview/beta control-plane line |

These package versions are still pre-release. Pin exact package versions and
validate against the target server before broad rollout.

## Release Channel Mapping

| Channel | Server meaning | SDK meaning | Expected use |
|---|---|---|---|
| `stable` | Default production release on the current admin API major | Default production SDK artifacts for JS/TS, Python, and .NET | Match `stable` server with the same `stable` SDK line. |
| `beta` | Pre-release for the next `v1` minor or additive control-plane work | Matching beta SDK artifacts from the same contract snapshot | Use for staging and pre-production validation only. |
| `preview` | Early evaluation build; shape may still change between drops | Matching preview SDK artifacts only | Use for evaluation, not production commitments. |
| `LTS` | Stable server line explicitly called out in release notes for longer-lived support | Matching LTS SDK line with critical or security-only updates | Use when the server release itself is designated as LTS. |

`LTS` only applies when a release line is explicitly labeled as such in release
notes or release assets.

## Supported Server and SDK Combinations

The support rules below apply equally to the generated JavaScript/TypeScript,
Python, and .NET SDK artifacts.

| Server line | SDK line | Support | Notes |
|---|---|---|---|
| `stable` `v1` | Matching `stable` `v1` | Supported | Default production pairing. |
| `stable` `v1` | Older `stable` `v1` or matching `LTS` `v1` | Supported for backward-compatible releases | Safe only while release notes keep the admin API changes additive and do not call out a required SDK migration for workflows you use. |
| Designated `LTS` `v1` | Matching `LTS` `v1` | Supported | Preferred for pinned enterprise environments that want lower change velocity. |
| `beta` `v1` | Matching `beta` `v1` | Supported for validation only | Use for test environments, partner validation, or planned upgrade rehearsals. |
| `preview` line | Matching `preview` line | Evaluation only | Breaking changes may land between preview drops. |
| `stable` or `LTS` | `beta` or `preview` | Not supported | Do not mix pre-release SDKs into a production server line. |
| `beta` or `preview` | `stable` or `LTS` | Not supported | Pre-release servers should be exercised with the matching pre-release SDK line. |
| Any `v1` server | Any different admin API major | Not supported | A server and SDK must share the same admin API major. |

## Changelog Expectations

Any release that changes `docs/api-specs/admin-api.json` or the generated
control-plane SDK artifacts should call out:
- the server release channel and admin API major affected
- whether JavaScript/TypeScript, Python, and .NET artifacts all need to move
  together
- whether existing `stable` or `LTS` SDKs remain supported against the new
  server line
- added, deprecated, or removed fields/endpoints
- whether consumers need to regenerate or re-download SDK artifacts
- any required migration step for auth, write-path, import, or long-running
  operation changes

For `beta` and `preview` releases, changelog entries should also mark unstable
operations explicitly. If a change is breaking, the release notes must link to
the versioning policy and migration guide.

## Migration Guide Baseline

The baseline migration guidance for any server/SDK upgrade is:

1. Determine the target server release channel and admin API major.
2. Choose the matching SDK channel and the same admin API major for
   JavaScript/TypeScript, Python, or .NET.
3. Read the release notes for admin contract additions, deprecations, auth
   changes, and SDK regeneration requirements.
4. Refresh the SDK artifact if the release notes or contract diff say the admin
   surface changed.
5. Re-run the automation flows you depend on: auth, connection management,
   publish/update/import, and long-running operations.
6. If the release is breaking or deprecates an operation you use, follow the
   step-by-step process in [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md)
   before production rollout.

The reusable per-SDK document template lives in
[SDK Migration Guide Baseline](SDK_MIGRATION_GUIDE_BASELINE.md).
