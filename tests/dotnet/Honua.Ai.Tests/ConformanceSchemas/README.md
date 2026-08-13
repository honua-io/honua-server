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

### ADR-0030 composition-interaction schemas (PRE-MERGE pin)

Three files are pinned to an **unmerged** upstream branch rather than to the
`eb53989` trunk commit above:

- `geospatial-mcp/tools/bind_interaction.schema.json`
- `geospatial-mcp/tools/remove_interaction.schema.json`
- `geospatial-mcp/common/interactions.schema.json`

They are byte-identical to `spec/schemas/` on geospatial-mcp branch
`feat/adr-0030-interactions` (commit `9cbc35e`, "feat(spec): declarative
interactions and layout for composition documents (composition profile)"),
proposed in **geospatial-mcp PR #67** and **not yet merged to that repo's
trunk**. Honua ships the server-side reference implementation of ADR-0030
(`honua_studio_bind_interaction` / `honua_studio_remove_interaction`), so the
schemas are vendored ahead of the upstream merge to keep the conformance suite
binding.

Two honest consequences, recorded here rather than papered over:

- The upstream `index.json` marks both tools `implementationStatus:
  "known-gap"` with `referenceToolName: null` (the ADR was written before this
  implementation landed). The vendored `index.json` copy marks them
  `implemented` with the reference tool names, which is what is true of the
  live surface. **When PR #67 merges, the upstream index flips to match and
  these entries must be re-vendored rather than kept hand-edited.**
- The upstream fixtures under `conformance/fixtures/tools/{bind,remove}_interaction/`
  address a composition document by `mapPackageId`. Honua addresses the
  optimistic-concurrency draft the composition lives in, a spelling ADR-0030
  admits as `x-honua-reference-shape` and the tool schemas declare
  (`draftId` + `generation`). The vendored fixtures are therefore the
  reference-shape variant of the upstream instances — they validate against
  both the standard schema and Honua's live `inputSchema`, which is the
  property the conformance suite actually asserts.
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
