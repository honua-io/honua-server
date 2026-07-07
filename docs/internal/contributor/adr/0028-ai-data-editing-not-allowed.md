# ADR-0028: AI-Driven Data Editing Is Not Allowed

## Status

Accepted

## Context

Honua's AI-first direction is centered on operator trust, clear guardrails, and
deterministic execution. That trust is materially harder to earn for data
editing than it is for:

- analysis
- publishing
- map generation
- app generation
- deployment planning

The risk profile of AI-driven editing is significantly higher:

1. **Operators are highly sensitive to unintended data mutation.** A rogue or
   incorrect AI edit damages confidence in the whole platform.
2. **Geometry authoring is semantically weak in natural language.** Requests
   such as "move this boundary slightly" or "clean up these shapes" are often
   under-specified and do not translate cleanly into safe deterministic edits.
3. **Edit mistakes are expensive.** Query, analysis, and publishing mistakes
   can often be rerun or discarded. Source-data edits can corrupt operational
   datasets and downstream systems.
4. **The trust story for v1 depends on inspectability.** Honua needs operators
   to trust that AI proposals are bounded, reviewable, and non-destructive by
   default.

Because of this, AI-driven data editing is not compatible with Honua's trust
model for the operator plane.

## Decision

Honua does not allow AI-driven data editing in the primary operator contract,
and it is not planned as a product workflow.

### In Scope For AI

AI may:

- inspect datasets
- profile and validate data quality
- identify schema or topology issues
- generate QA reports
- recommend candidate fixes
- propose edit plans for human review
- simulate the impact of a proposed change where practical

### Out Of Scope For AI

AI will not, as part of the primary operator contract:

- directly mutate source data
- apply attribute edits autonomously
- create, reshape, or delete geometry autonomously
- publish AI-generated edits as authoritative source changes

### Workflow Boundary

The operator workflow families are:

- `Analyze`
- `Publish Data`
- `Build App`
- `Automate / Deploy`

`Edit Data` is explicitly excluded.

### Trust Model

The trust boundary for v1 is:

- AI may inspect, recommend, and package results
- deterministic services may validate and execute non-destructive workflows
- source-data mutation remains under direct human control

Any future departure from this position would require a new ADR that explicitly
reverses this decision.

## Consequences

### Positive

- The operator trust model remains much stronger for initial adoption.
- Honua can focus on high-value AI workflows with lower mutation risk.
- The AI control plane remains easier to explain: analysis and publishing are
  first; editing is not silently bundled into the same autonomy model.

### Negative

- AI-assisted data correction and geometry workflows are not part of the
  roadmap.
- Some user requests that appear adjacent to publishing or QA will need to stop
  at recommendation rather than execution.

### Follow-On Work

- Ensure the AI operator contract and workflow docs exclude direct editing.
- Treat QA reports and fix recommendations as first-class non-destructive
  outputs.

## References And Reaffirmation (2026-07-06)

This decision was **reaffirmed by the founder on 2026-07-06**: AI operational
data editing is not supported by Honua, and it is explicitly forbidden. A
same-day proposal to reconcile this ADR via a "governed mutation profile" — an
authenticated, per-edit-type authorized, transactional MCP edit tool — was
**rejected**. This ADR stands unreconciled; a governed mutation profile is not
an accepted exception to it.

Enforcement in the codebase:

- The **MCP surface deliberately exposes NO feature-mutation tool.** There is no
  `honua_edit_features` (or any AI-facing feature-edit verb). The former MCP edit
  tool was removed — not merely disabled behind a flag — from
  `src/Honua.Ai/Features/Protocols/Mcp/Mcp/` (registration site, tool, schemas,
  models, output schema, capability catalog, and MCP tests). Do not reintroduce
  an MCP edit tool without a new ADR that reverses this decision.
- The **shared edit/transaction pipeline stays.** `IEditProcessor` /
  `IFeatureWriter.ApplyEditsAsync` and the shared authorization core
  (`ServiceDataEditorAuthorization`) continue to serve the **human-facing**
  protocol adapters (FeatureServer `applyEdits`, OGC API Features, WFS-T, OData
  CRUD, admin). What ADR-0028 forbids is projecting that pipeline onto an
  AI/MCP tool — not the pipeline itself.
- The open **geospatial-mcp** standard keeps an *optional* `mutation` conformance
  profile for other adopters, but records in its own
  `docs/adr/0028-governed-feature-mutation.md` (Addendum, 2026-07-06) that the
  reference implementation (Honua) does **not** implement it. Honua's conformance
  manifest declares the `base` profile only.
