# Spec Grounding v1.0

`/v1/grounding/spec/*` turns natural-language edit requests into validated canonical-spec mutations and deterministic per-section summaries for the [Honua spec grammar v1.0](../../spec-grammar/v1.0/README.md). The surface is the spec-workspace counterpart to the workflow-focused [`GROUNDING.md`](../../GROUNDING.md) pipeline — it operates on the structured `SpecDocument` model rather than on ranked catalog candidates, and it never returns a spec that would fail `ISpecValidator`.

- **Implementation**: `src/Honua.Server/Features/Grounding/Spec/*`
- **Endpoint registration**: `EndpointRegistry` (`/v1/grounding/spec/mutate`, `/v1/grounding/spec/summarize`)
- **Related ADRs**: [ADR-0027 deterministic intent / clarification workflow](../../../contributor/adr/0027-deterministic-intent-clarification-workflow.md), [ADR-0028 no AI data editing](../../../contributor/adr/0028-ai-data-editing-not-allowed.md)

## Endpoints

Both endpoints accept and return `application/json` and currently allow anonymous access (the same surface is also intended for admin workspace use). All request and response DTOs flow through a source-generated `SpecGroundingJsonContext` for AOT compatibility.

### `POST /v1/grounding/spec/mutate`

Grounds one NL turn against an existing canonical spec and returns either a validated mutation plan, a structured clarification envelope, or a structured error.

Request body:

```json
{
  "spec": { "$schema": "...", "grammar": "v1.0", "kind": "analysis", ... },
  "turn": "only AE zones",
  "context": {
    "target_id": "hospitals",
    "default_crs": "EPSG:3857",
    "default_unit": "m",
    "hints": ["flood"]
  },
  "clarification_answer": {
    "intent_id": "spec-grounding-abc123",
    "answers": {
      "dataset.selection": ["catalog:layer:42"]
    }
  }
}
```

| Field | Required | Notes |
|---|---|---|
| `spec` | yes | Canonical JSON form of the current `SpecDocument`. An empty object is accepted and is equivalent to a fresh `analysis` spec. |
| `turn` | yes | Natural-language instruction. Multiple clauses can be separated by `.`, `;`, or newlines; each clause is grounded in order and the per-clause mutations are applied sequentially before the full batch is re-validated. |
| `context` | no | Disambiguation hints: default `target_id` for scope clauses, `default_crs`, `default_unit`, and freeform `hints`. When the turn omits a unit or CRS and `context` supplies one, the clarification is skipped. |
| `clarification_answer` | no | Echoes `intent_id` + `answers` from a prior clarification turn. Each `answers[questionId]` must contain at least one non-blank value. |

Response body (always 200 on a well-formed request; `400` with a Problem Details envelope for malformed JSON or missing `turn`/`spec`):

```json
{
  "mutation": {
    "mutations": [ { "kind": "add-compute", "compute_id": "...", "operator": "buffer", "inputs": { ... }, "parameters": { ... } } ],
    "next_spec": { "$schema": "...", "compute": [ ... ], ... },
    "sections_touched": ["compute"],
    "sections_preserved": ["sources", "scope", "map", "outputs"]
  },
  "clarifications": [],
  "warnings": [ { "code": "UnknownOperator", "severity": "warning", "message": "..." } ],
  "error": null
}
```

Exactly one of `mutation`, `clarifications[]`, or `error` is populated on any non-`400` response:

- `mutation.next_spec` is the canonical JSON emitted by `SpecCanonicalizer.ToJson(nextDocument)` after `ISpecValidator.Validate` reported no error-severity diagnostics.
- `mutation.sections_touched` / `sections_preserved` partition the canonical top-level section names (`sources`, `scope`, `compute`, `map`, `outputs`) based on which mutations ran. Sections in `sections_preserved` round-trip byte-for-byte through the canonical emitter.
- `clarifications[]` is one entry per `ClarificationQuestion`. Each entry carries `intent_id`, `kind` (one of `pick-dataset`, `pick-column`, `pick-value`, `specify-unit`, `specify-crs`, `choose-op`, `confirm-heavy-op`), `reason_codes[]`, `question_id`, `question_kind`, `prompt`, and typed `candidates[]`.
- `warnings[]` echoes non-error `SpecDiagnostic`s surfaced during validation or catalog lookup.
- `error.kind` ∈ `unresolvable`, `ambiguous`, `invalid_mutation`, `out_of_scope`. The response never includes a partially applied spec on error.

### `POST /v1/grounding/spec/summarize`

Request body:

```json
{ "spec": { ... } }
```

Response body:

```json
{
  "title_summary": "Runs buffer with 2 sources.",
  "section_summaries": [
    { "section_id": "sources", "text": "source hospitals uses dataset Hospitals. Source flood uses dataset Flood Zones." },
    { "section_id": "compute", "text": "Runs buffer as flood_buffer on input=@flood using distance=500.m and crs=EPSG:3857." }
  ]
}
```

Summaries are a pure deterministic function of the canonical AST: no LLM is called. Section ids align with the canonical section names so callers can pair a summary with the `sections_touched` / `sections_preserved` list from a mutation response.

## Closed mutation catalog

The S1 scope enumerates exactly nine mutation kinds. Any request that would require a different kind returns `error.kind = "out_of_scope"` with a pointer to `docs/developer/spec-grammar/v1.0/README.md`, which structurally honours ADR-0028 (no mutation kind targets row-level data).

| `kind` wire value | Payload | Effect |
|---|---|---|
| `add-source` | `source_id`, `source_type`, `source_ref` (`catalog:layer:<id>`) | Adds a `source` binding. Ignored (idempotent) when `source_id` is already present. |
| `remove-source` | `source_id` | Removes the binding. Errors with `invalid_mutation` when the id does not exist. |
| `add-scope-clause` | `target_id`, `predicate` (CQL2 string) | Adds a scope clause over `@target_id`. Duplicate predicate+target pairs are deduplicated. |
| `add-compute` | `compute_id`, `operator`, `inputs{}`, optional `parameters{}` | Appends a `ComputeStep`. Idempotent on `compute_id`. |
| `remove-compute` | `compute_id` | Removes the step. Errors with `invalid_mutation` when missing. |
| `set-map-layer` | `layer_ids[]` | Overwrites `map.layers` with the given id list. |
| `set-viewport` | `viewport{}` (e.g. `center`, `zoom`) | Overwrites `map.viewport` fields. |
| `set-output` | `output_id`, `expression` | Inserts or replaces the output at `output_id`. |
| `rename-reference` | `from_id`, `to_id` | Walks sources, scope targets, compute inputs/parameters, map layers/viewport/legend, and outputs — every `@from_id` or bare `from_id` reference is rewritten. Catalog URIs (`source.ref`) are not renamed. |

The canonical JSON returned in `mutation.next_spec` is always the output of `SpecCanonicalizer.ToJson`, so byte-for-byte preservation of untouched sections is a property of the canonicalizer's deterministic emitter plus the applier's pure AST rewrite.

## Deterministic clause grammar

The service's clause planner (`SpecGroundingService.PlanClauseAsync`) recognises the following lowercase patterns per clause. Recognised clauses emit mutations; unrecognised clauses that are not detectable as out-of-scope return `unresolvable`.

| Pattern | Example | Produces |
|---|---|---|
| `use <dataset phrase>` / `add <dataset phrase> as <id>` | `use hospitals` | `add-source` after resolving the phrase against the layer catalog (or a `pick-dataset` clarification). |
| `source <id> uses dataset <phrase>` | `source flood uses dataset Flood Zones` | Same as above, but with an explicit id. |
| `only <tail>` | `only AE zones` | `add-scope-clause` resolving the target from `context.target_id`, the first source matched in `tail`, or the single source when one exists. Multi-field layers surface `pick-column`; `or`-separated values surface `pick-value`. |
| `filters <target> where <predicate>` | `filters @hospitals where state = 'CA'` | `add-scope-clause` with the explicit predicate. |
| `buffer <target> by <n><unit> [in epsg:<code>] [as <id>]` | `buffer flood by 500.m in epsg:3857` | `add-compute` with operator `buffer`. Missing unit or CRS surfaces `specify-unit` or `specify-crs`. |
| `<left> within <n><unit> of <right> [in epsg:<code>]` | `hospitals within 500.m of flood` | Resolves both sides (adding `add-source` when needed), then emits an `add-compute(buffer)` + `add-compute(spatial_join)` pair. |
| `reproject <target> [to epsg:<code>] [as <id>]` | `reproject flood to epsg:3857` | `add-compute(reproject)`. |
| `runs <op> as <id> [on <inputs>] [using <params>]` | `runs buffer as flood_buffer on input=@flood using distance=500.m and crs=EPSG:3857` | `add-compute` with the literal operator, inputs, and params. |
| `rename <from> to <to>` | `rename hospitals to er` | `rename-reference`. |
| `remove source <id>` / `remove compute <id>` | `remove source flood` | `remove-source` / `remove-compute`. |
| `show <ids> on the map` | `show hospitals, flood_buffer on the map` | `set-map-layer` after validating each id against sources/compute/outputs. |
| `viewport center <lon> <lat> zoom <z>` | `viewport center -157.8 21.3 zoom 11` | `set-viewport`. |
| `output <id> returns <expr>` | `output at_risk returns @spatial_join` | `set-output`, normalising to `@<id>` form. |

Clauses containing `schedule`, `publish`, `deploy`, `dashboard`, or `app` short-circuit to `out_of_scope` before any planning runs. Clauses containing ` near ` without an explicit operator keyword (`buffer`, `spatial_join`) surface a `choose-op` clarification. Clauses mentioning `zonal_stats` / `zonal stats` / `zonal statistics` surface `confirm-heavy-op` before the mutation applies.

## Clarifications

Structured clarification kinds reuse the ADR-0027 `ClarificationRequest` / `ClarificationQuestion` model. The wire envelope flattens questions into one entry per `clarifications[]` element so workspace renderers can render each one as a card.

| `kind` | `question_id` | `question_kind` | `reason_codes` | Typed candidate fields |
|---|---|---|---|---|
| `pick-dataset` | `dataset.selection` | `single-select` | `ambiguous_dataset` | `candidate_type=dataset`, `catalog_ref`, `schema_preview[]` |
| `pick-column` | `column.selection` | `single-select` | `ambiguous_column` | `candidate_type=column`, `column_name`, `type_ref`, `nullable`, `sample` |
| `pick-value` | `value.selection` | `single-select` | `ambiguous_filter_value` | `candidate_type=value`, `value` |
| `specify-unit` | `unit.selection` | `single-select` | `ambiguous_unit` | `candidate_type=unit`, `unit` — fixed set: `km`, `m`, `mi`, `ft` |
| `specify-crs` | `crs.selection` | `single-select` | `ambiguous_crs` | `candidate_type=crs`, `crs` — current defaults: `EPSG:3857`, `EPSG:32604`, `EPSG:26910` |
| `choose-op` | `operator.selection` | `single-select` | `ambiguous_process` | `candidate_type=operator`, `operator_name` |
| `confirm-heavy-op` | `heavy.confirm` | `confirmation` | `heavy_operation_confirmation` | *(no candidates)* — any of `yes`, `true`, `confirm` in the answer counts as acknowledgement |

All clarifications carry the same `intent_id` across turns when the caller echoes it back in `clarification_answer.intent_id`; otherwise the service allocates a fresh `spec-grounding-<guid>` id.

## Failure envelope

| `error.kind` | Trigger |
|---|---|
| `unresolvable` | Input spec already has error-severity diagnostics; no clause parses; a referenced source/compute/output id does not exist; no catalog layers available to resolve a dataset phrase; map/output target cannot be resolved. |
| `ambiguous` | A clause requires clarification — the response also carries a non-empty `clarifications[]` and `clarification_answer` echoes the intent in a follow-up turn. |
| `invalid_mutation` | The applier threw `InvalidOperationException` (e.g. `remove-source` on a missing id), or the post-apply `ISpecValidator` returned error-severity diagnostics. |
| `out_of_scope` | Turn contains an S2/S3 keyword (`schedule`, `publish`, `deploy`, `dashboard`, `app`). The warnings list includes a pointer to `docs/developer/spec-grammar/v1.0/README.md`. |

Problem Details (`application/problem+json`) with status `400` is reserved for malformed wire payloads: missing `turn`, non-object `spec`, invalid `clarification_answer` shape. Validation or grounding failures always return `200` with a structured `error` envelope instead.

## Round-trip invariants

- Sections whose name is **not** in `sections_touched` are identical in canonical JSON between `spec` and `next_spec`. The applier operates on the AST and the canonicalizer is deterministic, so the preservation is structural rather than asserted.
- `summarize` is a pure function of the canonical AST — repeat calls on the same spec return identical `title_summary` and `section_summaries`.
- `mutate → summarize → mutate` converges: a turn derived from a summary produces a semantically equivalent spec (verified in `SpecGroundingServiceTests`).

## Telemetry

Metrics emit under the `honua.grounding.spec.*` namespace on the shared `HonuaTelemetry.Meter`:

| Instrument | Type | Tags |
|---|---|---|
| `honua.grounding.spec.mutate.turns` | counter | `clarified`, `retried`, `error` |
| `honua.grounding.spec.mutate.mutations` | counter | `mutation_kind`, `section` |
| `honua.grounding.spec.validation_failure` | counter | `diagnostic_code` |
| `honua.grounding.spec.summarize.count` | counter | `cached` |
| `honua.grounding.spec.summarize.latency` | histogram (`ms`) | `sections` |

Source-generated log events (`SpecGroundingLog`) reserve IDs `8220-8224`:

- `8220 MutateStarted` (Debug) — entry log per `MutateAsync` call.
- `8221 MutateCompleted` (Info) — emitted on success or clarification with mutation and clarification counts.
- `8222 MutateRejected` (Warning) — emitted for any structured error kind.
- `8223 SummarizeCompleted` (Info) — section count + duration.
- `8224 CatalogUnavailable` (Warning) — `ILayerCatalog` failure; the call continues with an empty catalog and a `CatalogUnavailable` warning in the response.
