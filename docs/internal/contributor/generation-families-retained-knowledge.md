# Retained knowledge from the retired generation families

Companion record to [ADR-0076](adr/0076-retire-server-side-generation-families.md).

Before the eight generation families under `src/Honua.Ai/Features/` were removed,
every prompt, schema, and validation gate in them was read to find defaults that
existed nowhere else. This file records what must survive. It is not a summary of
the deleted code — it is the subset that would otherwise be lost.

## The headline: there was far less cartographic knowledge than assumed

ADR-0076 originally justified an extract-before-delete step on the expectation
that the prompts encoded hard-won cartographic defaults — palettes,
classification methods, class counts, ramps. **They did not.** Real cartography
lives outside the delete boundary and is untouched:

- `src/Honua.Core/Features/Styling/Domain/ColorPalettes.cs`
- `src/Honua.Core/Features/Styling/Services/StyleSuggestionService.cs`
- `src/Honua.Server/Features/WorkflowPackages/AuthoringWorkflowNodeProvider.cs`
  — including the choropleth defaults (`classes = "5"`, `colorRamp = "Blues"`)

The families were vocabulary-grounding and structural-validation layers, not
cartographic ones. Across all eight, the only colour literal was a single example
hex in a prompt (`#2D69A5`).

What *was* unique is narrower and different: a **generation-vs-publish leniency
contract**, a set of **placeholder conventions**, **hand-mirrored copies of
canonical compiler vocabularies**, and one **measured prompt-engineering result**.

## 1. The measured A/B result (WorkflowGeneration)

The single most valuable line in all eight families, because it is empirical and
counter-intuitive:

> An A/B over the 43-case e2e corpus showed that enriching the node prefill with
> per-node Title/Description sentences **regressed** quality on the local 7B CPU
> model — the larger palette pushed slow cases past the provider timeout and
> nudged over-elaboration (under-specified projection nodes missing `srid`). The
> lean baseline measured best at **41/43**.
>
> **Teach node usage via the training corpus, not the runtime prefill.**

This generalizes past workflows: any future client-side prompt or catalog
prefill should assume a richer palette is a cost, not a free improvement, and
should be measured rather than assumed.

## 2. The generation-vs-publish leniency contract

The governing design statement was that generation validates *structure* while
resolution binds at publish. Each family encoded that as an explicit list of
error codes downgraded from blocking to deferred warnings. The lists differ, and
the differences are deliberate:

| Family | Deferred codes |
|---|---|
| Map | `layerNotFound`, `layerNotResolved`, `sourceNotFound`, `sourceNotResolved`, `styleNotFound`, `styleNotResolved`, `popupSourceNotFound`, `labelSourceNotFound`, `basemapNotFound` |
| Form | `serviceNotFound`, `layerNotFound`, `targetFieldNotFound`, `targetFieldNotWritable`, `targetNotResolved`, `targetMissing` |
| Analysis | `UNKNOWN_LAYER`, `LAYER_NOT_FOUND`, `INPUT_NOT_RESOLVED`, `UNKNOWN_DATASET` |
| App | `bindingNotResolved`, `actionPageNotFound` |
| Query | `LAYER_NOT_BOUND` |
| Report, Dashboard | none — structural-only |
| Workflow | none, and a hard gate: never return a graph the server's own validator rejects |

Two rules inside that contract are worth keeping explicitly:

- A **missing locator URL is a warning, not an error** — deliberately, so a
  descriptively-named layer still produces a draft.
- A **present `styleRef` with a blank `styleId` is a blocking error** — a
  half-specified reference is worse than none.

Any deterministic draft-creation path faces the same question (how complete must
a draft be before it is accepted?) and should start from these lists.

## 3. Query filter vocabularies, hand-mirrored from `FilterPlanCompiler`

`QueryGenerationValidationGate` was the densest of the eight and mirrored the
canonical compiler's vocabularies by hand. The vocabularies themselves survive in
the compiler; what would be lost is the **validation layer over them**:

- **Hard nesting cap: `MaxNestingDepth = 4`.**
- `in` requires an array value; `isNull` requires *no* value; every other
  comparison requires a non-null value.
- `dwithin` requires a **positive** distance — missing and `<= 0` are distinct
  failures.
- `after` and `during` require `start`; `before` and `during` require `end`.
- A spatial geometry must be a non-empty GeoJSON object — a null or scalar
  placeholder is rejected, unlike every other placeholder convention.
- Empty `outFields` means *all fields* and is valid; only a blank entry fails.

Two natural-language rewrites that are genuine domain knowledge:

- **"between X and Y" becomes two clauses** (`gte X`, `lte Y`) joined by `and`.
  There is no `between` operator.
- **NL → spatial operator**: "in/overlapping" → `intersects`; "inside" →
  `within`; "contains" → `contains`; "within N units of" → `dwithin` plus
  distance and unit.

## 4. Map extent and CRS conventions

The only real cartographic content in the eight, and directly relevant because
the deterministic `create_map_package` must honor `initialView`, which today is
published in the schema and then dropped by the parser:

- Default CRS for the initial view is **`EPSG:4326`**.
- bbox axis order is **`[minLon, minLat, maxLon, maxLat]`** — lon first.
- `min <= max` is validated on both axes.
- Accepted CRS grammar is exactly three forms: `EPSG:<digits>`,
  `http://www.opengis.net/def/crs/...`, `urn:ogc:def:crs:...`.
- Extent fallback: if the request names a place, pick a reasonable bounding box;
  otherwise use a world or regional extent.

## 5. Safety postures that must not regress

- **App sharing is closed by default** — `visibility = "private"`,
  `embed = false`, `reviewed = false` unless explicitly requested.
- **Forms may never author policy.** The model was forbidden from setting
  `submitPolicy`, `attachmentPolicy`, `privacyPolicy`, or `offlinePolicy`; the
  server applies safe defaults from `FormPackageContracts.cs`. That boundary
  survives only if nothing downstream starts accepting those fields from a
  client.
- **The server owns discriminators.** Format and `schemaVersion` values were
  force-rewritten server-side regardless of what the model emitted, and Query
  force-overwrote the echoed `naturalLanguageQuery` with the caller's exact
  prompt rather than trusting a paraphrase. Client-side inference makes this
  posture *more* important, not less.

## 6. Conditionally-required parameter triples (Analysis)

Algorithm-specific and easy to lose: `eps` + `minPoints` when
`algorithm = dbscan`; `k` when `algorithm = kmeans`; `distance` when
`predicate = dwithin`.

## NL-to-spatial-SQL survives this deletion

Recorded because `QueryGeneration` looks like the NL-to-SQL feature and is not.

The pipeline is `NL -> FilterPlan -> FilterPlanCompiler -> SQL`, and both
load-bearing pieces sit outside the delete boundary:

- `FilterPlan` — `src/Honua.Core/Features/NlQuery/Domain/`
- `FilterPlanCompiler` — `src/Honua.Geometry/Features/NlQuery/Services/`

`QueryGeneration` was one route to a `FilterPlan`. A second, separate feature —
`src/Honua.Ai/Features/NlQuery/` — does the same job behind an
`INlQueryPlanProvider` seam and already shipped two implementations. Only the
step deciding *who runs the model* changes: a client emits a `FilterPlan`, the
server validates and compiles it. That is the better shape, because a plan is
inspectable and dry-runnable before it reaches the database in a way a generated
SQL string never is.

The query rules in §3 above are what a client-side planner or a server-side plan
validator needs in order to keep the behaviour that `QueryGenerationValidationGate`
enforced.

**`NlQuery` is itself currently unreachable.** `INlQueryOrchestrator` has no
consumers outside its own feature and tests — no endpoint, no MCP tool, no
registry route — so the feature is registered by
`FeatureRegistrationExtensions.cs:133` and never invoked. Its
`OpenAiNlQueryPlanProvider` was removed with the generation families (it was the
last path on which the server initiated inference of its own accord); the
deterministic provider and the seam remain.

## Defects found during extraction

Recorded because deleting the code would also delete the evidence.

1. **"Non-chart panels MUST NOT include a `chartSpec`" was prompt-only.** Neither
   `ReportDocumentValidator` nor `DashboardDocumentValidator` enforces it — they
   only check `chartSpecRequired` for chart panels. Deleting the prompts drops
   the rule while leaving the impression it is enforced. If the rule is wanted,
   it must move into the validators.
   **Closed.** The rule was wanted. It landed in both document validators as
   `chartSpecNotAllowed` (#3261) and on the separate publish-time gate,
   `ContentPublicationBodyValidator`, as `publication.panel.chartSpec.notAllowed`
   (#3263). The publish path had the identical blind spot and is the only gate
   left once the prompts are gone.
2. **Six placeholder conventions for one concept.** Form used
   `serviceId "placeholder"` + `layerId 0`; Map used
   `https://placeholder/<service>`; Analysis used the string `"0"`; Query used
   numeric `layerId <= 0`; Report and Dashboard used `contentRef "placeholder"`;
   App used `content:<ref>@v<N>`. A deterministic rewrite should pick one.
3. **Provider reach was uneven.** Only Dashboard and Report implemented Bedrock
   and Azure OpenAI paths. A Bedrock-only deployment could generate dashboards
   and reports but not maps, forms, apps, analyses, or queries. This is one more
   argument for client-side inference: the asymmetry disappears.
4. **`Temperature = 0.0` everywhere except the Anthropic workflow provider**,
   which set none. If determinism was the intent, that was a live inconsistency.
5. **Version pinning was inconsistent.** Map, App, Form, Report, and Dashboard
   pinned and force-normalized a format discriminator; Workflow left
   `graph.schemaVersion` an unconstrained string; Analysis and Query had no
   discriminator at all.
6. **Report and Dashboard overlapped ambiguously.** The Report prompt refused
   only when a request was "not about building a report/dashboard-style
   document", so both families accepted the same prompt and produced different
   panel vocabularies.

## What was deliberately not retained

`AppGeneration` and `ReportGeneration` encoded nothing worth carrying beyond the
items above — App was vocabulary transcription of the console's `studio-app/v1`
contract, and Report was a near-verbatim clone of Dashboard whose only unique
sentence was "a report is a top-to-bottom layout of panels". `FormGeneration`
and `DashboardGeneration` were mostly mirrors of validators in `Honua.Core` that
survive. Naming this explicitly is part of the record: it is why the deletion is
smaller in substance than in line count.
