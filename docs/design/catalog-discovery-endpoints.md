# Catalog Discovery-Endpoints Registry — Backend Scoping & Handoff

**Status:** Proposed · scoping for implementation handoff
**Issue:** honua-server#1279
**Owner (UI side):** honua-console `/operate/catalogs` surface (the discovery-endpoints registry, per-endpoint
detail, and per-item editor)
**Audience:** the engineer/agent implementing the honua-server side
**Goal:** populate the catalog discovery-endpoints registry from **real published catalog state** and add
the **enable/disable (and auto-default vs opt-in) mutation** the `/operate/catalogs` surface needs, so the
page renders live discovery endpoints (Esri / OGC API Records / OData / STAC / DCAT) instead of the honest
missing-binding/empty state it shows today.

---

## 1. TL;DR — what this is and is not

This is **not** a greenfield "build a catalog registry" task. The read contract, DTOs, store seam, and
endpoint wiring already exist and are mirrored by the console:

- A read API, `CatalogDiscoveryEndpoints`
  (`src/Honua.Server/Features/Console/CatalogDiscoveryEndpoints.cs`) mapped at
  `/api/v{version}/console/catalog-endpoints`, `RequireAdminAuthorization()`, returning `ApiResponse<T>`:
  - `GET /{workspaceId}` → `CatalogDiscoveryRegistry`
  - `GET /{workspaceId}/{endpointKey}` → `CatalogEndpointDetail`
  - `GET /{workspaceId}/{endpointKey}/items/{itemId}` → `CatalogItem`
- The DTOs (`src/Honua.Server/Features/Console/Models/CatalogDiscoveryModels.cs`): `CatalogDiscoveryRegistry`,
  `CatalogEndpoint` (`dialect`, `enabled`, `autoDefault`, `url`, `entries`, `feeders[]`, `issueCount`),
  `CatalogEndpointDetail`, `CatalogEndpointItem`, `CatalogItem` + field groups. Dialect constants
  (`CatalogDialects`: `esri`/`ogc`/`odata`/`stac`/`dcat`) match the console's `HonuaCatalogDialects`. The
  header comment states these shapes deliberately mirror honua-console
  `Honua.Console.Contracts/CatalogDiscoveryShims.cs` (`IHonuaCatalogDiscoveryClient`) so the binding
  activates the moment the API publishes.
- The store seam, `ICatalogDiscoveryRegistryStore`
  (`src/Honua.Server/Features/Console/Services/ICatalogDiscoveryRegistryStore.cs`) — **read-only by design**
  ("the authoritative endpoint configuration is owned by server config/metadata, so implementations are not
  expected to support writes").
- A config-backed implementation, `ConfigCatalogDiscoveryRegistryStore`
  (`src/Honua.Server/Features/Console/Services/ConfigCatalogDiscoveryRegistryStore.cs`), registered in
  `Program.cs` (~line 476) with **no seeds** today: `new ConfigCatalogDiscoveryRegistryStore()`. It honours
  a strict **no-fabrication** principle — an unconfigured deployment returns an empty registry, never sample
  data.

So the read contract is done and live. **The work is two bounded gaps:**

1. **The registry is always empty.** `ConfigCatalogDiscoveryRegistryStore` is constructed with no seeds, and
   there is no projection that derives the endpoint cards from the server's *actual* published catalogs
   (the GeoServices catalog, OGC API Records/Features, OData service document, STAC catalog, DCAT). So
   `/operate/catalogs` binds, gets an empty registry, and shows the honest empty state. The gap is a **real
   projection feeder**.
2. **No mutation.** The store and routes are read-only. The `/operate/catalogs` surface needs to **toggle an
   endpoint on/off** and **flip auto-default vs opt-in**, which has nowhere to land today.

This doc scopes both, reusing the frozen read DTOs verbatim.

---

## 2. Existing pieces to reuse (do not reinvent)

| Concern | Existing type / file | Reuse as |
| --- | --- | --- |
| Read API + routes | `CatalogDiscoveryEndpoints` · `Honua.Server/Features/Console` | Keep; add the mutation routes alongside |
| Wire DTOs | `CatalogDiscoveryModels.cs` (`CatalogDiscoveryRegistry`, `CatalogEndpoint`, `CatalogEndpointDetail`, `CatalogItem`) | Frozen; the console mirrors them — do not rename fields |
| Store seam | `ICatalogDiscoveryRegistryStore` | Extend with mutation (`SetEndpointEnabledAsync`, `SetEndpointAutoDefaultAsync`) |
| Config-backed store | `ConfigCatalogDiscoveryRegistryStore` | Keep as the projection target; feed it from real catalog state |
| Dialect identity | `CatalogDialects` (`esri`/`ogc`/`odata`/`stac`/`dcat`) | The endpoint `dialect`/`key` vocabulary |
| Envelope | `ApiResponse<T>` · `src/Honua.Hosting/Features/Models/ApiResponse.cs` | This group already uses it (success/failure) |
| Catalog sources to project from | `src/Honua.Core/Features/Catalog`, `src/Honua.Protocols.GeoServices/Catalog`, plus the OGC API Records / STAC / OData metadata surfaces | Read the live published set; do **not** rebuild catalogs |
| One-shot discovery (do NOT confuse) | `ExternalServiceDiscoveryEndpoints` (`/api/v{version}/admin/external-services/discover`) | This is *inbound* discovery of *external* services to import — unrelated to *this* surface, which is *outbound* discovery the server *publishes*. Keep them distinct. |

> **Distinct from #1162.** This surface (`/console/catalog-endpoints`) describes *which discovery dialects
> the server publishes to consumers*. It is **not** the singular content catalog (`/api/v1/console/content`,
> #1162) of content items. The DTO header comment already calls this out — keep the separation.

---

## 3. Architecture

```
            GET  /api/v{version}/console/catalog-endpoints/{workspaceId}           (read — exists)
            GET  …/{workspaceId}/{endpointKey}                                     (read — exists)
            GET  …/{workspaceId}/{endpointKey}/items/{itemId}                      (read — exists)
            PUT  …/{workspaceId}/{endpointKey}/enabled        (NEW — toggle on/off)
            PUT  …/{workspaceId}/{endpointKey}/auto-default   (NEW — auto-mirror vs opt-in)
                                          │
                                          ▼
                       ICatalogDiscoveryRegistryStore  (extend: read + targeted mutation)
                                          │
              ┌───────────────────────────┴───────────────────────────┐
              ▼                                                         ▼
   CatalogDiscoveryProjection (NEW)                        Durable enable/auto-default overrides (NEW)
   reads the server's live published catalogs              persisted per (workspace, endpointKey);
   (GeoServices catalog, OGC API Records/Features,         the projection reports the *effective* state
   OData $metadata, STAC, DCAT) → endpoint cards,          = published availability AND not disabled
   feeders, entry counts, per-item field groups
```

Effective `enabled` = the dialect is actually published by the server **and** an operator has not disabled
it. `autoDefault` = whether new publications auto-mirror into the endpoint (vs resources opting in). Both are
operator overrides layered over the projected baseline.

---

## 4. What exists vs the gap (precise)

| Capability | State |
| --- | --- |
| `GET …/{workspaceId}` registry read + DTOs | **Exists** (returns empty until projected) |
| `GET …/{workspaceId}/{endpointKey}` detail read | **Exists** |
| `GET …/{workspaceId}/{endpointKey}/items/{itemId}` item read | **Exists** |
| Console client mirror (`CatalogDiscoveryShims.cs`) | **Exists** (per DTO header comment) |
| DI registration | **Exists** (`Program.cs` ~476, no seeds) |
| **Projection from real published catalogs** | **GAP** — store is empty; nothing feeds it |
| **Enable/disable endpoint mutation** | **GAP** — store + routes are read-only |
| **Auto-default vs opt-in mutation** | **GAP** |

---

## 5. The console wire contract

### 5.1 Reads (FROZEN — already built; restated for completeness)

`GET /api/v{version}/console/catalog-endpoints/{workspaceId}` → `ApiResponse<CatalogDiscoveryRegistry>`:

```jsonc
{ "success": true, "data": {
  "workspaceId": "default",
  "workspaceName": "Default workspace",
  "publicHost": "https://maps.example.gov",
  "endpoints": [
    { "key": "esri",  "title": "Esri catalog",      "dialect": "esri",  "enabled": true,  "autoDefault": true,
      "url": "https://maps.example.gov/rest/services", "entries": 42, "fedBy": "12 FeatureServers",
      "feeders": [ { "kind": "feature-server", "label": "public-works-fs" } ], "issueCount": 0 },
    { "key": "stac",  "title": "STAC catalog",       "dialect": "stac",  "enabled": false, "autoDefault": false,
      "url": "https://maps.example.gov/stac", "entries": 0, "feeders": [], "issueCount": 0 }
    // … ogc, odata, dcat …
  ],
  "autoDefaultCount": 2, "optInCount": 3
}}
```

`404` (`ApiResponse<object>.Failure`) when the workspace is unknown. A known workspace with no published
dialects returns a registry with an empty `endpoints` list (not a 404).

### 5.2 `PUT /api/v{version}/console/catalog-endpoints/{workspaceId}/{endpointKey}/enabled` — NEW

Request: `{ "enabled": true }`
Response: `ApiResponse<CatalogEndpoint>` — the updated card with the new effective `enabled`.

- `404` when the workspace or `endpointKey` is unknown.
- `409` when the dialect is not actually published by the server (cannot enable an endpoint the server does
  not serve) — return `ApiResponse<object>.Failure` with a reason. This preserves no-fabrication: you can
  only toggle endpoints that physically exist.
- Records an audit event (`catalog_endpoint.enable` / `.disable`) via `IAuditLog`, mirroring
  `AlertAdminEndpoints` / `ReplicaManagementEndpoints` audit shape.

### 5.3 `PUT /api/v{version}/console/catalog-endpoints/{workspaceId}/{endpointKey}/auto-default` — NEW

Request: `{ "autoDefault": true }`
Response: `ApiResponse<CatalogEndpoint>` — the updated card.

- Same `404`/`409` semantics. `autoDefault=true` means new publications auto-mirror into this endpoint;
  `false` means resources opt in. The projection recomputes `autoDefaultCount`/`optInCount`.

### 5.4 Camel-case / enum conventions

JSON is camelCase. `dialect` and `key` use the lowercase `CatalogDialects` values. `state` on
`CatalogItemField` uses `CatalogFieldStates` (`system`/`calculated`/`input`). These are existing string
literals — do not introduce CLR enum names on the wire.

---

## 6. The projection (filling the empty registry)

The projection is the substance of #1279. Build `CatalogDiscoveryProjection` that, per workspace, reads the
server's **live published surfaces** and emits the endpoint cards + items:

| Dialect (`key`) | Source to project from | `entries` | `feeders` |
| --- | --- | --- | --- |
| `esri` | GeoServices catalog (`src/Honua.Protocols.GeoServices/Catalog`) — published Feature/Map services | service count | FeatureServer/MapServer feeders |
| `ogc` | OGC API Records / OGC API Features collections | collection count | `ogc-features` feeders |
| `odata` | OData v4 service document / entity sets | entity-set count | `odata-entity-set` feeders |
| `stac` | STAC catalog/collections | collection/item count | STAC feeders |
| `dcat` | DCAT dataset catalog | dataset count | DCAT feeders |

- **Effective enabled** = the dialect is published (the protocol is enabled server-wide and has ≥0 entries)
  AND not operator-disabled. A published-but-empty endpoint shows `enabled:true, entries:0` (honest), not
  hidden.
- Per-item field groups (`CatalogItem.groups`: Identity / Catalog presentation / Service bindings /
  Standards mapping) derive from the publication's metadata; `CatalogItemField.state` marks which fields are
  `system` (identity), `calculated` (derived from the backing resource), or `input` (catalog-only operator
  text).
- **No fabrication:** if a protocol is not enabled/published, omit its endpoint card entirely — do not emit a
  disabled placeholder unless an operator override exists for it.
- Where the projection is expensive, cache it behind the shared metadata/catalog cache helpers (the
  registry is metadata, not ad-hoc spatial — caching is allowed per AGENTS.md cross-cutting rules) and vary
  the key by workspace + host/scheme (links are host-dependent).

Operator overrides (enabled/auto-default) persist in a small durable table (Postgres) keyed by
`(workspaceId, endpointKey)`; the projection layers them over the baseline. For dev/test, an in-memory
override store mirrors the pattern (cf. the in-memory vs Postgres split used by `IAnalysisContentStore`).

---

## 7. Auth, config, secrets

- **Auth:** all routes inherit the group's `RequireAdminAuthorization()`. The `/operate/catalogs` surface is
  admin; the console sends `X-API-Key`. Mutations write audit events (config-change category).
- **Config:** `workspaceId` is the existing workspace/tenant concept the read API already takes. `publicHost`
  comes from the server's configured public host (links). No new secrets.
- **No-fabrication:** preserve the existing principle (`ConfigCatalogDiscoveryRegistryStore` header). The
  registry must reflect what the server actually serves.

---

## 8. Build order (suggested)

1. **`CatalogDiscoveryProjection`** that materialises endpoint cards from the live published catalog surfaces
   for the `esri` dialect first (the highest-value, GeoServices catalog already exists), then `ogc`, `odata`,
   `stac`, `dcat`. Feed it into `ICatalogDiscoveryRegistryStore`. **This alone turns `/operate/catalogs`
   green for read** (the page binds real endpoints).
2. **Override store** (`(workspaceId, endpointKey)` → enabled/auto-default), Postgres + in-memory; layer it
   into the projection's effective-state computation.
3. **Mutation routes** (`PUT …/enabled`, `PUT …/auto-default`) in `CatalogDiscoveryEndpoints`; extend
   `ICatalogDiscoveryRegistryStore` with the two setters; add audit; add routes to `EndpointRegistry.cs`;
   add request DTOs to `ConsoleJsonContext`.
4. **Per-item field-group derivation** for `CatalogItem` so the item editor (drill-down) shows real
   identity/presentation/binding/standards fields.
5. **Tests** mirroring the console-content/discovery test patterns: empty workspace, projected endpoints per
   dialect, enable→effective-state flip, 409 enable-unpublished, auto-default recount.

Step 1 is the read unblock; step 3 is the write unblock.

---

## 9. Endpoints to register (admin, `RequireAdminAuthorization()`, api-version 1.0)

| Method | Route | Returns |
| --- | --- | --- |
| `GET` | `/api/v{version}/console/catalog-endpoints/{workspaceId}` | `ApiResponse<CatalogDiscoveryRegistry>` (exists) |
| `GET` | `/api/v{version}/console/catalog-endpoints/{workspaceId}/{endpointKey}` | `ApiResponse<CatalogEndpointDetail>` (exists) |
| `GET` | `/api/v{version}/console/catalog-endpoints/{workspaceId}/{endpointKey}/items/{itemId}` | `ApiResponse<CatalogItem>` (exists) |
| `PUT` | `/api/v{version}/console/catalog-endpoints/{workspaceId}/{endpointKey}/enabled` | `ApiResponse<CatalogEndpoint>` (new) |
| `PUT` | `/api/v{version}/console/catalog-endpoints/{workspaceId}/{endpointKey}/auto-default` | `ApiResponse<CatalogEndpoint>` (new) |

Register new routes in `CatalogDiscoveryEndpoints.cs`, add to `EndpointRegistry.cs`, add request DTOs to the
console JSON source-gen context. Telemetry: reuse `ConsoleEndpointsLog.EndpointFailed` with operations
`catalog-endpoints.enable` / `catalog-endpoints.auto-default`.

---

## 10. Cross-repo

- **honua-console** — `/operate/catalogs` binds the §5 routes through `IHonuaCatalogDiscoveryClient`
  (`Honua.Console.Contracts/CatalogDiscoveryShims.cs`). The reads already bind (empty today); the projection
  makes them non-empty and the mutations make the toggles live. No console change required when the server
  side lands.
- **honua-server** — this document. Keep the §5 read DTOs stable (they are mirrored); evolve the projection
  freely behind them.
