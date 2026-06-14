# ADR-0034: GDAL/OGR honua Driver Delivery Strategy

## Status

Accepted

## Context

GDAL/OGR is the de-facto data-abstraction layer in the geospatial ecosystem. It is
the substrate underneath QGIS, R (`sf`, `terra`), Python (`fiona`, `rasterio`,
`pyogrio`), and the `ogr*` / `gdal_*` command-line tools. Without a GDAL driver
that can address honua endpoints by name (e.g. `HONUA:https://my-server`),
thousands of downstream tools fall back to either:

- The generic `OAPIF:` driver, which speaks vanilla OGC API Features but cannot
  carry honua-specific concerns (`Authorization: Bearer`, FeatureServer fallback,
  honua field-typing extensions, output CRS via `crs=` query parameter, JWT
  refresh, paging hints).
- Hand-crafted HTTP/`vsicurl` calls that re-implement the protocol in user code,
  which is brittle and fragments the experience across desktop GIS, R, Python,
  and CI scripts.

A first-class `HONUA:` OGR driver removes that friction in one move. The driver
itself lives outside `honua-server` — it is a C++ shared library loaded by GDAL —
but the *strategy* and the *server-side proof points* (CI E2E, fixture
documentation) belong here while the dedicated `honua-gdal` repository is
stood up.

This ADR captures the upstream-vs-out-of-tree decision so the implementation
work in the future `honua-gdal` repo (CT-807-A) and any contributor onboarding
have a single reference document.

## Decision

**Ship the honua GDAL driver as an out-of-tree GDAL plugin first. Pursue
upstream submission to OSGeo/gdal as a follow-on once the driver is stable
and external usage warrants the maintenance cost.**

The driver lives in a dedicated `honua-gdal` repository (Apache 2.0, in line
with the open client SDK / protocol policy in ADR-0024 § "Client SDK Licensing").
It is distributed as:

- a versioned GitHub release with `gdal_HONUA.so` / `gdal_HONUA.dll` /
  `gdal_HONUA.dylib` artifacts;
- a `pip install gdal-honua` wheel that drops the plugin into the GDAL plugin
  directory at install time;
- a `honua/gdal-driver` Docker image bundling GDAL + the plugin for repeatable
  CI and local validation.

The first release covers **OGC API Features (read; write where the collection
advertises the Transactions conformance class) and FeatureServer (read fallback)**.
Raster, WMS/WMTS, and OGC API Maps/Tiles are explicitly deferred to a follow-on
ticket once the vector pattern is proven.

honua-server itself ships **two artifacts** as part of this ADR's scope:

1. This ADR document.
2. A nightly E2E CI workflow (`.github/workflows/gdal-driver-e2e.yml`) that
   stands up honua-server with the existing `client-compat-v1.sql` seed and
   exercises `ogrinfo` + `ogr2ogr` against it. The workflow uses GDAL's built-in
   `OAPIF:` driver as a stand-in until `honua-gdal` ships, at which point the
   workflow swaps to `HONUA:` with the plugin installed.

No production code in honua-server changes for this ADR — the existing OGC API
Features endpoints already speak the protocol GDAL needs.

### Why out-of-tree first

| Concern | Upstream (OSGeo/gdal) | Out-of-tree plugin (honua-gdal) |
|---|---|---|
| **Distribution** | Ships in every GDAL release; zero install for end users | `pip install gdal-honua` or drop the `.so` in the GDAL plugin directory |
| **Time to first user** | 3–12 months: PR review, OSGeo coding-standards conformance, release cycle | Days to weeks: honua owns the release cadence |
| **honua-specific features** | Must follow GDAL-generic patterns; honua extensions (auth, paging hints, field typing) need community buy-in | Free to add `HONUA_*` open options, JWT refresh, FeatureServer fallback, honua field typing |
| **Maintenance burden** | Shared with OSGeo maintainers long-term | honua owns it; must track GDAL plugin ABI across major versions |
| **Auth integration** | Must follow `GDAL_HTTP_AUTH` / `GDAL_HTTP_BEARER` conventions exactly | Layers honua-specific `TOKEN` open option on top of GDAL's HTTP plumbing |
| **Risk of drift** | Hard to fix server-side regressions quickly — fix has to land in next GDAL release | Plugin can chase server-side fixes inside one release window |

The first three rows dominate. Out-of-tree keeps velocity high for the period
when honua-server itself is still iterating fast and lets the driver carry
honua-specific features that an upstream submission would have to argue for
case-by-case.

The upstream path is **not abandoned**. Once the driver has stabilized
(API surface frozen, no honua-specific knobs being added every quarter, sustained
external usage), an upstream submission removes the install-friction story for
casual users and shifts long-term maintenance to OSGeo. The ADR explicitly
preserves that path so a future ticket can cite this decision rather than
re-litigating it.

### Driver architecture (summary; full spec in `honua-gdal` repo)

- **Connection string**: `HONUA:<base-url>` with optional `,token=<bearer>` /
  `,page_size=<n>` modifiers. Standard GDAL open options: `BASE_URL`, `TOKEN`,
  `PAGE_SIZE` (default 1000), `PROTOCOL` (`auto` | `OAPIF` | `FeatureServer`),
  `CRS` (forwards to honua-server's `crs=` query parameter).
- **Layer enumeration**: prefer OGC API Features
  (`GET /ogc/features/collections`); fall back to FeatureServer
  (`GET /rest/services`) when the OAPIF landing page is absent.
- **Read path**: stream pages of GeoJSON via GDAL's `CPLHTTPFetch` + `CPLJSONObject`;
  follow the `next` link until exhausted. Do not buffer the dataset in memory.
- **Write path**: gated on the OGC API Features Transactions conformance class
  (`http://www.opengis.net/spec/ogcapi-features-4`). When absent, write methods
  return `OGRERR_UNSUPPORTED_OPERATION` cleanly rather than silently no-oping.
- **CRS**: read `storageCrs` from collection metadata, surface via GDAL's CRS
  API, and forward `CRS` open option to the `crs=` query parameter.
- **Auth**: `Authorization: Bearer <token>` header injected via `CPLHTTPFetch`
  options. Tokens never appear in error messages or debug output (mask before
  logging).
- **Dependencies**: GDAL's own helpers only — no external `libcurl` or
  `nlohmann/json` symbols, so the plugin does not collide with the host
  GDAL build.

### Driver scope deferred to `honua-gdal` (CT-807-A)

The driver source, unit tests, plugin packaging, and PyPI release are tracked
under a separate ticket against the `honua-gdal` repo. honua-server does not
host the C++ source; this ticket only owns the strategy ADR, the E2E proof, and
the seed-fixture documentation that the proof depends on.

## Consequences

### Easier

- The full GDAL ecosystem (QGIS, R, Python data-science, `ogr2ogr`, `gdal_translate`)
  can address honua endpoints by name as soon as the plugin ships.
- honua-specific features (auth, paging, FeatureServer fallback, output CRS)
  do not need OSGeo committee approval before they ship.
- honua-server's CI gains an end-to-end check that the OGC API Features surface
  responds correctly to GDAL-style access patterns. That check uses GDAL's
  built-in `OAPIF:` driver today and switches to `HONUA:` once the plugin
  ships, with no churn in the workflow shape.
- The QGIS plugin (#808) inherits the same connection abstraction, since QGIS
  consumes data through GDAL providers. Field-collection clients (mobile SDK,
  desktop tooling) get a uniform `HONUA:` URL story.
- `client-compat-v1.sql` is reused as the canonical seed. No new fixture has to
  be invented for the GDAL workflow, which keeps the certification surface
  aligned with the PyQGIS and Windows compatibility lanes.

### More difficult

- honua owns the long-term maintenance of the driver until it lands upstream.
  GDAL plugin ABI changes across major versions must be tracked; the
  `honua-gdal` CI matrix needs to exercise at least the supported GDAL versions
  (target floor: GDAL 3.6+).
- "Just works out of the box" is gated on the user installing the plugin
  (`pip install gdal-honua` or dropping the `.so` in the plugin directory).
  This is documented in the user-facing onboarding for honua-gdal but is
  weaker than the upstream-shipped story.
- The CI workflow uses the built-in `OAPIF:` driver as a stand-in for the
  acceptance check until `honua-gdal` ships. That proves honua-server speaks
  the right protocol to GDAL, but does not test honua-specific driver code paths
  (auth, paging hints, FeatureServer fallback). Those will be covered by the
  driver's own CI inside the `honua-gdal` repo.

### Re-evaluation triggers

This ADR should be revisited if any of the following occur:

- The driver collects more than ~3 honua-specific extensions that users
  explicitly request to be standardised across all OGR drivers (a signal the
  patterns belong upstream).
- Sustained external usage emerges and install friction becomes the dominant
  user complaint — at that point the upstream cost-benefit shifts.
- GDAL's plugin ABI changes break the driver more than once per major version,
  making out-of-tree maintenance untenable.

A re-evaluation should publish a follow-on ADR rather than editing this one in
place, so the decision history stays auditable.

## References

- Tracking issue: `honua-io/honua-server#807`
- Companion CI workflow: `.github/workflows/gdal-driver-e2e.yml`
- Seed fixture documentation: `tests/seed/README-gdal-driver-e2e.md`
- ADR-0024 (Open-Core Edition Model) — establishes the Apache 2.0 client SDK
  / protocol licensing posture that `honua-gdal` inherits.
- GDAL OGR vector driver developer documentation:
  https://gdal.org/tutorials/vector_api_tut.html
- GDAL plugin author guide:
  https://gdal.org/development/dev_practices.html
- OGC API Features Part 4 — Create, Replace, Update and Delete:
  https://docs.ogc.org/is/20-002r1/20-002r1.html
