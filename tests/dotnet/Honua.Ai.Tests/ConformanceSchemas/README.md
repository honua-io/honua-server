# Vendored geospatial-mcp JSON Schemas

This directory is a **vendored copy** of the published JSON Schemas from the
[`geospatial-mcp`](https://github.com/honua-io/geospatial-mcp) standard
(`spec/schemas/`). It exists so `McpTaxonomyAlignmentTests` can assert that
Honua's live `/mcp` advertised tool input schemas and resource payload shapes
**conform to the open standard Honua champions** — Honua is the reference
implementation.

## Provenance

- `geospatial-mcp/` is byte-identical to `spec/schemas/` of `geospatial-mcp`
  trunk commit **`eb53989cc61c856261cf017b4b5a8e721317dc41`**
  ("feat: direct geoprocessing verbs (analysis profile) +
  geometryPrecision/maxInlineBytes (#55)", 2026-07-06), re-vendored wholesale
  per honua-server#2496. Verify with
  `diff -r <geospatial-mcp>/spec/schemas geospatial-mcp/` (compare LF blob
  content; this repo normalizes to LF).
- `fixtures/` mirrors the standard's own conformance fixture tree
  (`conformance/fixtures/` at the same commit), minus the upstream `README.md`
  and `validate.py` (not test inputs), plus the two Honua-extension fixture
  directories noted below.
- The pin is deliberately held at `eb53989` (pre geospatial-mcp#58): the #58
  platform-ops schemas are marked implemented in the manifest but honua-server
  does not serve those tools yet, so re-vendoring past #58 would introduce
  conformance failures. The post-#58 bump is owned by #2555/#2566, which
  implement the new tools and vendor their schemas together.
- Do not hand-edit. Re-vendor the whole tree from a single pinned upstream
  commit when the schemas change, and update this note with the new commit.
- Files are copied to the test output directory (`CopyToOutputDirectory`) and
  loaded at test time.

The conformance tests validate representative valid argument/payload instances
against both Honua's live schema and the vendored standard schema, and assert
structural alignment (required fields, enum values) for the tools Honua
implements today. Standard tools Honua does not yet implement as discrete MCP
tools (the map/app composition and publish families, the `mutation` profile's
`edit_features` per ADR-0028, and the `analysis` profile's direct
geoprocessing verbs) are tracked as **known-gaps**: their absence does not
fail the suite; only NON-CONFORMANCE of an implemented tool fails.

## Skills live-surface contract provenance

- `geospatial-mcp/skills/` vendors `skills/catalog.json`,
  `skills/catalog.schema.json`, and `skills/contracts/live-surface.json` from
  geospatial-mcp commit **`bed89302a8b9ea0a168aa89554d867681ee732dd`**.
- The MCP conformance suite resolves the contract's canonical tool names to
  Honua's production descriptors and checks their advertised input schemas.
- Skill prose and evaluation artifacts remain owned by geospatial-mcp and are
  not vendored into honua-server.

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
together; their fixtures under `fixtures/tools/resolve_entity/` and
`fixtures/tools/list_capabilities/` are maintained here (the upstream fixture
tree does not carry them yet).
