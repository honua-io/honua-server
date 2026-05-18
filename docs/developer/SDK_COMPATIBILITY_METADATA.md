# SDK Compatibility Metadata

SDKs should use `GET /api/v1/admin/capabilities` as the runtime compatibility handshake for the Honua control plane.

- Canonical document: `data.compatibility`
- Purpose: decide whether the SDK can talk to this server without guessing endpoint shape or feature support
- Non-goal: inferring behavior from `serverVersion` strings alone

## Contract

Example response fragment:

```json
{
  "success": true,
  "data": {
    "resourceKinds": [
      "Service",
      "Layer",
      "Relationship",
      "Style",
      "Connection",
      "MapTemplate",
      "Theme",
      "Group",
      "SourceDescriptor"
    ],
    "compatibility": {
      "serverVersion": "1.2.3",
      "releaseChannel": "stable",
      "controlPlaneApi": {
        "major": 1,
        "basePath": "/api/v1/admin",
        "deprecated": false
      },
      "metadataSchemas": [
        {
          "version": "honua.io/v1alpha1",
          "deprecated": false
        },
        {
          "version": "honua.io/v1alpha0",
          "deprecated": true
        }
      ],
      "features": {
        "metadataResources": true,
        "manifestExport": true,
        "manifestApply": true,
        "manifestDryRun": true,
        "manifestPrune": true
      }
    }
  }
}
```

## SDK Use

1. Validate `controlPlaneApi.major` before constructing admin paths.
2. Enforce the SDK's documented minimum supported `serverVersion` only after the major matches.
3. Stop or degrade gracefully if `controlPlaneApi.deprecated` is `true`.
4. Prefer the newest `metadataSchemas` entry where `deprecated` is `false`.
5. Use `features` for coarse branches such as manifest workflows instead of probing endpoints.
6. Treat `releaseChannel` as rollout metadata and `serverVersion` as a minimum-version floor within the same major, not as the full feature contract.

## Catalog Metadata Kinds

`data.resourceKinds` advertises the metadata-resource kinds available through the generic CRUD surface. For catalog clients, `Group` and `SourceDescriptor` are first-class `honua.io/v1alpha1` kinds:

- `Group`: use `metadata.name` and `metadata.namespace` as the group identity. `spec.description` is optional display text.
- `SourceDescriptor`: store the SDK source descriptor in `spec.sourceDescriptor`. The descriptor must include non-empty `id` and `protocol` strings. Optional `locator`, `capabilities`, `schema`, and `attribution` fields follow `Honua.Sdk.Abstractions.Features.SourceDescriptor`.

Example `SourceDescriptor` metadata resource:

```json
{
  "apiVersion": "honua.io/v1alpha1",
  "kind": "SourceDescriptor",
  "metadata": {
    "name": "parks-source",
    "namespace": "default"
  },
  "spec": {
    "sourceDescriptor": {
      "id": "parks-source",
      "protocol": "geoservices-feature-service",
      "locator": {
        "serviceId": "parks",
        "layerId": 0
      },
      "capabilities": ["Query"]
    }
  }
}
```

## Minimum Version Check Rule

- First gate on `controlPlaneApi.major`. A different major is incompatible even if the semantic version string looks newer.
- When the major matches, compare `serverVersion` against the SDK's documented minimum supported server version using normal semantic-version ordering and ignoring build metadata.
- If a server reports an unparseable `serverVersion`, do not guess support from the string. Fall back to the major check plus `features` and log a warning for operators.
- Preview, alpha, beta, and RC builds should only be treated as supported when the SDK release notes explicitly call out that pre-release line.

## Release Channel Values

`releaseChannel` is derived from build version metadata and can report values such as `stable`, `lts`, `preview`, `alpha`, `beta`, `rc`, `nightly`, or `dev`.

## Notes

- `/api/v1/admin/version` remains available for legacy callers, but SDK compatibility decisions should use `/api/v1/admin/capabilities`.
- Metadata/catalog SDK parity should use
  [`metadata-catalog-endpoints.v1.json`](metadata-catalog-endpoints.v1.json)
  after the compatibility handshake. That inventory classifies public catalog
  reads, admin metadata reads, external migration inventory reads, and
  protocol-native metadata reads, and links the server-side OGC Records and SDK
  child issues.
- Adding new fields under `data.compatibility` is a backward-compatible `v1` change per the control-plane versioning policy.
