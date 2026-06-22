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
