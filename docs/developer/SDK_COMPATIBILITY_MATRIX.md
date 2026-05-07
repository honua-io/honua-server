# Server + SDK Compatibility Matrix

This page is the canonical compatibility contract for Honua Server SDKs. The
versioning contract applies to the generated JavaScript/TypeScript, Python, and
.NET admin clients backed by `/api/v*/admin/*`; the CI gate also records the
current per-SDK live smoke coverage for seeded read paths so SDK regressions
that break common workflows are visible before release.

Use this page first when you need to:
- choose an SDK artifact for a server release
- plan a server or SDK upgrade
- decide whether release notes require SDK regeneration

Use it together with:
- [Machine-readable SDK compatibility version manifest](sdk-compatibility-versions.json)
- [2026-05 Preview release-train manifest](../../release/honua-2026-05-preview.json)
- [Control Plane API](../operator/CONTROL_PLANE_API.md)
- [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md)
- [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md)
- [SDK Migration Guide Baseline](SDK_MIGRATION_GUIDE_BASELINE.md)

## Scope and Invariants

- This matrix versions the control-plane/admin API contract and gates the
  protocol surfaces each SDK actually exercises today. JavaScript covers
  FeatureServer and OGC API Features smoke paths, Python covers readiness,
  catalog, and FeatureServer smoke paths, and .NET currently covers admin
  compatibility. It is not a comprehensive standards-conformance matrix for
  FeatureServer, OGC, OData, WMS, WMTS, or other protocol adapters.
- Current admin API major in this repo: `v1`.
- JavaScript/TypeScript, Python, and .NET SDK artifacts are generated from the
  same curated admin OpenAPI contract and should be treated as one versioned
  release set.
- Server and SDK must stay on the same admin API major.
- Production deployments should stay on `stable` or a designated `LTS` line.

## Machine-Readable CI Manifest

The CI source of truth for tested SDK/server refs is
[`sdk-compatibility-versions.json`](sdk-compatibility-versions.json). The
manifest defines:

- `matrixDepth`: the supported last-N depth. The initial value is `3`.
- `serverRefs`: the last three server refs to exercise. `HEAD` means the
  workflow trigger commit and is resolved to `github.sha` by CI.
- `sdkSetVersions`: the last three generated SDK sets. Each set pins the
  JavaScript/TypeScript, Python, and .NET refs independently because release
  tags do not always share the same name.
- `matrix.supported`: blocking compatibility cells. Any failing supported cell
  is a CI regression.
- `matrix.evaluation`: non-blocking cells that still run and appear in the
  report.
- `matrix.unsupported`: intentionally excluded cells.

When a server release or SDK set ships, update the manifest first: add the new
ref at the top of the relevant list, keep exactly `matrixDepth` entries, move
or remove cells under `matrix.supported` / `matrix.evaluation` to match the
support policy, then run the SDK compatibility workflow manually. Replace the
temporary trunk-lineage commit refs with release tags as soon as the release
process publishes them.

The `sdk-server-compatibility.yml` workflow flattens this manifest into a
`fail-fast: false` GitHub Actions matrix on manual dispatch and the weekly
schedule. It publishes per-cell JSON evidence plus a
`sdk-compatibility-matrix-<run-id>` artifact containing a Markdown table and
machine-readable summary.

The per-SDK integration lanes use this manifest as the server-side compatibility
contract and can call `.github/workflows/reusable-sdk-pr-gate.yml` from each SDK
repository for PR-local checks. The cross-version gate remains owned here so SDK
repos do not need to duplicate the server ref policy.

## Repo-Local SDK Trackers

Each SDK repo owns its implementation lane and CI command. The issue body is the
repo-local implementation plan; this server repo owns only seed/bootstrap
contracts, matrix policy, and release evidence consumption.

| SDK repo | Tracker | Server contract consumed |
|---|---|---|
| `honua-sdk-js` | [`honua-io/honua-sdk-js#39`](https://github.com/honua-io/honua-sdk-js/issues/39) | Real seeded server, public JS SDK APIs, protocol-surface diagnostics |
| `honua-sdk-dotnet` | [`honua-io/honua-sdk-dotnet#31`](https://github.com/honua-io/honua-sdk-dotnet/issues/31) | Real seeded server, public .NET SDK APIs, protocol-surface diagnostics |
| `honua-sdk-python` | [`honua-io/honua-sdk-python#21`](https://github.com/honua-io/honua-sdk-python/issues/21) | Real seeded server, public Python SDK APIs, protocol-surface diagnostics |

## Server Bootstrap Contract

The server-owned compatibility target is a real Honua Server process backed by
PostGIS and seeded with `tests/seed/base-schema.sql`.

| Setting | Default | Meaning |
|---|---|---|
| `HONUA_SERVER_BASE_URL` | `http://localhost:5000` | Base URL used by SDK integration tests |
| `HONUA_ADMIN_API_KEY` | `ci-admin-password` | Admin key for compatibility metadata and admin SDK checks |
| `HONUA_SDK_SERVICE_ID` | `test_service` | Seeded service used by FeatureServer/catalog checks |
| `HONUA_SDK_LAYER_ID` | `0` | Seeded layer used by read/query checks |
| `HONUA_SDK_SEED_PROFILE` | `tests/seed/base-schema.sql:test_service:layer0` | Human-readable seed profile recorded into evidence |

SDK repos may either connect to an externally supplied server using these
variables or start an equivalent seeded server. Mock-handler-only results do not
qualify as release compatibility evidence.

## Evidence Contract

Each `compat-result.json` cell emitted by `sdk-server-compatibility.yml`
records:

- SDK package versions and checked-out refs for JavaScript, Python, and .NET
- Honua Server ref, resolved commit, channel, and image field when image-based
  runs are introduced
- seed profile, service id, layer id, and `protocol_surfaces_by_sdk` for the
  exact surfaces each SDK exercised in that cell
- pass/fail status, exit code, workflow run metadata, server log path, run log
  path, and a bounded failure log tail for reproduction

Do not infer implemented per-SDK protocol coverage from package-version capture
alone; the proof ledger must follow `protocol_surfaces_by_sdk`.

The smoke command uses a 40-minute command timeout inside a 75-minute job
timeout. The remaining job budget covers checkout/setup, kill grace, evidence
writing, artifact upload, and supported-cell failure handling so timed-out cells
still emit `exit_code: 124` and failure diagnostics.

The generated `sdk-compatibility-summary.json` embeds these cell records so
release owners can review both the matrix decision and the raw evidence fields
from one artifact.

## Release-Train Evidence

The 2026-05 Preview release lane is tracked by
[`release/honua-2026-05-preview.json`](../../release/honua-2026-05-preview.json).
That manifest is the release evidence index, not a second compatibility matrix.
It points back to `sdk-compatibility-versions.json` for SDK/server baselines and
records workflow evidence, image-validation state, waivers, and bounded
follow-up tickets for release-gating gaps.

Release notes and cross-repo scoreboards should link the release-train manifest
plus the generated `sdk-compatibility-matrix-<run-id>` artifact. Do not promote
source-build SDK compatibility evidence as release-candidate-image evidence
unless the evidence record names the exact image tag and digest tested.

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

Any release that changes `docs/developer/api-specs/admin-api.json` or the generated
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
