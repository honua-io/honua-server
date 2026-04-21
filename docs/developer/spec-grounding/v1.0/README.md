# Spec Grounding v1.0

`/v1/grounding/spec/*` turns natural-language edit requests into validated canonical-spec mutations and deterministic summaries.

## Endpoints

- `POST /v1/grounding/spec/mutate`
  - Request: `{ spec, turn, context?, clarification_answer? }`
  - Response: `{ mutation?, clarifications[], warnings[], error? }`
  - Success returns a `mutation` plan with closed mutation kinds, the validated `next_spec`, and touched/preserved section lists.
- `POST /v1/grounding/spec/summarize`
  - Request: `{ spec }`
  - Response: `{ title_summary, section_summaries[] }`
  - Each section summary is one or two short sentences and is deterministic for the same canonical spec.

## Clarifications

Structured clarification kinds map onto the ADR-0027 clarification model with typed candidates:

- `pick-dataset`
- `pick-column`
- `pick-value`
- `specify-unit`
- `specify-crs`
- `choose-op`
- `confirm-heavy-op`

The wire format carries `intent_id`, `reason_codes`, `question_id`, `question_kind`, `prompt`, and typed `candidates`.

## Failure Modes

Grounding never returns an invalid canonical spec. A mutate call resolves to exactly one of:

- a validated mutation plan
- a structured clarification envelope
- a structured error with `kind` in `unresolvable`, `ambiguous`, `invalid_mutation`, or `out_of_scope`

`out_of_scope` is reserved for requests outside the closed spec-mutation surface. Source data is never mutated.
