# SDK Standards Coverage by Language

This page is the server-owned SDK standards coverage ledger for
[honua-server#994](https://github.com/honua-io/honua-server/issues/994). It is
for website, release-note, and interoperability copy that needs to describe how
first-party SDKs line up with Honua Server protocol surfaces.

Use this page together with:

- [Server + SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md) for
  server/SDK version support and live compatibility evidence
- [Protocols Overview](../gis/STANDARDS_APIS.md) for server-side standards and
  protocol adapter support
- [Metadata and Catalog Parity Matrix](metadata-catalog-parity-matrix.md) for
  metadata/catalog endpoint parity targets
- [Mobile SDK Roadmap](mobile-sdk-roadmap.md) for MAUI/offline field workflows

## Claim Rules

- Server standards support means Honua Server exposes and governs the protocol
  endpoint. It does not mean every first-party SDK has a language-native wrapper.
- SDK coverage means there is a language-native convenience path or a tracked
  first-party client target for that language. Standard HTTP/protocol access
  remains available even when a language-specific wrapper is not claimed.
- Marketing and site copy should say that Honua Server keeps standards
  endpoints available while SDKs provide language-native paths for the surfaces
  developers actually use in each environment.
- Do not claim that every SDK supports every server standard.
- Release-grade SDK claims must point to a pinned SDK line, release note, or
  compatibility artifact. The `sdk-server-compatibility.yml` evidence records
  the exact `protocol_surfaces_by_sdk` exercised for a server/SDK cell.

## Status Labels

| Label | Meaning |
|---|---|
| Supported convenience | First-party SDK path may be cited for a pinned SDK line when release notes or compatibility evidence cover that package. |
| Targeted convenience | First-party SDK work is scoped or tracked, but it is not a shipping claim until the SDK repo releases it and evidence names it. |
| Generic protocol | The server standard is available, but the language should use ordinary HTTP, generated OpenAPI/gRPC code, or ecosystem clients rather than a Honua-specific wrapper claim. |
| Deferred | No first-party SDK client should be claimed until a concrete product workflow is linked. |

## Evidence Boundary

This page is positioning and backlog guidance, not release evidence by itself.
The current server-owned SDK compatibility lane records these exercised surfaces:

| SDK | Surfaces currently exercised by server compatibility evidence |
|---|---|
| JavaScript / TypeScript | `control-plane-admin`, `geoservices-catalog`, `feature-server`, `ogc-api-features`, `migration-scan`, `arcgis-import`, `geoserver-dry-run`, `migration-evidence` |
| Python | `control-plane-admin`, `platform-http`, `geoservices-catalog`, `feature-server` |
| .NET | `control-plane-admin`, `migration-scan` |

The same artifact also records migration automation surfaces:
`migration-scan`, `arcgis-import`, `geoserver-dry-run`, and
`migration-evidence`. These are visible in `protocol_surfaces_by_sdk` and carry
per-SDK `migration_automation_by_sdk` status. They are release claims only when
the relevant SDK cell records `supported` with an uploaded artifact path and the
SDK package version is published; `unsupported` entries identify remaining
public SDK API/command gaps.

Rows below can guide copy and SDK backlog decisions, but they become release
claims only when the relevant SDK package, release note, or compatibility
artifact names the surface.

## Coverage Matrix

| Server protocol surface | Server support | JavaScript / TypeScript SDK | Python SDK | .NET SDK |
|---|---|---|---|---|
| Admin/control-plane typed APIs (`/api/v1/admin`) | Supported server management API. | Supported convenience for web apps, app-builder flows, and automation. | Supported convenience for automation, ETL, and data operations. | Supported convenience for C# services, admin tooling, and automation. |
| Geospatial gRPC (`geospatial.v1`) | Supported server protocol; protobuf ownership stays in `honua-io/geospatial-grpc`. | Generic protocol unless a web/gRPC-Web client is released for a concrete workflow. | Generic protocol unless a Python generated client is released for a concrete workflow. | Targeted convenience for C# services and automation. Do not claim feature gRPC interop until the .NET package path and live evidence match the server's `geospatial.v1` methods. |
| GeoServices REST FeatureServer | Supported feature metadata, query, edit, attachment, and related-record surface. | Supported convenience for web/app service discovery, layer metadata, query, and edit workflows. | Supported convenience for analysis, ETL, AI/data, metadata, and feature query workflows. | Supported or targeted convenience where `Honua.Sdk.GeoServices` covers backend FeatureServer workflows. Cite only the operations released and evidenced for the pinned SDK line. |
| GeoServices REST MapServer and ImageServer | Supported map, image, identify, legend, export, tile, and raster metadata surfaces. | Supported convenience for browser/web map display and raster/image workflows where released. | Supported convenience for analysis, ETL, validation, export, and raster/image workflows where released. | Backend-only target. No .NET map-display claim unless a .NET display component exists. Raster/image clients require a concrete backend workflow before implementation. |
| OGC API Features | Supported standards feature API. | Supported convenience for web/app feature discovery, query, and map-source workflows. | Supported convenience for analysis, ETL, AI/data, and standards feature workflows. | Supported or targeted convenience through `Honua.Sdk.OgcFeatures` for services, automation, field, and offline workflows. No map-display claim by itself. |
| OGC API Records | Planned server catalog surface; final route and query contract are tracked by [honua-server#952](https://github.com/honua-io/honua-server/issues/952). | Targeted convenience after the server contract is pinned. | Targeted convenience after the server contract is pinned. | Targeted convenience after the server contract is pinned. |
| OGC API Maps, OGC API Tiles, MVT, TileJSON, and Terrain-RGB tiles | Supported rendered-map, tile, vector-tile, TileJSON, and terrain tile surfaces. | Supported convenience for browser map display and tile consumption where released. | Supported convenience for validation, cache inspection, data workflows, and ecosystem client integration where released. | Deferred for generic map-display parity. Only track backend raster/tile clients for named workflows such as static rendering, endpoint validation, tile cache audit/prewarm, secured proxying, thumbnails, or offline package generation. |
| Classic OGC WFS | Supported standards feature service. | Supported convenience or generic standards-client path for web/app feature workflows where released. | Supported convenience for analysis, ETL, AI/data, and standards feature workflows where released. | Supported or targeted backend feature-interchange convenience where released and evidenced. |
| Classic OGC WMS and WMTS | Supported standards map image and tile services. | Supported convenience for web map display and tile/image consumption where released. | Supported convenience for validation, rendering/export checks, and ecosystem client workflows where released. | Deferred for generic map-display parity. No WMS/WMTS .NET display claim without a .NET display component and release evidence. |
| WCS and OGC API Coverages | Supported coverage/raster standards surfaces. | Generic protocol or targeted convenience for browser fetch/preview workflows where released. | Supported convenience for analysis, ETL, validation, and raster/coverage workflows where released. | Deferred unless a backend coverage workflow is linked, such as report rendering, thumbnail generation, validation, or offline package creation. |
| STAC API | Supported catalog, collection, item, and search surface. | Supported convenience for catalog/search and app-builder workflows where released. | Supported convenience for analysis, ETL, AI/data, and asset catalog/search workflows where released. | Supported or targeted convenience for backend catalog/search workflows where released and evidenced. |
| Open Data / DCAT | Supported public page/list reads, bounded DCAT/data.json catalog export, Schema.org preview, and admin Console publication controls. | Targeted convenience for Console Share and browser open-data workflows; no release claim until SDK evidence names the surface. | Targeted convenience for automation and catalog-harvest workflows; generic HTTP remains valid. | Targeted convenience for admin Console Share and backend catalog workflows; distinguish Console STAC status readbacks from public STAC Collection documents. |
| OData v4 | Supported BI/query/CRUD surface with spatial functions. | Supported convenience or generic OData client path where released. | Supported convenience for analysis, ETL, BI-adjacent, and data workflow clients where released. | Generic OData ecosystem client path by default; add Honua-specific convenience only for a concrete backend workflow. |
| Process/job APIs (OGC API Processes, GeoServices GPServer, MCP, gRPC ProcessService) | Supported through the canonical process/job runtime where each adapter is implemented. | Supported convenience for MCP and app-builder/operator workflows where released. | Targeted convenience for AI/data and automation workflows where released. | Targeted convenience for C# service automation and typed gRPC process workflows where released and evidenced. |
| Migration automation (`migration-scan`, ArcGIS import, GeoServer dry run, parity evidence artifacts) | Supported server contracts for source inventory scans, ArcGIS/GeoServer import endpoints, and stable migration artifact models. | Server compatibility smoke exercises all four surfaces through public JS SDK APIs; claim release support only for published SDK versions with `supported` artifact evidence. | Targeted convenience tracked by `honua-sdk-python#49`; no release claim while compatibility evidence records `unsupported`. | Server compatibility smoke exercises `migration-scan` through `HonuaAdminClient.ScanMigrationSourceAsync`; ArcGIS import, GeoServer dry run, and artifact-bundle evidence remain targeted convenience tracked by `honua-sdk-dotnet#134`. |
| Field/offline workflows | Server-owned sync and protocol contracts support mobile/offline clients. | Generic protocol unless a web/offline package explicitly covers the workflow. | Generic protocol unless an offline/data package explicitly covers the workflow. | Supported or targeted through the MAUI/offline roadmap and shared .NET SDK packages where released. |

## Language Positioning

JavaScript/TypeScript can be described as the web/app SDK lane: typed APIs,
GeoServices, OGC API surfaces, WFS, WMS, WMTS, STAC, OData, tile, and map
display support may be cited only for the surfaces present in the pinned SDK
line.

Python can be described as the analysis, ETL, AI, and data-workflow SDK lane:
typed APIs, OGC API surfaces, WFS, WMS, WMTS, STAC, OData, raster/coverage, and
related protocol clients may be cited only for the surfaces present in the
pinned SDK line.

.NET should be described as the C# services, automation, admin/control-plane,
typed gRPC, FeatureServer, WFS, OGC API Features/Records, STAC, and
field/offline workflow lane where those packages are released and evidenced.
Do not claim .NET map-display support unless there is a .NET display component
with its own product scope and evidence.

## .NET Raster, Tile, and Map Display Gate

.NET WMS, WMTS, OGC API Maps, OGC API Tiles, TileJSON, MVT, Terrain-RGB,
ImageServer, WCS, and OGC API Coverages clients are not parity work for map
display by default. Open or cite those clients only when the issue names a
backend workflow such as:

- static report rendering
- endpoint validation or conformance probing
- tile cache audit, prewarm, expiry, or quota checks
- secured proxying
- thumbnail or preview generation
- offline package generation
- backend export, ETL, or data-quality validation

A .NET UI/display claim requires a separate .NET display component, such as a
MAUI, WPF, Blazor, or partner map viewer, plus evidence that it consumes the
specific protocol surface.
