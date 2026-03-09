# SDK Compatibility Contract

The Honua Server exposes a compatibility metadata endpoint that SDKs use to negotiate
version compatibility, discover available features, and consume deprecation notices.

## Endpoint

```
GET /api/v1/admin/compatibility
```

Requires admin authentication. Returns the full compatibility metadata for the running
server instance.

## Response Shape

```json
{
  "success": true,
  "data": {
    "version": "1.0.0.0",
    "controlPlaneApiVersion": "v1",
    "releaseChannel": "stable",
    "edition": "community",
    "serverTime": "2026-03-08T12:00:00+00:00",
    "sdk": {
      "minimumSupportedVersions": {
        "js": "0.1.0",
        "python": "0.1.0",
        "dotnet": "0.1.0"
      },
      "compatibilityContract": "2026.1"
    },
    "capabilities": {
      "grpcStreaming": false,
      "distributedCache": false,
      "offlineSync": false,
      "cdc": false,
      "spatialAnalytics": false,
      "aiSpatialAgent": false,
      "sso": false,
      "rbac": false,
      "multiTenancy": false,
      "pluginSdk": false
    },
    "deprecations": []
  },
  "timestamp": "2026-03-08T12:00:00+00:00"
}
```

## Field Reference

| Field | Type | Description |
|-------|------|-------------|
| `version` | string | Server assembly version |
| `controlPlaneApiVersion` | string | Control plane API version (e.g. `v1`) |
| `releaseChannel` | string | One of `stable`, `preview`, or `lts` |
| `edition` | string | One of `community`, `pro`, or `enterprise` |
| `serverTime` | ISO 8601 | Current server UTC time |
| `sdk.minimumSupportedVersions` | map | Minimum SDK version per platform (`js`, `python`, `dotnet`) |
| `sdk.compatibilityContract` | string | Contract version identifier (e.g. `2026.1`) |
| `capabilities` | map | Boolean feature flags keyed by capability name |
| `deprecations` | array | Active deprecation notices (see below) |

## SDK Minimum Version Check Flow

SDKs should call this endpoint during initialization and compare their own version
against the appropriate entry in `sdk.minimumSupportedVersions`:

1. Fetch `GET /api/v1/admin/compatibility`.
2. Extract the platform key (`js`, `python`, or `dotnet`) from `sdk.minimumSupportedVersions`.
3. Compare the returned minimum version against the SDK's own version using semver.
4. If the SDK version is below the minimum, emit a warning or refuse to connect,
   depending on SDK policy.

This allows the server to enforce minimum SDK versions without requiring SDK updates
to be coordinated with server deployments.

## Feature Detection via Capabilities

The `capabilities` map provides boolean flags for features gated by server edition.
SDKs should use these flags to enable or disable feature-specific code paths at runtime
rather than hard-coding edition checks.

### Edition Capability Matrix

| Capability | Community | Pro | Enterprise |
|-----------|-----------|-----|------------|
| `grpcStreaming` | false | true | true |
| `distributedCache` | false | true | true |
| `offlineSync` | false | true | true |
| `cdc` | false | true | true |
| `spatialAnalytics` | false | true | true |
| `aiSpatialAgent` | false | true | true |
| `sso` | false | false | true |
| `rbac` | false | false | true |
| `multiTenancy` | false | false | true |
| `pluginSdk` | false | false | true |

## Deprecation Notice Consumption

Each entry in the `deprecations` array has the following shape:

```json
{
  "endpoint": "/api/v1/admin/some-old-endpoint",
  "sunsetDate": "2026-12-31",
  "replacement": "/api/v1/admin/new-endpoint",
  "message": "Use the new endpoint instead."
}
```

SDKs should:

1. Log a warning when calling a deprecated endpoint.
2. Surface the `sunsetDate` and `replacement` in developer-facing diagnostics.
3. Optionally redirect calls to the replacement endpoint if the SDK supports it.

## Contract Governance

Changes to this endpoint follow the control plane versioning policy documented in
[CONTROL_PLANE_VERSIONING_POLICY.md](CONTROL_PLANE_VERSIONING_POLICY.md):

- **Additive changes** (new capability flags, new fields) are non-breaking and may
  ship in any minor release.
- **Removing or renaming fields** requires a new API version (`v2`) and a deprecation
  notice in the current version.
- The `compatibilityContract` value is bumped whenever the contract shape changes in
  a way that SDKs need to be aware of.

## Configuration

The server reads edition and release channel from configuration:

| Config Key | Default | Description |
|-----------|---------|-------------|
| `Honua:Edition` | `community` | Server edition (`community`, `pro`, `enterprise`) |
| `Honua:ReleaseChannel` | `stable` | Release channel (`stable`, `preview`, `lts`) |
