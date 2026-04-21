# Honua Spec Grammar v1.0

The Honua declarative geospatial spec language is the shared surface between
the admin workspace (Spec IDE), the CLIs (`honua spec …`), the grounding
pipeline, and downstream plan/apply. It gives users a single text form for
describing sources, compute pipelines, maps, and outputs, and guarantees a
canonical JSON wire format that downstream services can consume without
re-parsing the text.

This directory publishes the v1.0 grammar artifacts:

- **[`spec.schema.json`](spec.schema.json)** — JSON Schema Draft 2020-12 for
  the canonical JSON form. Authoritative. All clients and servers validate
  against this schema.
- **[`spec.ebnf`](spec.ebnf)** — EBNF for the brace-based text projection
  (human-friendly; converted to canonical JSON by the parser).

## Status

v1.0 ships the **S1 resource set**: `source`, `scope`, `compute`, `map`, and
`output`. `map` is present but validated leniently at S1 (full layer/viewport
validation arrives with S2). The next minor versions (`v1.1`, `v1.2`) add
richer compute operators without breaking clients — any v1.x server accepts
any v1.y spec where y ≤ server minor.

## Surface at a glance

```
grammar "v1.0"
kind    "analysis"
title   "hospitals within 500 m of flood zones"

source hospitals {
  type = "layer"
  ref  = "osm:amenity=hospital"
}

source flood {
  type = "stac"
  ref  = "stac://climate/flood-100yr"
}

scope {
  target = @hospitals
  where  = cql2("state = 'CA'")
}

compute flood_buffer {
  op     = buffer
  inputs = { input = @flood }
  params = { distance = 500.m, crs = "EPSG:3857" }
}

compute at_risk {
  op     = spatial_join
  inputs = { left = @hospitals, right = @flood_buffer }
  params = { crs = "EPSG:3857" }
}

output at_risk_features {
  expr = @at_risk
}
```

### Two forms, one semantic

```
┌──────────────┐   parse    ┌──────────────┐  canonicalize  ┌──────────────┐
│  text form   │ ─────────▶ │   AST        │ ─────────────▶ │ canonical    │
│  (EBNF)      │            │ SpecDocument │                │ JSON         │
└──────────────┘ ◀───────── └──────────────┘ ◀───────────── └──────────────┘
                 pretty-print                   read
```

The canonical JSON is authoritative — it's what the hash/identity of a spec
is defined against and what server APIs accept over the wire. The text form
is a projection for humans and AI agents.

Round-trip invariants:

- **text → JSON** is deterministic. Object keys are sorted alphabetically,
  array declaration order is preserved, numbers use their minimal JSON form,
  and strings are UTF-8.
- **text → JSON → text** is semantically equivalent. Comments from the text
  form are preserved in `meta.comments` keyed by JSON-Pointer so tools can
  render them back during formatting.
- **JSON → AST → JSON** is byte-for-byte idempotent.

## Canonical JSON layout

Root keys, in sort order:

| key            | required | description                                              |
|----------------|----------|----------------------------------------------------------|
| `$schema`      | yes      | Fixed URL identifying this grammar version.              |
| `capabilities` | yes      | Operator catalog capability version (`{ operators }`).   |
| `compute`      | —        | Ordered array of compute steps (preserves declaration order; not keyed by id so user-chosen ids don't have to alphabetize). |
| `grammar`      | yes      | Grammar SemVer (`vMAJOR.MINOR`). Server accepts within current major, warns when minor is newer than the server. |
| `kind`         | —        | `analysis` \| `map` \| `app`. `map`/`app` are reserved for S2/S3 and emit warnings at S1. |
| `map`          | —        | Map section (S2 validates layers/viewport; S1 accepts free-form). |
| `meta`         | —        | Sidecar metadata. `meta.comments` keys are JSON-Pointers. |
| `outputs`      | —        | Ordered array of output bindings.                        |
| `scope`        | —        | Ordered array of scope clauses.                          |
| `sources`      | yes      | Ordered array of source bindings.                        |
| `title`        | —        | Optional human title.                                    |

### Unit and geometry encoding

Unit-carrying numeric literals (`500.m`, `15.min`, `2.ha`) are emitted as
structured objects, not strings, so downstream consumers don't have to
re-parse them:

```json
{ "kind": "distance", "unit": "m", "value": 500 }
```

Geometry literals round-trip through the NetTopologySuite WKT reader/writer:

```json
{ "crs": "EPSG:4326", "type": "geometry", "wkt": "POLYGON((…))" }
```

## Diagnostics

Every diagnostic carries a stable `code`, a 1-based `line`/`column`, and a
severity. The parser is diagnostic-collecting — syntax errors surface as
values, not exceptions, so IDEs see a partial AST even when input is broken.

| code                        | severity | emitted by       | meaning                                                                 |
|-----------------------------|----------|------------------|-------------------------------------------------------------------------|
| `SyntaxError`               | error    | lexer / parser   | malformed token (unterminated string, bad escape, unterminated comment) |
| `ParseError`                | error    | parser           | unexpected token, missing separator, unknown section name               |
| `DuplicateIdentifier`       | error    | resolver         | two sources/computes/outputs share an id                                |
| `UnknownReference`          | error    | resolver         | `@foo` doesn't match any declared id                                    |
| `CatalogUnavailable`        | warning  | resolver         | external catalog snapshot is empty (structural-only validation)         |
| `UnknownOperator`           | error    | type checker     | `op = …` names an operator not in the catalog                           |
| `TypeMismatch`              | error/warn | type checker   | input/parameter type doesn't satisfy the operator signature             |
| `MissingRequiredParameter`  | error    | type checker     | required input/parameter not supplied                                   |
| `CrsUnitMismatch`           | error/warn | semantic       | distance/area literal used with no CRS or a geographic CRS              |
| `UnsupportedGrammarVersion` | error    | semantic         | `grammar` directive is missing or outside the current major             |

The `line` value is 1-based. This is deliberately an improvement over the
existing CQL2 diagnostic surface (which only exposes character offsets).

## Operators (S1 catalog)

| operator       | inputs (port: type)                   | required params              | CRS-sensitive |
|----------------|---------------------------------------|------------------------------|---------------|
| `filter`       | `input: dataset`                      | `where: string`              | no            |
| `spatial_join` | `left: dataset`, `right: dataset`     | —                            | yes           |
| `buffer`       | `input: dataset`                      | `distance` (+ optional `crs`)| yes           |
| `reproject`    | `input: dataset`                      | `crs`                        | no            |
| `zonal_stats`  | `zones: dataset`, `values: raster`    | —                            | no            |
| `slope`        | `input: raster`                       | —                            | no            |

The operator catalog is versioned independently of the grammar. Its
capability version is published in `capabilities.operators` in every
canonical spec so clients know which operator set the document targets.
Minor bumps add new operators/parameters; major bumps reserve breaking
changes (e.g. renamed ports).

## AOT / source-generated JSON

The canonical emitter writes bytes directly via `Utf8JsonWriter` — there's
no `JsonSerializer.Serialize` call on the hot path and no reflection-based
polymorphic serializer. The small DTO surface (`CanonicalSpecHeader`,
`CanonicalSpecCapabilities`) is generated through `SpecJsonContext`, which
keeps the whole path AOT-safe.

## Embedding in the server

Service registration is opt-in per project:

```csharp
services.AddSpecGrammar();
```

This binds the three public interfaces as singletons, plus the default
S1 operator catalog:

- `ISpecParser` → `SpecParser` (text → `SpecParseResult(Document, Diagnostics)`)
- `ISpecCanonicalizer` → `SpecCanonicalizer` (AST → canonical JSON bytes/string)
- `ISpecValidator` → `SpecValidator` (AST → `SpecValidationResult(Diagnostics)`)
- `IOperatorCatalog` → `OperatorCatalog` (S1 signatures)

`SpecJsonReader` is a static class (canonical JSON → AST) used by the
round-trip tests and by consumers receiving specs over the wire.

`ISpecValidator.Validate` takes an optional `ISpecCatalogSnapshot` used to
resolve `@`-references against external services/layers. When omitted it
defaults to `StaticSpecCatalogSnapshot.Empty` — validation still runs but
downgrades unresolved external refs to a `CatalogUnavailable` warning so
offline/CI linting can run without a live catalog.

## Telemetry

Two `ActivitySource` instances emit per-pass spans and diagnostic counts so
the feature can be traced end-to-end without adding bespoke logging:

| activity source          | span            | tags                                                                         |
|--------------------------|-----------------|------------------------------------------------------------------------------|
| `Honua.Spec.Parse`       | `spec.parse`    | `spec.parse.tokenCount`, `spec.parse.errorCount`                             |
| `Honua.Spec.Validation`  | `spec.validate` | `spec.resolver.diagnostics`, `spec.type.diagnostics`, `spec.semantic.diagnostics`, `spec.total.diagnostics` |

Both sources follow the repository's existing `ActivitySource` pattern —
add them to your OpenTelemetry listener configuration the same way you
wire up any other feature slice.

## Extending the grammar

- **New operator.** Register it in `OperatorCatalog`. Bump the operator
  capability version in `SpecGrammarVersion.CurrentOperatorCapability`. The
  grammar version does not change.
- **New section or keyword.** Bump the grammar minor (e.g. `v1.0` → `v1.1`).
  Update both [`spec.ebnf`](spec.ebnf) and [`spec.schema.json`](spec.schema.json)
  in the same change. Servers at `v1.x` accept specs up to their own minor
  and surface `UnsupportedGrammarVersion` for anything newer.
- **Breaking change.** Bump the grammar major and publish a new directory
  (`v2.0/`). Keep the previous directory intact for back-compat reads.

## Related surfaces

- [Spec Grounding v1.0](../../spec-grounding/v1.0/README.md) — `/v1/grounding/spec/mutate`
  and `/v1/grounding/spec/summarize` author, refine, and describe canonical
  specs from natural-language turns while preserving unchanged sections
  byte-for-byte.
