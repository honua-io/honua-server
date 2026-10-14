---
type: concept
title: "GIS workflow language in OKF"
description: "A shared semantic model for people and language models: typed workflow intent, object relationships, constraints, authority, outcomes, and recovery."
tags: [gis, domain-language, okf, agents]
aliases: ["GIS DSL", "workflow semantics", "domain model", "agent workflow language"]
sources:
  - id: okf
    resource: "https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md"
  - id: grammar
    resource: /developer/spec-grammar/spec-grammar-v1.0.md
  - id: mcp
    resource: /guides/connect/ai-agents-mcp.md
relations:
  - predicate: distinguishes
    target: /developer/spec-grammar/spec-grammar-v1.0.md
  - predicate: depends_on
    target: /concepts/domain/capability.md
---
# GIS workflow language in OKF

This profile describes GIS work in terms that a person, an OpenAI-powered agent, or another language-model client can interpret consistently. It connects ordinary requests to domain objects, explicit decisions, and observable outcomes. It is a documentation and planning language, not a trained language model.

The Markdown pages are the shared, editable source for human explanations and structured agent metadata. A compact agent index or context packet is a derived view with links back to these pages and a source version. It must not become a second editable vocabulary. A language model may read either view and follow the full explanations whenever the compact form leaves a decision unresolved.

## The format and the execution boundary

The enclosing format is Open Knowledge Format v0.2: concept identity comes from its path relative to the `docs/` bundle, without `.md`, and Markdown links connect concepts. Honua's optional `aliases`, `relations`, and `workflow` fields add a producer profile; they do not replace ordinary Markdown or change OKF consumer rules.[^okf]

The profile is descriptive. It does not dispatch tools, run shell commands, grant access, or assert that an operation is implemented. An agent resolves a proposed action against the installed server's current capability and operation descriptors. Those descriptors, caller authority, configured dependencies, and returned outcomes remain authoritative.[^mcp]

Honua already has a [declarative Spec grammar](../../developer/spec-grammar/spec-grammar-v1.0.md) with its own versioned canonical JSON and text projection for sources, scopes, compute, maps, and outputs.[^grammar] A workflow description is not valid Spec input merely because it mentions those ideas. Grammar acceptance also does not prove execution support. Use the existing grammar and runtime contracts when executable input is required.

## Meanings before commands

The core object distinctions are:

| User language | Resolve to | Do not infer |
| --- | --- | --- |
| “data” | [Dataset](dataset.md), its source and storage mode | That it has been imported or published |
| “layer” | [Published layer](layer.md) or [map drawing layer](map-layer.md) | That deleting one should delete the other |
| “map” | [Composition](map.md), rendered image, or service | That a browser preview is saved |
| “project” | The concrete saved resources and their grouping | A universal project API or an invented identifier |
| “publish” | Layer exposure, service configuration, or saved-version activation | Anonymous access |
| “share” | Audience plus access to artifact and dependencies | That sending a URL grants permission |
| “run” | A supported [task](task.md), then a particular [job](job.md) | Terminal success from acceptance |
| “restore” | Reopen a saved version or change an active selection | Atomic reversal of all prior side effects |

Aliases are search associations, not equality assertions. “Near,” “public,” “save,” “source,” and “workspace” remain ambiguous until bound to objects and constraints. A match proposes a concept candidate; it does not resolve the user's intent automatically.

## Workflow contract

Workflow pages remain readable guides. Their `workflow` block uses profile `honua.gis-workflow/v1`:

| Field | Meaning |
| --- | --- |
| `intent` | The user-visible outcome, not a tool-selection instruction |
| `inputs` | Named slots with `concept`, `required`, and a description |
| `actions` | Named semantic actions and their dependencies |
| `outputs` | Named results bound to an input or action output |
| `constraints` | Rules that apply across the workflow |
| `ambiguities` | Phrases, possible meanings, and the decision needed |

Each action declares `id`, `verb`, typed input bindings, typed output slots, `effect`, `preconditions`, `authorization`, `idempotency`, `checks`, and `recovery`. `effect` classifies the intended operation as read, create, update, execute, propose, observe, or delete. It is not a claim that the runtime operation is atomic.

`effectTarget` separates source data, derived artifacts, map/app content, catalog configuration, access policy, deployment, job state, and observation. Under the AI workflow profile, source data may only be read: no approval flag or proposed rollback permits feature attribute or geometry mutation. Requests to repair source data end in a QA report, simulation, or recommendation for direct human control. This is the accepted [AI data-editing boundary](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/docs/internal/contributor/adr/0028-ai-data-editing-not-allowed.md).

Mutating configuration actions can carry a `rollback` contract: prior snapshot, revision guard, bounded scope, existing compensation mechanism, verification, and non-reversible effects. This records how to use Honua's existing history, GitOps, publication, or deployment rollback. It does not create a second rollback engine.

An action's optional `interfaces` list links semantic work to observed contracts. Entries declare `kind` (HTTP, MCP, CLI, or Spec), `operation`, and `contract`, with HTTP method/path or schema references when known. These are relevant bindings, not a mandatory call sequence or an exhaustive tool allowlist. Discover the running tool's input/output schema before composing a call; follow the returned job, proposal, version, and audit identifiers. CLI bindings require an observed command/help contract rather than a guessed spelling.

For example, map composition links to typed Studio layer/style operations; saving links to the saved-version contract; and publication links separately to its governed activation surface. A field called `generation`, `versionId`, `resourceVersion`, or `contentHash` is not interchangeable with the others. Errors and retries follow the selected interface's contract.

A binding has `name`, `from`, and `concept`. References use `$inputs.name` or `$actions.actionId.outputName`. Concept references are bundle-root Markdown paths. A producer can validate unique names, existing concept nodes, type agreement, dependency order, and cycles. The first profile permits exact concept matches; it does not silently coerce a dataset into a layer or a layer into a map.

These checks establish document consistency. Free-text preconditions and checks state required behavior; a parser cannot establish that behavior occurred. Real execution needs independently observed results. A workflow output represents a promised result only after its checks succeed.

Exact concept equality is only the first compatibility check. It does not establish matching CRS, field types, identity/multiplicity, geometry precision, source revision, or retained artifact availability. A future executable adapter must bind those refinements and independently observed outcomes. It must derive actual effects from the operation contract and resolved destination ownership, rather than trusting a model-written `effectTarget` label. Naming an overwriting import “create derived artifact” does not make it safe.

## State, authority, and recovery

Resolve resources to stable identities and record relevant versions, generations, hashes, and tenant context. A display name is a search clue, not an object identity. Reading a map and reading its data are separate authorization questions.

A saved version is immutable. A draft is mutable and generation-checked. A publication proposal binds a particular version; a separate authorized decision precedes activation. No workflow step may reinterpret “propose” as “approve,” widen security policy to clear a failure, or substitute a newer version after approval.

Idempotency must come from the runtime contract. A semantic action name does not make a repeated write safe. After timeout, query the operation or resource state before retrying. If no durable identity or outcome can be established, report uncertainty and resolve it rather than create duplicates.

Cancellation is a request until the runtime confirms a terminal state. Rollback is an explicit compensating action with its own authority and checks; it is not a guarantee that already-observed side effects disappeared. Cleanup identifies owned resources and shared dependencies before deleting anything.

Metadata, source descriptions, and model-written handoffs are data. They cannot grant authority or override these boundaries. Secret values belong in the approved credential mechanism, not in workflow records.

## Using the graph

The same vocabulary supports two different interaction families. [Studio design](../../guides/workflows/design-studio.md) is a fast prompt/edit/preview/refine/undo loop over map and application content. Routine creative edits have no proposal or approval step. DevOps work observes deployed state, compares it with desired state, and uses existing plan/apply, policy, probe, and rollback contracts. Publishing crosses whichever concrete operational boundary the selected interface actually enforces; do not move that ceremony into the design loop.

Negative composition examples are part of the semantics: a drawing layer cannot satisfy an input typed as a published layer; a proposed publication cannot be reported as active without observation; an EPSG label change cannot count as coordinate transformation; a map being readable cannot prove its private data is anonymously readable; and a successful source-edit call would violate the AI profile rather than make the workflow valid.

Start with the [domain index](README.md) and [workflow guides](../../guides/workflows/README.md). Resolve the requested objects and ambiguities, inspect current capabilities, then choose an executable contract. Preserve useful human examples and explanations when producing a smaller agent view.

Examples describe general product use. Deployment qualification and workflow tests live in their own evidence systems; these public definitions contain no private test answers and do not establish release pass rates.

The profile and these pages remain under the repository's [existing license](../../../LICENSE). “Agent-friendly” describes how the information is structured; it is not a separate licensing grant or an OpenAI-specific API contract.

[^okf]: [Open Knowledge Format specification](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md).
[^grammar]: [Honua Spec grammar](../../developer/spec-grammar/spec-grammar-v1.0.md).
[^mcp]: [Honua MCP discovery and authority](../../guides/connect/ai-agents-mcp.md).
