# Grounding & Intent Drafting

The grounding pipeline turns a natural-language operator goal into a typed
draft intent, a ranked shortlist of catalog candidates, and — when the
request is materially ambiguous — a structured clarification envelope. It is
the front door for the server-owned AI operator MCP surface described in
[MCP_SERVER.md](MCP_SERVER.md) and the canonical implementation of the
grounding contract in [ADR-0027](../contributor/adr/0027-deterministic-intent-clarification-workflow.md).

The pipeline is deterministic by default. Model-backed engines can plug in
through the same interface without touching the service, the authorization
graph, or the tool surface.

## Pipeline

```
GroundingRequest (goal, optional hint, explicit inputs, assumption policy)
        │
        ▼
┌──────────────────────────────────────────────────────────────────────┐
│ IGroundingEngine                                                     │
│  • Classify  → WorkflowFamilyClassification (family + confidence)    │
│  • ScoreProcesses / ScoreLayers / ScoreServices                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌──────────────────────────────────────────────────────────────────────┐
│ GroundingService                                                     │
│  1. Tokenize goal                                                    │
│  2. Engine classification + candidate scoring                        │
│  3. Apply confidence bands + MaxCandidatesPerKind cap                │
│  4. IGroundingAuthorizationFilter — drop candidates the principal    │
│     cannot see (shares the IOperatorAuthorizationEvaluator graph)    │
│  5. MaterialAmbiguityEvaluator — emit findings per ADR-0027          │
│  6. IntentDrafter — shape the typed DraftIntent                      │
│  7. Synthesise Clarification envelope from findings (if any)         │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
GroundingResult { workflowFamily, draftIntent, candidates, clarification?, engine }
```

The service owns no session state — callers carry the `intentId` and any
clarification answers back across turns via `honua_clarify_intent`.

## Workflow families

Source: `Honua.Core.Features.Grounding.Domain.WorkflowFamily`.

| Family | Status | Draft intent shape |
|--------|--------|--------------------|
| `Analyze` | Functional | `DraftAnalysisIntent` with scored process + dataset candidates |
| `PublishData` | Functional | `DraftPublishingIntent` (source kind, target kind, draft status) |
| `BuildApp` | Envelope-only | `EnvelopeStub` + `PolicyBoundary` clarification |
| `AutomateDeploy` | Envelope-only | `EnvelopeStub` + `PolicyBoundary` clarification |

The two envelope-only families classify and rank normally but the drafter
emits an envelope stub rather than a typed intent. `MaterialAmbiguityEvaluator`
always attaches a `PolicyBoundary` finding so the operator confirms the
envelope-only mode before the caller proceeds.

## Confidence bands

`ConfidenceBand` buckets the classifier and ranker's `[0.0, 1.0]` score.

| Band | Score range |
|------|-------------|
| `Low` | `score < MediumConfidenceFloor` (default `< 0.40`) |
| `Medium` | `MediumConfidenceFloor ≤ score < HighConfidenceFloor` (default `0.40 – 0.70`) |
| `High` | `score ≥ HighConfidenceFloor` (default `≥ 0.70`) |

A classification whose confidence is below `WorkflowFamilyFloor` (default
`0.60`) always triggers a `LowConfidence` clarification. The service still
emits a provisional draft intent based on the top-scoring family — callers
surface the draft alongside the clarification envelope so operators can
preview the current best guess and either confirm it or pick a different
family through the `workflow_family` question.

## Material-ambiguity rule set

`MaterialAmbiguityEvaluator` applies the ADR-0027 rules in a fixed order so
the conformance harness can pin the finding sequence. A single pass can
surface multiple findings — the clarification envelope carries every
finding so the caller can answer them in one turn.

| # | `ClarificationReasonCode` | Trigger | Question shape |
|---|---------------------------|---------|-----------------|
| 1 | `LowConfidence` | Classification confidence `< WorkflowFamilyFloor` | Single-select over the four workflow families |
| 2 | `MissingRequiredInput` | Top process has required parameters with no inferable default and none were supplied | Free-text per gap, one finding per parameter |
| 3 | `AmbiguousDataset` | Two or more dataset candidates at `≥ HighConfidenceFloor` within `MaterialSpread` | Single-select over the tied candidates |
| 4 | `AmbiguousProcess` | Two or more process candidates at `≥ HighConfidenceFloor` within `MaterialSpread` | Single-select over the tied candidates |
| 5 | `DestructiveAction` | Top process candidate is flagged destructive by `ProcessDestructiveClassifier` | Confirmation |
| 6 | `PublishAction` | Classified as `PublishData` | Single-select over publish target kinds |
| 7 | `PolicyBoundary` | Classified as `BuildApp` or `AutomateDeploy` | Confirmation (proceed with envelope-only stub) |

Each finding carries a stable `QuestionId` (e.g. `workflow_family`,
`param.<name>`, `dataset.selection`, `process.selection`,
`destructive.confirm`, `publish.target`, `workflow_family.blocked`) that
the caller echoes back in `honua_clarify_intent`.

### Answer application

`ClarificationAnswerResolver` parses the `response.answers` map into an
`AppliedClarificationAnswers` record and the service folds every
recognised answer into the next pipeline pass so a clarification turn
reshapes the result, not just the acknowledged-question set:

| QuestionId | Effect on the follow-up pass |
|------------|------------------------------|
| `workflow_family` | Overrides the classifier. Confidence reports as `1.0` with evidence `clarification`. Unknown values raise `invalid_argument`. |
| `dataset.selection` | Reorders the post-authorization dataset ranking so the pinned id is first. Unknown ids raise `invalid_argument`. |
| `process.selection` | Same pin semantics as `dataset.selection`, applied to the process ranking. |
| `publish.target` | Flows into the drafted `PublishIntent.TargetKind`. Unknown values raise `invalid_argument`. |
| `param.<name>` | Skips the matching `MissingRequiredInput` clarification and records `param.<name>=<value>` on `provenance.assumptions`. |
| `destructive.confirm`, `workflow_family.blocked` | Confirmation-only; any non-blank value counts as acknowledgement. |

Unknown question ids are tolerated (forward compatibility) and simply
left out of `provenance.clarificationsAnswered`.

`GroundingService.GroundAsync` also enforces intent-id parity before
applying any answers: when a `ClarificationResponse` is supplied, the
request's `IntentId` and the response's `IntentId` must both be
non-empty and identical. Missing or mismatched ids fail with
`invalid_argument` so answers cannot be silently rebound to a
different intent. The MCP tool mapper preserves parity by copying
`honua_clarify_intent.intentId` into both fields; direct callers of
`IGroundingService` are held to the same contract.

## Configuration

Options live under the `Operator:Grounding` configuration section and are
loaded into `GroundingOptions`. Defaults are the AI operator technical
plan's tuned values; overrides are allowed so the honua-server-734 eval
harness can re-tune from data without a contract change.

```jsonc
{
  "Operator": {
    "Grounding": {
      "WorkflowFamilyFloor": 0.60,    // below this → LowConfidence clarification
      "HighConfidenceFloor": 0.70,    // at/above → eligible for ambiguity tie
      "MediumConfidenceFloor": 0.40,  // band boundary; below → Low
      "MaterialSpread": 0.05,         // max score gap between tied candidates
      "MaxCandidatesPerKind": 5       // shortlist cap per candidate kind
    }
  }
}
```

## Deterministic engine

`DeterministicGroundingEngine` ships as the default `IGroundingEngine` and
is the engine the conformance harness pins against.

- **Tokenizer**: `GroundingTokenizer` lowercases, strips punctuation, and
  removes a small stopword list.
- **Classifier**: weighted lemma matches against a per-family verb bag;
  honours `WorkflowFamilyHint` at confidence `1.0` with a single `hint`
  evidence entry; falls back to `Analyze` with low confidence when nothing
  matches.
- **Process ranker**: weighted bag-of-lemma over `{ title: 0.55,
  category: 0.20, description: 0.15, parameter: 0.10 }`.
- **Layer / service rankers**: name + description overlap.

Every candidate carries an `Evidence` list (e.g. `title:buffer`,
`category:analysis`) for explainability. Output is deterministic and pure
given `(request, catalog snapshot)`.

## Pluggable engine extension point

`IGroundingEngine` is the only seam between the ranker/classifier and the
rest of the pipeline. A model-backed engine (e.g. an embeddings reranker)
replaces the default registration:

```csharp
services.AddGroundingFeature();
services.Replace(ServiceDescriptor.Singleton<IGroundingEngine, MyEmbeddingsEngine>());
```

The engine is responsible only for scores and classification — the service
handles authorization filtering, provenance wiring, draft-intent shaping,
and clarification emission. `Name` flows into telemetry and into the
`engine` field of every `GroundingResult`, so conformance fixtures can pin
the engine they were captured against.

## Tool surface

The MCP tools `honua_ground_candidates` and `honua_clarify_intent` are thin
delegations over `IGroundingService.GroundAsync`. Tool payload shapes live
in [MCP_SERVER.md](MCP_SERVER.md#payload-notes); the domain types they map
to live in `Honua.Core.Features.Grounding.Domain`.

The `GroundingToolMapper` is the only place that converts between wire
payloads and domain records — all tool integration tests drive through it.

## Conformance fixtures

`tests/fixtures/grounding/grounding-fixtures.json` pins load-bearing slices
of the default engine's canonical output against the built-in process
catalog. The fixtures are consumed by:

- `Honua.Server.Tests/Features/Grounding/GroundingFixtureReplayTests.cs`
  — an xUnit `Theory` that replays every scenario through the real
  `GroundingService` + `DeterministicGroundingEngine` +
  `BuiltInProcessCatalog` stack on every CI run.
- The honua-server-734 eval harness for cross-engine conformance scoring
  once grounding scenarios are added to `tests/Eval/scenarios/` — the
  harness is landed (`tests/Honua.Server.Tests/Features/Eval/`), and its
  current scenarios target analyst and publishing workflows.

**Edit rule**: a change to `DeterministicGroundingEngine` or
`BuiltInProcessCatalog` should trip the replay. Update the fixtures and
the engine/catalog in lock-step; do not overspecify scenarios with exact
process IDs that a catalog addition could reshuffle.

## Related

- [MCP_SERVER.md](MCP_SERVER.md) — operator MCP surface (tool definitions,
  payload shapes, authorization story)
- [ADR-0027](../contributor/adr/0027-deterministic-intent-clarification-workflow.md) — canonical workflow
  taxonomy and material-ambiguity contract
- `src/Honua.Server/Features/Grounding/` — service, engine, drafter,
  evaluator, and MCP mappers
- `src/Honua.Core/Features/Grounding/` — domain types and abstractions
