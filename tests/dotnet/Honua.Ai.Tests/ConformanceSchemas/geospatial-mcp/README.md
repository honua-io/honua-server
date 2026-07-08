# Machine-Readable JSON Schemas

**Status:** Draft
**Date:** 2026-06-21
**Scope:** Implementable JSON Schema (draft 2020-12) bindings for the geospatial MCP standard

This directory makes the prose specification **implementable**. It publishes
[JSON Schema](https://json-schema.org/) (draft 2020-12) documents describing:

- each core MCP **tool** `inputSchema` (the argument shape an agent sends on a
  `tools/call`), under [`tools/`](tools/);
- each core MCP **resource** payload shape (the read-only projection a
  `resources/read` returns), under [`resources/`](resources/).

A machine-readable [`index.json`](index.json) maps every standard tool name and
resource family URI to its schema file, so a harness can resolve a schema from
a vocabulary key without hard-coding paths.

A self-check **conformance fixture tree** lives under
[`../../conformance/fixtures/`](../../conformance/fixtures/): example tool-call
inputs and resource payloads that validate against these schemas. An
implementer can run their JSON Schema validator over the fixtures to confirm the
schemas load and that the examples conform, before testing their own server.

## Relationship to the prose

These schemas are **derived from**, and faithful to, the normative prose:

- Tool families and names come from
  [`spec/taxonomy.md` §MCP Tools to Workflow Family Mapping](../taxonomy.md#mcp-tools-to-workflow-family-mapping).
- Resource families, URI grammar, and inspection fields come from
  [`spec/resources.md`](../resources.md).
- The canonical concept model (`AnalysisPlan`, `ArtifactRef`, `WorkspaceRef`,
  `GeoprocessingError`, …) comes from
  [`spec/taxonomy.md` §Canonical Concept Model](../taxonomy.md#canonical-concept-model).

### Where the prose is intentionally upstream-owned

The prose **defers concrete field spellings** for several canonical objects to
`honua-server` (`MapPackage`, `AppPackage`, `PublishedService`, `Deployment`
— see [`resources.md` §2.4 Deferred-Shape Fixtures](../conformance.md#24-deferred-shape-fixtures)
and the responsibility-level projections in `resources.md`). For those, the
prose deliberately gives **responsibilities**, not a frozen field table.

Per the task of making the standard implementable, where the prose is ambiguous
or upstream-owned, **these schemas adopt the shape the reference implementation
(Honua's `/mcp`) already emits**, and mark it as such with a
`x-honua-reference-shape: true` annotation and a `description` note. This keeps
the standard constructible today while remaining honest that the concrete field
names track the reference implementation until the upstream ticket lands.

Resources whose concrete shape is still fully deferred upstream (e.g.
`MapPackage`, `AppPackage`, `PublishedService`, `Deployment`,
`PublishingResultPackage`) carry **responsibility-level** schemas: the stable
identifier and `honua://` URI are pinned and required, additional properties are
permitted (`additionalProperties: true`), and the responsibility fields are
described but not over-constrained. This matches the conformance rubric, which
scores those resources on stable identifiers, URI grammar, and
responsibility-level projection while marking concrete-field assertions
`not_applicable`.

## Draft and dialect

All schemas declare:

```json
"$schema": "https://json-schema.org/draft/2020-12/schema"
```

and a stable `$id` of the form
`https://geospatial-mcp.honua.io/spec/schemas/{tools|resources}/{name}.schema.json`.

## Tool-name prefixing

The standard names tools without a vendor prefix (`plan_analysis`,
`ground_candidates`, …; see `taxonomy.md`). A conformant server MAY advertise
the tools under a vendor-namespaced name — the reference implementation
advertises e.g. `honua_plan_analysis`. The `index.json` lists both the bare
standard name and the reference implementation's advertised name so a harness
can resolve either. The schemas validate the **argument object**, which is
prefix-independent.

## Files

| File | Describes |
|---|---|
| [`index.json`](index.json) | Machine-readable tool/resource → schema map |
| [`common/geoprocessing-error.schema.json`](common/geoprocessing-error.schema.json) | The canonical `GeoprocessingError` envelope (shared) |
| [`tools/*.schema.json`](tools/) | One `inputSchema` per core tool family |
| [`resources/*.schema.json`](resources/) | One payload schema per core resource family |
