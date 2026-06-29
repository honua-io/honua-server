# Vendored geospatial-mcp JSON Schemas

This directory is a **vendored copy** of the published JSON Schemas from the
[`geospatial-mcp`](https://github.com/honua-io/geospatial-mcp) standard
(`spec/schemas/`). It exists so `McpTaxonomyAlignmentTests` can assert that
Honua's live `/mcp` advertised tool input schemas and resource payload shapes
**conform to the open standard Honua champions** — Honua is the reference
implementation.

- Source: `geospatial-mcp` `spec/schemas/` (branch `feat/json-schemas-conformance`,
  PR honua-io/geospatial-mcp#21).
- Do not hand-edit. Re-vendor when the upstream schemas change.
- Files are copied to the test output directory (`CopyToOutputDirectory`) and
  loaded at test time.

The conformance tests validate representative valid argument/payload instances
against both Honua's live schema and the vendored standard schema, and assert
structural alignment (required fields, enum values) for the tools Honua
implements today. Standard tools Honua does not yet implement as discrete MCP
tools (the map/app composition and publish families) are tracked as
**known-gaps**: their absence does not fail the suite; only NON-CONFORMANCE of
an implemented tool fails.

## Honua extensions (`x-honua-extension`)

A few schemas under `geospatial-mcp/tools/` are **documented Honua extensions**
over the bare standard taxonomy rather than copies of an upstream schema. They
carry `"x-honua-extension": true` in the schema body:

- `resolve_entity.schema.json` — `honua_resolve_entity` (#1949): natural-language
  text → ranked service/layer references grounded in the live catalog.
- `list_capabilities.schema.json` — `honua_list_capabilities` (#1949): a
  self-describing manifest of the live tool/resource surface for a cold client
  LLM.

These are exposed as first-class reference-implementation tools while the
`geospatial-mcp` standard formalizes capability discovery / entity resolution
(the standard currently models them as `CapabilityCatalog` reads). When the
upstream standard publishes canonical schemas for them, re-vendor and drop the
extension marker. They participate in the full conformance assertions (required
fields + fixture validation) because their live and vendored shapes are authored
together.
