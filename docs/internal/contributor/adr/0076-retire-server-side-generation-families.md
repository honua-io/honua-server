# ADR-0076: Retire the Server-Side Generation Families; Re-found Package Creation Deterministically

## Status

Accepted (2026-08-16). Records decision **D8** (recorded as "D5"; renumbered
2026-08-17), extracted from the Studio/MCP convergence epic
([#3220](https://github.com/honua-io/honua-server/issues/3220)) and tracked as
[#3255](https://github.com/honua-io/honua-server/issues/3255). Pairs with D7
(model routing default, #3254;
[ADR-0077](0077-studio-model-routing-default.md)), which is related but
independent. See honua-release#80 for the canonical decision registry.

Amends the operator-contract posture set by [ADR-0026](0026-ai-first-operator-contract.md)
and the clarification workflow in [ADR-0027](0027-deterministic-intent-clarification-workflow.md):
both stand, but the inference that serves them moves to the client.

## Context

`src/Honua.Ai/Features/` carries **eight** generation families — `AnalysisGeneration`,
`AppGeneration`, `DashboardGeneration`, `FormGeneration`, `MapGeneration`,
`QueryGeneration`, `ReportGeneration`, `WorkflowGeneration` — plus two in-server
model provider adapters, `Providers/AzureOpenAi` and `Providers/Bedrock`. (#3255's
body says seven; `WorkflowGeneration` is uncounted. `src/Honua.Scene/Generation`
and `src/Honua.Core/Features/WorkflowPackages/Generation` are unrelated despite
the name and are **not** in scope.)

Each family is a server-side, prompt-driven, whole-artifact generator: a natural
language request goes in, a complete package comes out. That shape is now the
odd one out, for four reasons.

### 1. Whole-artifact generation is neither reversible nor reproducible

A monolithic generation is one opaque, nondeterministic call. It cannot be
partially undone, cannot be replayed, and cannot be explained after the fact.
The element-level authoring model that replaced it — a stream of small,
individually validatable composition ops — produces an **op log**, which is both
the undo stack and the reproduction. This is the same posture the standard took
in [ADR-0030](https://github.com/honua-io/geospatial-mcp/blob/trunk/docs/adr/0030-declarative-interactions-and-layout.md),
whose stated failure mode is composition "generated whole by a vendor surface."

The streaming canvas is not speculative: honua-studio #24, #27, #28, and #29 have
all landed. D5 removes what they replaced.

### 2. "The server decides to call a model" is currently a configuration accident

`FeatureRegistrationExtensions.cs:213` and `:218` register `IMapGenerationService`
and `IAppGenerationService` **unconditionally** — no options gate, no feature
flag. The `Enabled` check lives inside the implementation
(`MapGenerationService.cs:47-50`), not in the container, so the service is always
resolvable and always builds real chat-completion requests. It fails at call time
for want of a key rather than being absent.

**State the resulting property precisely, because the obvious phrasing is wrong.**
"The server makes no outbound LLM calls" would be false even after this work, and
it is exactly the kind of sentence that ends up in a compliance attestation. The
Studio AI proxy (`src/Honua.Ai/Features/StudioAiProxy/`, honua-server#3000) is a
deliberate, shipped, provider-agnostic **pass-through** at
`POST /api/v1/studio/ai/chat`. It exists so a browser that cannot hold model keys
can still do client-side inference, which is what makes the rest of this decision
workable. It stays.

The defensible claim, and the one this ADR makes, is narrower:

> **The server performs no model inference of its own as part of executing a
> capability.** It may forward a client's inference request through an explicitly
> configured proxy.

Getting there requires one thing beyond the eight families:
`OpenAiNlQueryPlanProvider` (`src/Honua.Ai/Features/NlQuery/`) made real
`/chat/completions` calls, was registered unconditionally, and was **not** in the
original D5 scope. It is removed here. Doing so costs nothing: `INlQueryOrchestrator`
has zero consumers outside its own feature and tests — no endpoint, no MCP tool, no
route — so the whole NlQuery feature is registered but unreachable. Removing the
one provider that opened a socket leaves the `INlQueryPlanProvider` seam and the
deterministic fixture provider in place.

### 3. The unconstrained generation surface is a live security exposure

The report and dashboard generation prompts mandate raw, unconstrained Vega-Lite
(`ReportGenerationPrompt.cs:29`, `DashboardGenerationPrompt.cs:30`). Two console
surfaces — `StudioReportBuilderPage` and `StudioDashboardBuilderPage` — render
that model-authored spec directly. Because the vendored Vega bundles are UMD and
therefore attach to `window`, the gadget behind GHSA-7f2v-3qq3-vvjf is reachable
on exactly those two surfaces and on no other chart surface in the product
(honua-console#337).

Narrowed, validated authoring ops remove the untrusted-input condition rather
than mitigating it. See [ADR-0075](0075-ui5-web-components-application-chrome.md)
for the chart-grammar half of the same argument.

### 4. Our own spec already says inference belongs to the client — and our implementation ignores its own schema

Two of the eight families back tools that also appear in geospatial-mcp:
`honua_create_map_package` and `honua_create_app_package` are bound in
`spec/schemas/index.json` to Build App v1's `create_map_package` /
`create_app_package`.

**This is not a conformance constraint.** We author geospatial-mcp, it has no
external adopters, and the spec follows the implementation rather than binding
it. Where the two disagree, the spec text is what changes. Conformance checks are
useful as drift detection between our own artifacts, not as a gate on product
decisions — and nothing in this ADR is motivated by preserving a conformance
level.

What the spec *is* useful for here is as a record of a design position we already
took and then failed to implement. `spec/planning.md:755-759`:

> Client-agent phrasing of `ClarificationRequest.prompt`, LLM selection,
> prompt-engineering patterns, and natural-language rendering of options are
> owned by the client agent. MCP defines the typed protocol only.

We wrote that, and then built the opposite. D5 closes the gap.

The concrete implementation defects are worth fixing on their own merits,
independent of any spec:

- `CreateMapPackageTool.cs:85-89` **throws when `prompt` is missing** — a
  Honua-local field neither schema defines. A caller who supplies fully
  structured input and no prose gets a hard validation error, which is a bad API
  regardless of what any document says.
- **`sourceBindings` is not accepted at all**, despite being the primary
  composition input in both our schema and any sensible design.
- **`initialView` is published in the schema and then dropped by the parser** —
  the tool advertises an input it silently ignores.

A deterministic entry point fixes all three, because honoring structured input is
the entire job once generation is gone.

## Decision

**Retire the eight server-side generation families and the two in-server model
provider adapters. Re-found `create_map_package` and `create_app_package` as
deterministic draft-creation entry points. All model inference moves to the
client.**

### The deterministic entry points

`honua_create_map_package` and `honua_create_app_package` remain registered,
advertised, and functional. They stop calling a model and instead:

- accept the **standard's** properties — including `sourceBindings` and
  `initialView`, which are honored rather than dropped;
- **drop the `prompt` requirement**, which the standard does not define;
- create and persist a draft package deterministically from that structured
  input;
- return a real package carrying a stable `map_…` / `app_…` identifier
  addressable at its `honua://map-packages/{id}` / `honua://app-packages/{id}`
  URI.

Composition beyond that initial draft happens through the element-level ops the
client's model drives.

### The guard that matters more than the deletion

**A tool that returns `capability_unavailable` is not an acceptable end state.**

This is the failure mode to design against, because it is the *cheap* version of
this change and nothing we run today would catch it. Both tools resolve their
service through a nullable `httpContext.RequestServices.GetService<T>()`
(`CreateMapPackageTool.cs:96`) rather than constructor injection. Deleting the two
DI registrations alone therefore **compiles cleanly and keeps the tools listed in
`tools/list`**, while making them permanently return an unavailable stub. A
half-done deletion looks identical to a finished one from outside.

Tool-roster checks cannot catch it either — they compare names, not behaviour. So
the guard has to be behavioural and ours: **an integration test asserting each
tool returns a real package with a stable `map_…` / `app_…` identifier**, not
merely that the tool is advertised.

### Extract before deleting

Every prompt, schema, and validation gate in the eight families was read before
any of them was removed, on the expectation that they encoded hard-won
cartographic defaults — palettes, classification methods, class counts, ramps —
that existed nowhere else.

**That expectation was mostly wrong, and saying so is part of the record.** Real
cartography lives outside the delete boundary and is untouched: `ColorPalettes.cs`,
`StyleSuggestionService.cs`, and the choropleth defaults in
`AuthoringWorkflowNodeProvider.cs`. Across all eight families the only colour
literal was a single example hex in one prompt. They were vocabulary-grounding
and structural-validation layers, not cartographic ones — which is why the
deletion is smaller in substance than in line count.

What *is* unique, and is recorded in
[generation-families-retained-knowledge.md](../generation-families-retained-knowledge.md)
before removal: a measured A/B result showing a richer prompt prefill **regressed**
quality on a local 7B model (41/43 for the lean baseline), the generation-vs-publish
leniency contract, the query filter validation rules including a nesting cap of 4,
the map extent/CRS conventions the deterministic entry point now has to honor, and
three safety postures that must not regress.

That document also records six defects found during extraction — including a rule
both report and dashboard prompts state and **no validator enforces** — because
deleting the code would otherwise delete the evidence.

## Consequences

### Preserved

- Both tools stay registered, advertised, and functional — they change
  implementation, not existence.
- BYOM. Inference moves to the client; the capability is not removed.
- The clarification envelope (ADR-0027, ADR-0061). It is a typed protocol, and
  the client fills it.

### Improved

- "The server performs no model inference of its own as part of executing a
  capability" becomes an architectural fact rather than a missing-key accident —
  a claim `honua-compliance` can attest, stated in the form that is actually
  true. The Studio AI proxy remains a deliberate BYOM pass-through and is
  excluded from that claim by construction, not by oversight.
- The tool API stops lying: the undefined `prompt` requirement goes away, and
  `sourceBindings` / `initialView` start being honored instead of rejected and
  silently dropped.
- GHSA-7f2v-3qq3-vvjf stops being reachable on the report and dashboard surfaces,
  because nothing renders model-authored raw Vega-Lite any more.
- No server-side model key custody, and no server-side token spend.
- The provider asymmetry disappears. Only Dashboard and Report implemented
  Bedrock and Azure OpenAI paths, so a Bedrock-only deployment could generate
  dashboards and reports but not maps, forms, apps, analyses, or queries. Moving
  inference to the client removes the whole class of problem.

### Costs and risks

- **Chattiness.** Many small ops replace one call. Anticipated upstream by
  geospatial-mcp #72 (batched composition operations and the optimistic-apply
  contract); the `draftId` + `generation` optimistic-concurrency spelling already
  exists in the composition schemas.
- **Client model capability.** A weaker client model may compose worse than a
  purpose-built server generator did. Mitigated by the extracted defaults above
  and by the capability registry (ADR-0058), not by hope.
- **Four call sites, not two.** `StudioPackageEndpoints.cs:339` and `:394` inject
  `IAppGenerationService` / `IMapGenerationService` into REST endpoints outside
  the MCP surface entirely.
- **Scope by feature family, never by assembly.** `CapabilityManifestEmitter`
  — the generator that emits our published capability manifest — lives at
  `src/Honua.Ai/Features/Protocols/Mcp/Mcp/Discovery/CapabilityManifestEmitter.cs`,
  inside the assembly being trimmed. Retiring `Honua.Ai` wholesale would take the
  manifest generator and the alignment tests with it, so the manifest would stop
  reflecting what the server actually offers even though the tools survived. The
  `CapabilityRegistry` roster itself is safe in `Honua.Core`.
- **Spec text needs a follow-up edit, as housekeeping.** `index.json:50` and
  `:93` say the reference "routes through the canonical IMapGenerationService /
  IAppGenerationService pipeline", which this makes false. We own that text, so
  it is a cheap correction rather than a negotiation — but it should be done, or
  the spec starts describing a product that no longer exists. Same stale copy is
  vendored at `tests/dotnet/Honua.Ai.Tests/ConformanceSchemas/geospatial-mcp/index.json`.
  While there: the fixtures for both tools still carry `"deferred": true` and a
  note that the reference "does not yet ship `create_map_package` as a discrete
  tool", contradicting `index.json`'s `"implemented"`. Both statements predate
  this work and one of them was always wrong.

### Known pre-existing skew, surfaced but not caused here

The vendored `index.json` copy is dated 2026-07-06 while the source is
2026-08-11, and `CapabilityManifestEmitter` pins `SpecDate = "2026-04-19"` — three
different vintages of the same vocabulary in one repo. That is a drift-detection
problem between our own artifacts and is worth cleaning up on its own schedule.
Recorded here only so it is not discovered mid-deletion and mistaken for a
regression this work caused.

## Non-goals

- Removing BYOM. Inference moves to the client, and the Studio AI proxy stays so
  a browser can reach a model without holding a key.
- Removing NL-to-spatial-SQL. The pipeline is
  `NL -> FilterPlan -> FilterPlanCompiler -> SQL`, and both load-bearing pieces
  sit outside this deletion: `FilterPlan` in `Honua.Core/Features/NlQuery/Domain`
  and `FilterPlanCompiler` in `Honua.Geometry/Features/NlQuery/Services`. Only
  the step that decides *who runs the model* changes. A client-planned
  `FilterPlan` is also strictly better than a generated SQL string, because it is
  inspectable and dry-runnable before it reaches the database.
- D4 model routing (#3254).
- Touching `src/Honua.Scene/Generation` or
  `src/Honua.Core/Features/WorkflowPackages/Generation`, which are not AI
  generation despite the name.

## Amendment (2026-08-16): where the persisted draft lives (#3262)

The Decision above says the entry points "create **and persist** a draft
package" and return an identifier "addressable at its
`honua://map-packages/{id}` / `honua://app-packages/{id}` URI". The first
implementation created without persisting. `MapPackageResource` and
`AppPackageResource` reverse-look-up *deployments*, and a freshly created draft
has none, so the tool returned a well-formed URI that could never resolve — a
softer version of the `capability_unavailable` dead end this ADR is built around
guarding against, and one invisible from the tool's own response.

### What was chosen

**A dedicated draft store, `IPackageDraftStore`, keyed by the `map_…` / `app_…`
identifier the factories mint.** Both tools write the draft before returning;
both package resources read it when the deployment reverse-lookup finds nothing.
The deployment lookup stays first, so a promoted package keeps reporting its
deployment edges rather than the older draft it grew from. The response gains one
field, `packageStatus` (`draft` | `published`), so a caller can tell which
lifecycle state it is looking at instead of inferring it from an empty
`deploymentResourceUris`.

### Why not `IStudioPackageStore`

`IStudioPackageStore` already exists and already persists drafts, which makes it
the obvious candidate. It was rejected because it is `Guid`-keyed over a
`StudioPackageEnvelope`: routing map/app drafts through it puts two identifier
schemes on one object, and the identifier the tool returns — the one the URI is
built from — would not be the store's key. Every read would then be a
scan-and-reconcile against a `packageKey` rather than a lookup, and the
`create → resolve` promise this amendment exists to keep would rest on that
reconciliation staying correct. Studio's Guid-keyed envelope drafts and the MCP
package drafts remain separate surfaces with separate lifecycles; nothing here
merges them.

### Retention, stated rather than implied

The default implementation, `InMemoryPackageDraftStore`, is in-process and
age-bounded (24h TTL, 500 drafts per kind, oldest evicted first). A draft is
pre-publish scratch: it becomes durable when it is promoted to a deployment,
which is the surface the resources already read. The consequences are real and
are recorded here rather than discovered later — on a multi-replica deployment a
draft resolves only on the replica that created it, and it does not survive a
restart. Both present as an ordinary not-found, which is also what an expired
draft gives. A durable, shared backing is a separate decision with its own
retention and authorization questions and is not made here.

Draft reads are authorized exactly as deployment-backed package reads are
(`OperatorResourceType.Package` / `OperatorOperation.Read`). The draft store
records no owner, so it does not narrow that further; per-draft ownership would
be a change to the package authorization model, not to this store.

### The guard

The behavioural guard this ADR asks for is extended rather than duplicated:
`McpPackageDraftIntegrationTests` now asserts **create-then-resolve** through the
composed host — `tools/call` to mint the identifier, then `resources/read` on the
exact `resourceUri` the tool returned — for both map and app packages. A tool
that creates without persisting passes the create half and fails the resolve
half, which is precisely the defect this amendment closes.
