# SDK Compatibility Matrix

This matrix defines the tested and supported combinations of Honua Server and SDK versions. Use this page to verify that your SDK version is compatible with your server deployment.

## Current Compatibility

| SDK | SDK Version | Minimum Server | Maximum Server | Status | Notes |
|-----|-------------|----------------|----------------|--------|-------|
| `@honua/sdk-js` | 0.0.1-alpha.0 | 1.0.0-beta | — | Alpha | Pre-release; API may change without notice |
| `honua-sdk` (Python) | 0.0.1a0 | 1.0.0-beta | — | Alpha | Pre-release; API may change without notice |
| `Honua.Sdk.Grpc` (.NET) | 0.1.0-alpha.1 | 1.0.0-beta | — | Alpha | Pre-release; API may change without notice |
| `Honua.Sdk.Admin` (.NET) | 0.1.0-alpha.1 | 1.0.0-beta | — | Alpha | Pre-release; API may change without notice |

All SDKs are currently in alpha. There are no backward-compatibility guarantees for alpha releases. Pin your SDK version and test against your target server version before upgrading.

## Protocol Coverage by SDK

Each SDK supports a different subset of Honua's protocol surface. The table below shows which protocols each SDK can consume.

| Protocol | `@honua/sdk-js` | `honua-sdk` (Python) | `Honua.Sdk.Grpc` (.NET) | `Honua.Sdk.Admin` (.NET) |
|----------|-----------------|---------------------|------------------------|-------------------------|
| GeoServices REST (FeatureServer) | Yes | — | — | — |
| GeoServices REST (MapServer) | Yes | — | — | — |
| OGC API Features | Yes | — | — | — |
| gRPC (Feature queries) | — | Yes | Yes | — |
| Admin API (REST) | — | Yes | — | Yes |
| OData v4 | — | — | — | — |

**Legend**: "Yes" = implemented and tested. "—" = not yet implemented in this SDK.

The JS SDK covers the broadest set of geospatial protocols (FeatureServer, MapServer, OGC API Features). The Python and .NET gRPC SDKs provide binary streaming access for high-throughput feature queries. The Python and .NET Admin SDKs cover the control-plane API for automation and headless management.

For protocol details, see [Geospatial Data APIs](STANDARDS_APIS.md). For control-plane API details, see [Server Management API](CONTROL_PLANE_API.md).

## Edition Compatibility

All SDKs work with all Honua Server editions (Community, Pro, Enterprise). Edition-specific features are detected at runtime via the capabilities endpoint:

```
GET /api/v1/admin/capabilities
```

SDKs should use the capabilities response to enable or disable features based on the server edition. There is no SDK-side license gating — all SDK functionality is available in the Community edition. Pro and Enterprise features are gated server-side and return actionable error responses (HTTP 402 or gRPC `PERMISSION_DENIED`) when accessed without a valid license.

For edition boundaries, see [ADR-0024: Open-Core Edition Model](../contributor/adr/0024-open-core-edition-model.md).

## Compatibility Rules

### Version Negotiation

SDKs should check server compatibility at initialization. The server exposes version and capability metadata via:

```
GET /api/v1/admin/version
GET /api/v1/admin/capabilities
```

The version response includes the server release version. The capabilities response includes feature flags the SDK can use for runtime feature detection. SDKs should fail fast with a clear error if the server version is below the SDK's minimum supported version.

### Support Window

- Each SDK major version supports the **current** and **previous** server minor version.
- When a new server minor version ships, the previous-minus-one server version enters a 90-day deprecation window for SDK support.
- SDK alpha/beta releases have no backward-compatibility guarantee.

### Version Status Definitions

| Status | Meaning |
|--------|---------|
| Alpha | Pre-release. API surface may change without notice. Not for production use. |
| Beta | Feature-complete for the target scope. API surface is stabilizing. Breaking changes require migration notes. |
| Stable | Production-ready. Follows semver. Breaking changes only in major versions. |
| LTS | Long-term support. Security fixes for 12+ months. No new features. |
| Deprecated | No longer actively maintained. Security fixes only for the remaining deprecation window. |

### Mapping to Server Release Channels

SDK version status aligns with the server's release channel model defined in [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md):

| SDK Pre-Release Tag | SDK Status | Server Release Channel |
|---------------------|-----------|----------------------|
| `-alpha.N` / `aN` | Alpha | preview |
| `-beta.N` / `bN` | Beta | preview |
| `-rc.N` / `rcN` | Beta | stable (release candidate) |
| (no tag) | Stable | stable |
| (LTS designation) | LTS | LTS |

## Related Documentation

- [Geospatial Data APIs](STANDARDS_APIS.md) — protocol-level API reference
- [Server Management API](CONTROL_PLANE_API.md) — control-plane API reference
- [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) — admin API versioning and deprecation
- [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md) — admin API migration guidance
- [MVP Compatibility Contract](MVP_COMPATIBILITY_CONTRACT.md) — launch compatibility and limitations
- [Client Template Version Matrix](CLIENT_TEMPLATE_VERSION_MATRIX.md) — desktop client compatibility (ArcGIS Pro, QGIS, Power BI, Excel)
- [SDK Migration Guide Baseline](SDK_MIGRATION_GUIDE_BASELINE.md) — migration guide structure for SDK releases
- [SDK Native Design Vision](../contributor/SDK_NATIVE_DESIGN_VISION.md) — long-term SDK architecture direction
