# ADR-0076: Retire the Server-Side Generation Families; Re-found Package Creation Deterministically

## Status

Proposed (2026-08-16). Records decision **D5**, extracted from the Studio/MCP
convergence epic ([#3220](https://github.com/honua-io/honua-server/issues/3220))
and tracked as [#3255](https://github.com/honua-io/honua-server/issues/3255).
Pairs with D4 (model routing default, #3254), which is related but independent.

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

### 2. "The server makes no outbound LLM calls" is currently a configuration accident

`FeatureRegistrationExtensions.cs:213` and `:218` register `IMapGenerationService`
and `IAppGenerationService` **unconditionally** — no options gate, no feature
flag. The `Enabled` check lives inside the implementation
(`MapGenerationService.cs:47-50`), not in the container, so the service is always
resolvable and always builds real chat-completion requests. It fails at call time
for want of a key rather than being absent.

That is not a property anyone can attest to. Moving inference to the client makes
zero-outbound-LLM **structural**.

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

### 4. The standard never asked for server-side generation — and requiring it made us non-conformant

Two of the eight families back **standard** tools: `honua_create_map_package` and
`honua_create_app_package` are bound in geospatial-mcp's `spec/schemas/index.json`
to Build App v1's `create_map_package` / `create_app_package`, and the reference
manifest declares FULL conformance on `base`. The obvious fear is that retiring
them drops Honua below the level it advertises as the reference implementation.

Verification says the opposite, on both halves.

**The standard assigns inference to the client.** `spec/planning.md:755-759`:

> Client-agent phrasing of `ClarificationRequest.prompt`, LLM selection,
> prompt-engineering patterns, and natural-language rendering of options are
> owned by the client agent. MCP defines the typed protocol only.

Corroborated by `spec/taxonomy.md:562-564` ("MCP tools delegate to deterministic
services; they do not reimplement service logic") and by `spec/conformance.md:438-444`,
which declines to prescribe a planner algorithm at all — *how* a package is
produced is unscoreable by construction.

**Neither tool schema has a natural-language input, or any required field.**
`create_map_package.schema.json` accepts `templateId`, `sourceBindings`,
`styleId`, `themeId`, `initialView`; `create_app_package.schema.json` accepts
`templateId`, `targetSdk`, `mapPackageId`, `boundArtifactIds`, `runtimeConfig`.
Every property is a structured identifier or geometry.

**Honua's implementation is currently the divergent one.** `CreateMapPackageTool.cs:85-89`
*throws* when `prompt` is missing — a Honua-local field riding on the schema's
`additionalProperties: true` — so a standard-conformant client sending the
standard's own fixture payload gets a hard validation error today. Meanwhile
`sourceBindings`, the standard's primary composition input, is not accepted at
all, and `initialView` is published in Honua's schema then dropped by the parser.

Re-founding these tools deterministically is therefore not a conformance cost to
be absorbed. It is the change that makes Honua conformant.

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
this change and it is invisible to every gate we run. Both tools resolve their
service through a nullable `httpContext.RequestServices.GetService<T>()`
(`CreateMapPackageTool.cs:96`) rather than constructor injection. Deleting the two
DI registrations alone therefore compiles cleanly, keeps `tools/list` intact, and
keeps `check_manifest.py` reporting FULL — while making both tools permanently
return an unavailable stub.

The manifest checker cannot catch this. It computes
`implemented_std_tools − advertised_std_tools`, a set difference over names
(`check_manifest.py:155`); it never opens a tool schema, never calls a server,
and never inspects behaviour. `manifest.schema.json` has no field in which
behaviour could even be declared.

What that state would fail is the downstream rubric's result-projection axis
(`spec/conformance.md:274`), for which no harness exists in either repo. So the
guard has to be ours: **an integration test asserting each tool returns a
package with a stable identifier**, not merely that the tool is listed.

### Extract before deleting

The generation prompts encode cartographic and structural defaults — symbology,
classification methods, class counts, palettes, layout conventions, validation
bounds — that exist nowhere else in the codebase. Deleting them without capturing
those defaults would regress output quality in a way that gets misattributed to
the client model rather than to the deleted defaults.

**The extracted defaults are recorded before any family is removed**, and become
deterministic templates and op presets. This ordering is not optional.

## Consequences

### Preserved

- FULL conformance on `base` holds, verified empirically: removing the two tools
  from a scratch manifest produces `FAIL: standard tool 'create_map_package' …
  is not advertised`; keeping them advertised produces
  `FULL [reference implementation] (31 standard tools …)`. There is **zero
  slack** — the index has exactly 31 `implemented` base tools and the manifest
  advertises exactly 31, so any drop is an immediate strict failure.
- BYOM. Inference moves to the client; the capability is not removed.
- The clarification envelope (ADR-0027, ADR-0061). It is a typed protocol, and
  the client fills it.

### Improved

- Zero outbound LLM calls from the server becomes an architectural fact rather
  than a missing-key accident — a claim `honua-compliance` can attest.
- Standard conformance improves in two concrete ways: the undefined `prompt`
  requirement goes away, and `sourceBindings` / `initialView` start being honored.
- GHSA-7f2v-3qq3-vvjf stops being reachable on the report and dashboard surfaces,
  because nothing renders model-authored raw Vega-Lite any more.
- No server-side model key custody, and no server-side token spend.

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
  — the generator that produces the conformance manifest — lives at
  `src/Honua.Ai/Features/Protocols/Mcp/Mcp/Discovery/CapabilityManifestEmitter.cs`,
  inside the assembly being trimmed. Retiring `Honua.Ai` wholesale would take the
  manifest generator and the conformance alignment tests with it, and the FULL
  claim would lose its mechanical guard even though the tools survived. The
  `CapabilityRegistry` roster itself is safe in `Honua.Core`.
- **A cross-repo documentation change is required.** `index.json:50` and `:93`
  state that the reference "routes through the canonical IMapGenerationService /
  IAppGenerationService pipeline." That is published text in a public standards
  repo which this decision makes false. The same stale text is vendored at
  `tests/dotnet/Honua.Ai.Tests/ConformanceSchemas/geospatial-mcp/index.json`.
  While there, the standard's own fixtures for both tools still carry
  `"deferred": true` with a note that the reference "does not yet ship
  `create_map_package` as a discrete tool", contradicting `index.json`'s
  `"implemented"` — worth correcting in the same pass.

### Known pre-existing skew, surfaced but not caused here

Honua's vendored `index.json` copy is dated 2026-07-06 while upstream is
2026-08-11, and `CapabilityManifestEmitter` pins `SpecDate = "2026-04-19"`. The
honua-side conformance test therefore grades against an older vocabulary than
geospatial-mcp CI does, so a green test here is not proof the upstream gate
passes. Unrelated to D5, but it should not be discovered during this work and
mistaken for a regression it caused.

## Non-goals

- Removing BYOM.
- D4 model routing (#3254).
- Touching `src/Honua.Scene/Generation` or
  `src/Honua.Core/Features/WorkflowPackages/Generation`, which are not AI
  generation despite the name.
