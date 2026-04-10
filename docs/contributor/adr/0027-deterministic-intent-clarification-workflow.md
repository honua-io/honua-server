# ADR-0027: Deterministic Intent, Clarification, and Plan Validation Workflow

## Status

Proposed

## Context

LLMs are useful for interpreting goals, ranking options, and proposing analysis
plans, but they are not reliable enough to execute geospatial workflows safely
without a deterministic control structure.

Specific risks include:

- silent guessing when requests are underspecified
- invalid or non-executable tool sequences
- unsupported or unauthorized capability use
- inconsistent result packaging
- hidden assumptions and poor provenance

Honua already has a precedent in the NL query pipeline:

- a constrained `FilterPlan`
- a compiler that validates properties and operators
- deterministic translation into a canonical AST

That pattern should be generalized for analyst and builder workflows.

## Decision

Honua will standardize on a deterministic workflow around a probabilistic
planner.

### Workflow Shape

The workflow will be:

1. capture partial intent
2. ground against available data, processes, templates, and policies
3. decide whether clarification is required
4. gather clarification via structured elicitation when required
5. compile a canonical plan
6. validate the plan deterministically
7. execute through deterministic services
8. package outputs with map, provenance, and optional app artifacts

### Clarification Policy

Clarification is required when:

- a required input is missing
- multiple materially different interpretations remain plausible
- a destructive or publish action is requested
- a policy or permission boundary is crossed
- grounding confidence falls below the configured threshold

Clarification is not required when:

- defaults are explicitly allowed by policy
- the assumption is low-risk and can be recorded transparently
- the workflow is in draft or dry-run mode

### Deterministic Boundaries

The model may:

- interpret user language
- rank candidate datasets
- rank candidate processes
- propose map/app refinements

The deterministic system must own:

- schema validation
- capability checks
- authorization
- state transitions
- artifact persistence
- result package shape
- provenance capture

### Result Packaging

Every executed workflow must emit explicit stage results and a final result
package with:

- status
- assumptions
- provenance
- artifacts
- `MapPackage`
- `AppPackage` when produced

## Consequences

### Positive

- Honua gets a repeatable, auditable workflow instead of prompt-only behavior.
- Clarification becomes policy-driven instead of ad hoc.
- MCP and gRPC can share the same canonical plan and result objects.
- Evaluation becomes measurable at each stage, not only at final output.

### Negative

- More contract design work is required up front.
- The planner/executor split introduces more interfaces and result types.
- Some apparently simple tasks will require extra clarification turns by design.

### Follow-On Work

- Define the canonical `AnalysisIntent`, `AnalysisPlan`, and stage result types.
- Define MCP elicitation flows for clarification.
- Define plan validation and result packaging contracts in gRPC.
- Build an evaluation suite that scores clarification quality, plan validity, and
  final output quality separately.
