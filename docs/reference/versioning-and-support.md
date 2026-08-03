# Versioning and support

How each public surface of Honua is versioned, what counts as a breaking change, and how deprecations are rolled out. Standards-based protocols are versioned by their specifications; only the admin/control-plane API and the gRPC contract carry Honua-owned version numbers.

## Admin API (`/api/v1/admin/*`)

The current and only published major version is `v1`. Within `v1`, all changes are additive and backward compatible:

- Allowed without a version bump: new optional request fields, new response fields, new endpoints/resources, new optional authentication alternatives.
- Breaking (requires a new major path such as `/api/v2/admin/*`, except emergency security fixes): removing an endpoint or HTTP method, removing request/response fields, making optional fields required, removing supported media types or security schemes, changing a field type/enum in a way older clients cannot parse.

### Release channels

| Channel | Guarantee |
| --- | --- |
| Stable | Backward compatibility for the published major path; all changes additive. This is the currently published channel. |
| Preview | Opt-in; may change or be removed without a major bump; must graduate or be removed within 3 minor releases. No preview path is currently published. |
| LTS | When designated: security fixes for at least 12 months from designation; no new features. Designations are published in release notes. |

### Deprecation lifecycle

1. **Announce** — mark the operation `deprecated: true` in `docs/developer/api-specs/admin-api.json`, document the replacement in release notes and the [migration guide](control-plane-migration-guide.md).
2. **Grace period** — deprecated behavior is maintained for at least 2 minor releases or 90 days, whichever is longer. Deprecated endpoints return `Deprecation` and `Sunset` ([RFC 8594](https://www.rfc-editor.org/rfc/rfc8594)) response headers.
3. **Removal** — only in the next major path, unless emergency security remediation is required.

### Contract governance

The admin OpenAPI baseline is `docs/developer/api-specs/admin-api.json`, served at runtime at `/api/v1/admin/openapi.json` ([details](openapi-and-explorer.md)). CI validates the contract shape and diffs against the baseline on every PR; breaking diffs fail by default. An intentional break must be described in the PR, update the migration/deprecation documentation, and check the exact `OPENAPI_BREAKING_CHANGE_APPROVED` marker in the PR template. CI accepts the PR marker only when the diff also updates the control-plane migration guide, versioning policy, or release checklist. The acknowledgement keeps the governance job green but emits a warning annotation and a job-summary list of every suppressed finding. SDK regeneration from the baseline is validated by a separate CI workflow.

The repository-wide `OPENAPI_ALLOW_BREAKING_CHANGES` variable is a temporary pre-publication override. It produces the same visible warnings when it suppresses findings and must be `false` before the first published control-plane release; that release-time verification is tracked as the `honua-release#71` first-release gate. Once reset, acknowledgment is scoped to the PR that introduces the change rather than silently authorizing future PRs.

SDK generation, usage examples, and the breaking-change upgrade flow are covered in the standalone [control plane migration guide](control-plane-migration-guide.md).

## Standards protocols (OGC, GeoServices, OData, STAC)

Standards APIs are versioned by their specifications, not by Honua: OGC API building blocks declare conformance classes on each landing page, classic OGC services negotiate spec versions (WMS 1.3.0, WFS 2.0, WCS 2.0.1, WMTS 1.0), the GeoServices REST adapter tracks the ArcGIS REST contract, and OData follows OData v4. Compatibility status per protocol lives in [OGC conformance](compatibility/ogc-conformance.md), [GeoServices parity](compatibility/geoservices-parity.md), and the [protocols overview](../concepts/protocols.md).

## gRPC (`geospatial.v1`)

Protobuf contracts are owned in the [`honua-io/geospatial-grpc`](https://github.com/honua-io/geospatial-grpc) repository; a service lives under exactly one major package version and majors are hosted side by side. Within a major version the wire contract is frozen:

- No field renumbering or wire-type changes; message evolution is additive only (new optional fields, new field numbers).
- Enum values are append-only; removed fields have their numbers `reserved`.
- Deprecated fields are marked `[deprecated = true]`; breaking wire changes require explicit review and a documented migration plan.
- CI enforcement lives in the `geospatial-grpc` repository.

Connection details and service list: [gRPC reference](protocols/grpc.md).

## Database migrations

Schema migrations are forward-only and additive where possible. Destructive changes are staged across at least two releases (stop writing, then drop) and must ship rollback scripts. Migrations can be skipped at startup with `HONUA_SKIP_MIGRATIONS=true` (run them out of band instead).

## Related pages

- [Control plane migration guide](control-plane-migration-guide.md)
- [OpenAPI and the API explorer](openapi-and-explorer.md)
- [Admin API overview](admin-api/overview.md)
