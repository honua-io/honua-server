# ADR-0075: UI5 Web Components for Application Chrome, Vega-Lite for Charting

## Status

Accepted (2026-08-15).

## Context

`honua-sdk-js` ships roughly 46 custom elements and every one is map-domain:
the map canvas, layer list, legend, feature table, feature editor, chart,
basemap/locate/measure controls, bookmarks, search, print/export. The package
has no UI framework dependency — all of it is hand-rolled.

What does not exist is **application chrome**: shell and page regions, split
panes, tabs, dialogs, drawers, toolbars, menus, toasts, cards, breadcrumbs,
wizards, generic data tables, and — most consequentially — form layout, form
fields, and form validation. The two `honua-action` / `honua-action-panel`
elements echo Calcite's naming, which is evidence the pattern was already being
reached for without a substrate underneath it.

The practical consequence is that Honua can build a *map* but not an
*application*. There is no settings dialog, no permit-intake form, no
split-screen compare, no multi-step flow. Four separate workstreams are blocked
on the same missing piece:

1. **Agent authoring.** The Studio composition document carries `widgets` with
   a free-string `kind` and an opaque `config` that no validator inspects. An
   agent asked to "add an interactive chart bound to parcels" is guessing prop
   shapes into an unchecked blob, and failures are silent until render. A
   machine-readable component registry is the fix, but a registry needs a
   component *set* to register.

2. **Layout expressiveness.** The composition document's layout model is a
   twelve-column grid (geospatial-mcp ADR-0030). Shell regions, split panes,
   responsive breakpoints, and stacked flows have no representation, so a
   split-screen application is not expressible regardless of which components
   exist.

3. **Esri migration.** `honua-sdk-js/src/migration/widget-dispositions.ts` is a
   real disposition matrix — 25 `automated`, 14 `compat-shim`, 4 `assisted` —
   codemodding ArcGIS widgets onto `@honua/app-platform/web-components`. Every
   entry is map-domain, and the `no-equivalent` bucket is 3D scene analysis
   (Daylight, LineOfSight, ShadowCast, Slice, Weather). Application chrome is
   not merely uncovered; it is not represented in the matrix even as a gap.

4. **Forms.** [ADR-0069](0069-studio-persistence-bridge-forms-analysis.md)
   already establishes `honua.form-package.v1` documents with
   `FormPackageValidator`, monotonic versioning, offline compatibility
   manifests, and submission idempotency. The *document* format exists. There
   is no rendering vocabulary to put it on a screen.

Esri solves this for itself with Calcite Design System. Building an equivalent
is a multi-year effort with a dedicated team, it is not where Honua
differentiates, and a home-grown set would arrive without the accessibility
and internationalization evidence that government and enterprise procurement
asks for.

## Decision

Adopt **UI5 Web Components** (`@ui5/webcomponents` and
`@ui5/webcomponents-fiori`, Apache-2.0) as the application-chrome component set
across `honua-sdk-js`, `honua-console`, and `honua-studio`.

The deciding factor is coverage against the actual gap. Honua's application
surface is data-dense — attribute tables, feature lists, filter bars, and
schema-driven forms carrying coded-value domains and subtypes. Data tables,
form layout and validation, wizards, filter bars, shell bar, and side
navigation are the mature centre of UI5 rather than its periphery, which is not
true of the alternatives. UI5 additionally ships accessibility conformance
evidence backed by statutory obligation, and CLDR-backed internationalization
with RTL and full calendar/date/number handling — both of which are expensive
and unpleasant to retrofit.

Six sub-decisions are normative.

### 1. One design system across every surface

No per-surface split. A superficially attractive arrangement gives the console
UI5's density and public embeds something lighter; it is rejected, because two
design systems means two theming efforts, two runtimes, and a product that
looks inconsistent to the same user across two screens. The bundle cost on
public-facing embeds is accepted and mitigated with per-component imports.

### 2. Wrapped, never exposed in documents

Composition documents address components by Honua-owned semantic names —
`form`, `field`, `panel`, `splitView`, `tabs`, `dialog`, `list`, `table` — and
never by `ui5-*` tag names. Two reasons, both load-bearing. The geospatial-mcp
composition vocabulary is a vendor-neutral standard, and encoding a vendor's
tag names into it would be a permanent coupling. And swapping the library later
must not be a breaking change to published documents.

This is the same discipline as the protocol-adapter rule: the document is
canonical and the widget library is an implementation detail behind it.

### 3. Map elements stay Honua's

The existing map elements are not replaced. UI5 supplies chrome; Honua supplies
the map domain. The boundary is that anything which knows about geometry, CRS,
layers, features, styling, or tiles is Honua's, and anything which does not is
a candidate for UI5.

### 4. The component registry is generated, not hand-maintained

The agent-facing component registry is derived from Custom Elements Manifests —
UI5's published manifest plus a manifest generated for Honua's own elements —
merged into one component manifest exposed as an MCP resource.

Deriving it matters beyond convenience. Hand-maintained rosters in this
codebase drift: a tool list, a prose tool count, and a generated feature
catalog have each had to be edited in lockstep, and that class of edit produces
merge conflicts and silent inaccuracy. A generated registry removes the class.

### 5. Forms render `honua.form-package.v1`; the document format is unchanged

UI5 supplies field controls and form layout. The form *document* remains
ADR-0069's. Generating a form from layer metadata — field types, coded-value
domains, subtypes, nullability, editability, attachment rules — is Honua's
work and is explicitly not delegated to the component library. That generation
is what turns "build a permit intake form for the permits layer" into one
operation rather than forty, and it is the same engine `honua-collect` and
`honua-mobile` require.

### 6. Theming is a Honua token layer

Fiori is not shipped as Honua's visual identity. A Honua token layer over UI5's
theming parameters is required work, budgeted as design rather than treated as
a configuration step.

### 7. Charting is a separate grammar, and it is Vega-Lite

UI5 ships **no charting at all** — there is not a single chart element among
the 139 core or 63 `-fiori` components. Charting was therefore always going to
be a separate dependency, and this ADR settles it because no prior ADR records
the choice even though the code has already made it.

**The chart *grammar* is Vega-Lite** (BSD-3-Clause). `src/studio/chart-spec.ts`
in `honua-sdk-js` already targets it with a deliberately narrowed vocabulary —
four marks (`bar`, `line`, `point`, `arc`), four field types (`quantitative`,
`nominal`, `ordinal`, `temporal`), and a closed aggregate set — translated by
`chartWidgetToVegaLiteSpec()`. That narrowing is ratified here as normative,
for a reason given below.

**The chart *renderer* is swappable and is not Vega.** `src/analytics/`
carries an adapter registry with µPlot (MIT) as the current adapter. Keeping
the grammar decoupled from the Vega runtime is the load-bearing part of this
decision, not an implementation detail: vega-lite unpacks to 5.8 MB and the
Vega runtime to a further 3.7 MB, against µPlot's 545 KB. On a public-facing
map embed that difference is decisive. **The Vega runtime MUST NOT become a
non-optional dependency**; if a full-fidelity Vega adapter is added as a
correctness fallback, it is lazy-loaded, and any additional adapter (Apache
ECharts is the plausible candidate for sankey/treemap/gauge coverage and
large-N canvas rendering) joins the same registry rather than replacing the
grammar.

**Agents author the narrowed Honua grammar, never raw Vega-Lite — scoped to
SDK Studio compositions.** This rule governs the `honua-sdk-js` Studio
composition path only. It does **not** retroactively bind the two server-side
generators, which today instruct agents to emit raw Vega-Lite **v5**:
`ReportGenerationPrompt.cs:29` and `DashboardGenerationPrompt.cs:30` both
require a `chartSpec` declaring `https://vega.github.io/schema/vega-lite/v5.json`,
and their generation schemas leave `chartSpec` unconstrained.

That is a real divergence, stated here rather than papered over: two agent
chart contracts coexist, on different Vega-Lite majors. It is not resolved by
this ADR because those two prompts belong to the server-side generation
families that **#3255 (D5) proposes retiring** in favour of client-side
inference. If D5 lands, the divergence disappears with the generators. If D5 is
rejected, the generators must be migrated onto the narrowed grammar and the
version pin reconciled — and that migration is then in scope for whoever
rejects it, not deferred again. The
published `vega-lite-schema.json` is 1.88 MB across 458 JSON Schema draft-07
definitions with deep `$ref` nesting. It cannot be placed in a model's context,
and a model authoring against it directly will not reliably produce valid
output. The narrowed Honua chart grammar carries its own small schema, the
model authors that, and translation to Vega-Lite happens afterwards. This is
the same relationship sub-decision 2 establishes between semantic component
names and `ui5-*` tag names.

**The schema version is pinned deliberately and tracks latest stable.** The
grammar targets the current stable Vega-Lite major (v6). Version drift is a
decision, not an accident: bumping requires re-validating the narrowed
vocabulary against the new schema.

Why Vega-Lite rather than the alternatives. **Observable Plot** has the most
elegant API but its spec is JavaScript, not a JSON document, so it cannot be
serialized, persisted, validated, or diffed — disqualifying for both agent
authoring and a durable composition document. **Apache ECharts** is JSON-config
driven and therefore authorable, with broader chart coverage, but has no
rigorous published schema and an enormous, inconsistently-nested option
surface, which makes constraining a model to valid output much harder; it is a
good renderer adapter and a poor grammar. **Plotly** and **Chart.js** are
JSON-ish configuration rather than grammars, and are weak at layering and
faceting.

Two properties specific to Honua's architecture decided it. Vega-Lite's
`params` and selections are first-class grammar constructs producing filter
predicates, which is the natural bridge into the shared exploration contexts of
geospatial-mcp ADR-0032 — a chart bound with `role: "chart"` publishes its
brush into the shared `filters` slice, and because the semantics belong to the
grammar rather than to a renderer, they survive an adapter swap. And a
declarative, schema-validatable spec can be checked *before* it renders, which
is the same posture as the typed composition document; an imperative charting
API cannot be.

Chart accessibility is weak across the entire ecosystem regardless of library.
`src/analytics/accessible-table.ts` already provides the standard mitigation, a
data-table alternative, and it is normative rather than optional: a rendered
chart ships with its accessible table equivalent.

## Alternatives considered

**Adobe Spectrum Web Components** (Apache-2.0) was the runner-up and the
closest call. It is Lit-based with a lighter runtime, a conventional
Custom Elements Manifest pipeline, a more visually neutral design language, and
`sp-split-view` covers split screens directly. It was not selected because the
gap is data-dense surface: there is no production data grid (`sp-table` lacks
virtualization, column resize, and grouping), no wizard or multi-step flow, no
shell frame, no filter bar, and form orchestration is thin. Adopting it would
leave the hardest third of the requirement to build in-house. A secondary
concern is that Adobe's primary consumer is React Spectrum, which makes the
long-term priority of the framework-agnostic sibling less certain than UI5's,
where web components *are* the vendor's strategy for non-native consumers.

**Esri Calcite Design System** is the best functional fit for map-adjacent
chrome and carries genuine migration affinity: a hand-coded ArcGIS Maps SDK
application's chrome is frequently the majority of its source, and rendering
Calcite would let that markup survive migration with only map and data calls
changing. It was rejected as the primary set because it is a hard runtime
dependency on a competitor's roadmap — the Apache-2.0 grant protects the
versions already held, not release cadence, breaking changes, or direction —
and because every Honua application would then carry a competitor's visual
identity. It is retained in a narrower role; see below.

**Shoelace / Web Awesome** was rejected because the successor project moved a
substantial share of components behind a paid Pro tier, leaving the MIT
Shoelace 2.x line as a frozen predecessor.

**Carbon** (Apache-2.0) has a strong data table and enterprise fit but a
distinctly IBM identity and narrower form breadth than UI5. **Material Web** has
incomplete coverage and has been deprioritized by its maintainer. **Fluent UI
Web Components** carries churn risk across its versions. **Lion** is
accessibility-first and deliberately white-label, which is attractive for
theming freedom, but it supplies little coverage — choosing it is closer to
building our own with help.

**Building our own** was rejected on cost and focus, and because it would ship
without the accessibility and internationalization evidence procurement
requires.

## Calcite on the migration path only

`widget-dispositions.ts` already treats `compat-shim` as a first-class
disposition with 14 existing entries. Migrated hand-coded ArcGIS applications
may therefore retain their Calcite markup through a compat shim on the
migration path, without Calcite becoming a dependency of new-application
authoring and without it entering the composition vocabulary. This preserves
most of the migration benefit while keeping the strategic dependency out of the
product.

## Verification gates

This decision was reached on reasoning about coverage and maintainer posture.
Five properties were assumed and must be measured rather than taken on faith.
Two of them can reopen the comparison. Gate 1 has since been measured and
passed; gates 2–5 remain open and are due at first integration.

Coverage against the chrome gap was also confirmed directly from the `-fiori`
manifest: `ui5-flexible-column-layout` covers split screens,
`ui5-navigation-layout` / `ui5-shellbar` / `ui5-side-navigation` /
`ui5-dynamic-page` cover the app frame, and `ui5-wizard` covers multi-step
flows — the specific components whose absence motivated this ADR.

1. **Custom Elements Manifest completeness and typing.** Sub-decision 4 depends
   on it. **Measured against `@ui5/webcomponents@2.25.0` and
   `@ui5/webcomponents-fiori` on 2026-08-15 — this gate passes.**

   The package publishes `dist/custom-elements.json` (CEM schemaVersion 1.0.0,
   ~1.3 MB) declaring 139 custom elements, with 63 more in the `-fiori`
   package. Across the core package: 971 attributes, of which **100% carry a
   type, 100% carry a description, and 100% declare a default**; 153 slots, all
   described; 141 CSS parts; every element described.

   Critically for agent authoring, enumerated values are inlined as
   string-literal unions in the type text — `"Text" | "Email" | "Number" |
   "Password" | "Tel" | "URL" | "Search"` — across 199 attributes. Those
   convert directly into JSON Schema `enum` constraints with no need to resolve
   separate enum declarations (of which the manifest has none).

   **The one gap is event payloads.** Of 202 events, only about half carry a
   typed detail (`CustomEvent<ListItemBaseClickEventDetail>`); the rest are bare
   `CustomEvent`. The detail shapes are recoverable — they are declared in the
   561 shipped `.d.ts` files, e.g. `ListItemBaseClickEventDetail = { item?:
   ListItemBase; originalEvent: Event }` — but a generator must read TypeScript
   to reach them rather than reading the manifest alone. This matters
   specifically because event detail shape is what `$event.*` path substitution
   binds against in the composition interaction model, so the registry
   generator needs a TypeScript-reading stage for events. Bounded work, not a
   blocker.
2. **CSP behaviour** without `unsafe-inline`, given UI5's own bootstrap and
   theme/i18n asset loading. **Reopening gate**, together with (3).
3. **Blazor host behaviour** under enhanced navigation and render-mode
   re-instantiation, plus focus management across shadow-DOM boundaries. The
   `harness/blazor-host` fixture in `honua-studio` exists for exactly this
   hazard and should be the proving ground.
4. **Form-associated custom elements** (`ElementInternals`) for real native
   `<form>` participation.
5. **Realistic per-application bundle weight**, measured on a public-facing
   embed rather than on the console.

## Consequences

**Easier.** Application chrome exists, so forms, split screens, dialogs,
wizards, and dense tables become buildable rather than bespoke. Accessibility
conformance evidence arrives with the dependency instead of being a project.
Internationalization and RTL are shipped rather than retrofitted. The
agent-facing component registry becomes generated, which both unblocks reliable
AI authoring and removes a recurring class of hand-maintained-roster drift.
Chrome becomes representable in the migration disposition matrix for the first
time. Console, Studio, and embedded SDK converge on one design system.

**Harder.** Fiori is a strong visual identity, so reaching a distinctly Honua
look is real design work rather than a token swap, and it must be budgeted
explicitly or the product will ship looking like an SAP application. UI5 carries
more of its own runtime than a plain Lit library — bootstrap, theming, i18n and
theme asset loading — which is more integration surface and is the part most
likely to misbehave under CSP or in the Blazor host. Public-facing embeds pay a
bundle-weight cost that requires per-component imports and ongoing measurement.
And hand-coded ArcGIS chrome gains no migration affinity except through the
compat shim.

## Non-goals

- Replacing the existing map elements.
- Changing the form document format, which ADR-0069 owns.
- Extending the composition document's layout model beyond a twelve-column
  grid. That gap is real and blocks split-screen applications, but it is a
  change to the geospatial-mcp standard and needs its own ADR.
- Schema-driven form generation from layer metadata. It is required, it is
  Honua's to build, and it is separate work.
