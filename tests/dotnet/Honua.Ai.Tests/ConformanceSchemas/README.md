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

The schema tree and upstream JSON fixtures are refreshed from geospatial-mcp
commit **`f494393a119fcff4f53ca6998a8df5160ff1203e`** on `fix/s-vocab-map-app-wire-contract`. This is a pre-merge
pin for the coordinated Map/App contract migration; its reference manifest is
still awaiting regeneration from the matching server producer. The pinned changes rename identifiers and resource references without compatibility
aliases and align control identifiers with runtime validation.

The schema files are copied from `spec/schemas/`, fixtures from
`conformance/fixtures/`, and the skills catalog and live-surface contract from
`skills/`. Do not hand-edit upstream files. Refresh the whole tree from one
pinned commit and record the pin here.

Server-only tool and result schemas remain Honua extensions. Composition
control and interaction fixtures retain the documented Honua draft reference
shape (`draftId` and `generation`) rather than the standard's Map identity;
these variants are validated against both the standard and live tool schemas.
The server-owned `ops-parity-map.yaml` remains separate from upstream schemas.

## Honua catalog extensions

The vendored `index.json` includes the upstream catalog plus server-only tools
and results. Those entries preserve reference-implementation coverage while the
upstream catalog owns standard names and resource families. `resolve_entity`
and `list_capabilities` now use their upstream schemas and fixtures.

## Server-only operation result contract

`supported_operation_kinds.result.schema.json` is a server-authored Honua
extension, marked `x-honua-extension`. Its enum tracks supported canonical
executor kinds, including `Geoprocess`. The runtime response remains a
projection of registered executors. Result tests validate fixtures against live
and reference schemas and preserve JSON text/structured-content parity.
