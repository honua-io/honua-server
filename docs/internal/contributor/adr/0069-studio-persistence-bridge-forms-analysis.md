# ADR-0069: Bridge forms and analysis persistence into the Studio package lifecycle

## Status

Accepted. Implements honua-server#3004 (studio-v0 adversarial review, P0-2).

## Context

Console's live editors persist through three different server surfaces:

1. **Studio package lifecycle API** (`/api/v1/studio/*`, `IStudioPackageStore`,
   `studio_*` tables): draft → immutable content version → publish-request →
   rollback, with draft `generation` optimistic concurrency, item-level
   current/published pointers, and the honua-server#3001/#3018 ownership +
   fail-closed authorization model.
2. **Forms package API** (`/api/v1/admin/forms/packages`, `IFormPackageStore`,
   `form_packages` / `form_package_versions` tables): `honua.form-package.v1`
   documents, monotonic integer versions with `draft`/`published`/`archived`
   status, strong ETag + `If-Match` draft concurrency, a DB trigger that makes
   published rows physically immutable, publish-time validation
   (`FormPackageValidator`), offline compatibility manifests keyed by
   content/policy hashes, and submission idempotency records keyed by
   `(form_id, version)`.
3. **Analysis content API** (`/api/v1/analysis/content` + artifacts + jobs,
   `IAnalysisContentStore`, `analysis_content_*` tables): string `itm_*` item
   ids, append-only immutable versions with server-assigned monotonic numbers,
   job/artifact provenance links (`CreatedFromJobId`,
   `CreatedFromArtifactIds`, `ResultArtifactRecord.Source*` back-links,
   `AnalysisContentMetadataKeys` job metadata stamps), and retention-governed
   artifacts that deliberately have **no** foreign keys and never cascade.

The studio-v0 plan's "one JS Studio round-trips with console" claim only holds
for lifecycle-native families. Without unification, the console retirement
schedule for form/analysis editors (honua-console#324) has no persistence path.

## Decision

**Both families are BRIDGED, not migrated.** The Studio lifecycle API becomes
the single client-visible contract for enumerating, opening, drafting, and
saving form and analysis content, but it delegates persistence to each family's
native store through a per-family adapter
(`IStudioFamilyPersistenceBridge`, implemented by `FormStudioPackageBridge`
and `AnalysisStudioPackageBridge`, composed by `BridgedStudioPackageStore`
decorating whichever `IStudioPackageStore` is registered).

### Why bridge and not migrate

Migration into `studio_*` tables was rejected per family:

- **Forms.** `form_package_versions` is load-bearing beyond authoring:
  submission idempotency records reference `(form_id, version)`, offline
  clients hold `ContentHash`/`PolicyHash` pairs consumed by the compatibility
  manifest, the runtime submission path resolves the current *published*
  version, and a DB trigger enforces immutability of published rows. Moving
  rows into `studio_content_versions` would sever submission/idempotency
  references, break offline compatibility manifests mid-flight, and require a
  flag-day cutover of the console form builder — violating REQ-003 (no
  flag-day) and NFR-001 (zero data loss).
- **Analysis.** Artifact records back-reference `SourceItemId`/
  `SourceVersionId` as loose strings, and running jobs carry
  `analysis.content.*` metadata stamps that resolve against the analysis
  store. A migration would orphan every artifact/job link for in-flight and
  historical runs, and the geoprocessing terminal callback writes artifacts
  back through `IAnalysisContentStore` — the native store must stay
  authoritative.

Because the native stores remain the single source of truth, there is **no
data migration and no dual-write divergence risk**: the bridge reads native
rows on demand and writes through the native stores' own APIs. Console's
existing editors keep working unchanged throughout (REQ-003) because the
native HTTP surfaces and stores are untouched.

## The bridged contract

Bridged families surface through the existing lifecycle endpoints and
envelope; no new routes are added. Per family:

| Concern | Form family | Analysis family |
|---|---|---|
| Native store | `IFormPackageStore` | `IAnalysisContentStore` (kind `analysisPackage`) |
| Envelope `format` | `honua.form-package.v1` | `honua.analysis-content.v1` |
| Envelope `body` | The native `FormPackageDocument`, serialized with the family's own `FormPackageJsonContext` | The native `AnalysisPackageContent`, serialized with `AnalysisContentJsonContext` |
| Item id mapping | `formId` that parses as a GUID maps directly; otherwise a deterministic SHA-256-derived GUID with reverse lookup via `ListPackagesAsync` | `itm_{guid:N}` ids map directly; otherwise deterministic derived GUID with reverse lookup via `ListItemsAsync` |
| Version id mapping | Deterministic GUID derived from `formId:version` | Deterministic GUID derived from the native `versionId` (`{itemId}:v{n}`) |
| `versionNumber` | Native integer version | Native integer version |
| `contentHash` | Native `ContentHash` | Native `ContentHash` |
| Current pointer | Native `currentDraftVersion ?? currentPublishedVersion` | Native `CurrentVersion` |
| Published pointer | Native `currentPublishedVersion` | None (analysis has no publish concept) |
| Save-as-version | `IFormPackageStore.SaveDraftAsync` — always a **new** native draft version | `AddVersionAsync` (append-only, monotonic, conflict-retry) or `CreateItemAsync` for a new item |
| Publish-request | Validates with the native `FormPackageValidator` then `PublishAsync`; invalid content yields a `rejected` request and no native publish | Not supported (`publishSupported: false`); route-level publication remains the Content Publication Registry's job |
| Rollback | Not supported (published rows are DB-immutable; reopen + republish instead) | Not supported (append-only pointer only moves forward) |
| Drafts | Studio-native (`IStudioPackageStore` drafts with `generation` concurrency); the native draft row is only touched on save | Same |
| `ownerId` | `null` — forms has no ownership model → **fail-closed**: only admin can act on bridged form items under `Studio:EndUserAuthorization:Enabled`, matching the native surface's admin-only posture | Native `OwnerId`, enforced by the existing #3018 ownership checks |

### Family-semantic preservation (REQ-002 / NFR-001)

- **Form JSON-kind preservation, ruleId stability, unmodeled-field
  round-trip:** the bridge deserializes/serializes envelope bodies with the
  family's own source-generated `FormPackageJsonContext` — exactly the
  serializer the native endpoint uses. `JsonElement`-typed fields
  (`defaultValue`, domain `min`/`max`/`code`, rule `parameters`, conditional
  `value`) round-trip with their JSON kinds intact, and `ruleId`s are copied
  verbatim, byte-for-byte matching native behavior. The native contract has no
  `[JsonExtensionData]`, so unmodeled fields are dropped **by the native
  surface itself**; the bridge is exactly as lossless as the native API, which
  is the NFR-001 bar (round-trip fixtures prove native-parity, not
  better-than-native).
- **Analysis artifact/job links:** `CreatedFromJobId` and
  `CreatedFromArtifactIds` are projected into the envelope's `dependencies`
  sidecar (`kind: "job"` / `kind: "artifact"`) on read and written back to the
  native version record on save, so lifecycle-authored versions keep the same
  provenance joins the artifacts/jobs APIs resolve.

### Concurrency semantics

- Studio drafts keep `generation` optimistic concurrency (Studio-side working
  state, unchanged).
- A lifecycle save **never** blind-overwrites a native form draft: it appends
  a new native version via `SaveDraftAsync`, so a console editor concurrently
  editing draft *N* with `If-Match` is never clobbered — cross-surface
  concurrent edits produce distinct native versions instead of lost updates.
  Analysis saves use the native append-only monotonic numbering with the same
  conflict-retry the native service uses.

### Documented divergences (also advertised via `GET /package-families` limitations)

1. A form content version saved through the lifecycle is a native **draft**
   version: it is immutable *through the lifecycle surface* but remains
   editable through the native ETag surface until published. True immutability
   attaches at publish (DB trigger).
2. Bridged items appear in `GET /content-items` only once first saved;
   unsaved bridged drafts are visible through `GET /package-drafts`.
3. Bridged enumeration merges at most 1,000 native rows per family into the
   keyset-paginated listing.
4. Publish-request (analysis) and rollback (both) return `409` with an
   explanatory message; the operations are omitted from the families'
   advertised `supportedOperations`.
5. Version `compare` uses the native content hashes; hashes are only
   comparable within one family's hashing scheme (which is all the endpoint
   ever compares).

### Compatibility plan (REQ-003)

- Native routes, DTOs, ETags, and stores are byte-for-byte unchanged; console
  editors continue against them with no flag-day.
- The lifecycle surface reads native state live, so content authored in
  either surface is immediately visible in the other (dual-read by
  construction; no background sync, no divergence window).
- honua-console#324's form/analysis parity gates should reference this
  contract: the lifecycle envelope (`family: form|analysis` + native-format
  body) is the JS-Studio persistence path.

## Consequences

- `GET /package-families` now advertises `honua.form-package.v1` /
  `honua.analysis-content.v1` as the `form`/`analysis` formats (previously the
  placeholder `studio_form_package.v1` / `studio_analysis_package.v1`, which
  nothing outside the registry referenced).
- The saved-query (`query` family ↔ `AnalysisContentKind.SavedQuery`) bridge
  is deliberately deferred; it reuses `AnalysisStudioPackageBridge`
  parameterized by kind and is tracked as a follow-up.
- Bridged reads add native-store probes on version/pointer resolution; this
  surface is an admin/authoring control plane, not a runtime hot path.
- When neither native store is registered (for example bare `Honua.Core`
  hosts or MCP-only compositions), no bridge is registered and behavior is
  unchanged.
