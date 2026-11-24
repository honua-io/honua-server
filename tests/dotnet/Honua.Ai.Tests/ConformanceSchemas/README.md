# Vendored geospatial-mcp JSON Schemas

This directory is a **vendored copy** of the published JSON Schemas from the
[`geospatial-mcp`](https://github.com/honua-io/geospatial-mcp) standard
(`spec/schemas/`). It exists so `McpTaxonomyAlignmentTests` can assert that
Honua's live `/mcp` advertised tool input schemas and resource payload shapes
**conform to the open standard Honua champions** — Honua is the reference
implementation.

The `geospatial-mcp/ops-parity-map.yaml` route map is maintained by Honua Server,
separately from the upstream JSON Schemas. `SeatParityContractTests` enforces its
route coverage and human-only justifications. Console vendors that map from a
merged Server commit.

## Provenance

The base schema tree and upstream JSON fixtures are refreshed from geospatial-mcp
commit **`417d6a111f7a28b7f5405170b4b2923f0572e111`** on `fix/s-vocab-map-app-wire-contract`. This is a pre-merge
pin for the coordinated Map/App contract migration. Its standard reference
manifest was regenerated from server producer commit
`23a0a9c243bdcdc261ee0e8db7cf9c10a7f3f130`, with implementation extensions
reported separately; strict conformance and all 71 upstream fixtures pass. The
pinned changes rename identifiers and resource references without compatibility
aliases and align control identifiers with runtime validation.

The base schema files come from `spec/schemas/`, fixtures from
`conformance/fixtures/`, and the skills catalog and live-surface contract from
`skills/`. Do not hand-edit upstream files. Refresh upstream files from a
pinned commit and record the pin here; retain the scoped pins below.

Existing server input-validation constraints and the scoped upstream updates
below remain layered over the base snapshot. These include closed input
objects, paired service selectors and the server catalog declarations.

Server-only tool and result schemas remain Honua extensions. Composition
control and interaction fixtures retain the documented Honua draft reference
shape (`draftId` and `generation`) rather than the standard's Map identity;
these variants are validated against both the standard and live tool schemas.
The server-owned `ops-parity-map.yaml` remains separate from upstream schemas.

## Service-local selectors (pre-merge pin)

The `query_features`, `render_map`, `get_style`, `apply_style_preset`,
`list_layers` and `resolve_entity` input/result schema files and fixture
directories, plus `common/service-layer-reference.schema.json`, retain the selector contract from geospatial-mcp commit
**`7a6132ef66393139fe03648ac4670c7db18cec5e`**, reviewed in
[PR #187](https://github.com/honua-io/geospatial-mcp/pull/187).
They use `serviceLayerIndex` and reject the obsolete `layerId` selector.
The Map/App snapshot and existing server validation constraints layer over
these selectors; the obsolete selector remains rejected.
Refresh this pin to the merged upstream commit after certification against the
matching server candidate passes.

## Honua catalog extensions

The vendored `index.json` includes the upstream catalog plus server-only tools
and results. Those entries preserve reference-implementation coverage while the
upstream catalog owns standard names and resource families. `resolve_entity`
and `list_capabilities` now use their upstream schemas and fixtures.

## Platform-operation result contracts (scoped upstream pin)

`geospatial-mcp/tools/deploy_operations.result.schema.json` and
`geospatial-mcp/tools/platform_release_status.result.schema.json`, plus the
`deploy_operations_result_posture.json` and
`platform_release_status_result_null_{serving,execution}.json` fixtures, are
copied byte for byte from geospatial-mcp commit
**`5c844454e7170727d8f7adb1eb7db655a75f5519`**, reviewed in
[PR #196](https://github.com/honua-io/geospatial-mcp/pull/196).
This scoped refresh accepts canonical evidence posture and nullable release-plane
runtime/artifact fields; other schema families retain their documented pins.

## Server-only operation result contract

`supported_operation_kinds.result.schema.json` is a server-authored Honua
extension, marked `x-honua-extension`. Its enum tracks supported canonical
executor kinds, including `Geoprocess`. The runtime response remains a
projection of registered executors. Result tests validate fixtures against live
and reference schemas and preserve JSON text/structured-content parity.
