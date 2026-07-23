# OGC conformance

Honua Server passes every OGC CITE suite it runs on `trunk` at 100%. The
canonical per-suite pass rates, evidence-run links, and re-grading guidance
live in [`docs/cite-status.md`](../../cite-status.md) — see that page for the
current numbers; they are not restated here so there is nothing for this page
to fall out of sync with.

## CITE pass rates per suite

See the "Current Per-Protocol Status" table in
[`docs/cite-status.md`](../../cite-status.md#current-per-protocol-status) for
the canonical passed/total counts and pass rate per suite, including the WFS
2.0 explicit transactional slice.

## What conformance means per protocol

- **OGC API Features** — Part 1 Core, Part 2 CRS, and Part 3 Filtering pass against
  the seeded fixture. Any spec-compliant OAPIF client (QGIS, GDAL/OGR, OpenLayers)
  can discover collections, page items, filter with CQL2, and request alternate CRSs.
- **OGC API Tiles** — vector and raster tile delivery against the seeded tile matrix
  sets; 7 conformance classes.
- **WFS 1.0 / 1.1 / 2.0** — the `basic` profile: capabilities, DescribeFeatureType,
  GetFeature, spatial/temporal filters, paging, transactions, and managed stored
  queries. Locking, feature versioning, and spatial joins are not advertised and not
  in scope. WFS 1.0/1.1 are read-only compatibility surfaces.
- **WMS 1.3 / WMTS 1.0 / WCS 2.0** — the official ETS default/core profiles:
  GetCapabilities, GetMap/GetTile/GetCoverage, and GetFeatureInfo behave as legacy
  OGC clients expect. WMS 1.1.1 is also served but has no CITE evidence yet (see
  [known limitations](clients.md#known-limitations)).
- **GeoPackage 1.2 / GML 3.2 / KML 2.2** — format-level conformance: exported
  documents validate against the official suites for the classes Honua's exports
  exercise (`applicable` profile; out-of-scope optional class families are explained
  per suite in [`docs/cite-status.md`](../../cite-status.md)).

Some OGC API surfaces have no official CITE executable test suite yet — Styles,
Maps, Processes, Coverages, and Records. They are shipped as conformant adapters
proven by targeted integration tests plus accurate `/conformance` declarations,
and are not part of the 952/952 count. See the
[OGC API surfaces without an official CITE ETS](../../cite-status.md#ogc-api-surfaces-without-an-official-cite-ets)
section of the CITE status page.

## Runtime conformance endpoints

Each OGC API surface declares its implemented conformance classes at runtime
(paths verified against `src/Honua.Server/EndpointRegistry.cs`):

| Surface | Endpoint |
|---|---|
| OGC API Features | `GET /ogc/features/conformance` |
| OGC API Tiles | `GET /ogc/tiles/conformance` |
| OGC API Maps | `GET /ogc/maps/conformance` |
| OGC API Processes | `GET /ogc/processes/conformance` |
| OGC API Coverages | `GET /ogc/coverages/conformance` |
| OGC API Records | `GET /ogc/records/conformance` |
| OGC API Styles | `GET /ogc/styles/conformance` |
| STAC API | `GET /stac/conformance` |

The OpenAPI specs under `src/Honua.Server/*-openapi.json` carry the same CITE
totals in an `x-honua-cite-compliance` vendor extension on the `info` object.
An architecture test (`CiteStatusComplianceDriftTests`) fails the build if any
of those five files disagree with `docs/cite-status.md`.

## API versioning in one paragraph

Honua exposes two separately versioned API tiers. The admin/control plane is
path-versioned (`/api/v1/...`) with a documented deprecation lifecycle and
CI-gated OpenAPI contract governance — see
[versioning and support](../versioning-and-support.md). Standards APIs (WFS, WMS,
OGC API Features, and the rest) are versioned by the underlying OGC standard;
Honua does not add its own version axis, and conformance is asserted via the CITE
pass rates above. The `Geospatial.V1` gRPC surface is a stable wire contract —
breaking changes require a new major package (`Geospatial.V2`); see the
[gRPC versioning policy](../protocols/grpc.md).

## Related

- [`docs/cite-status.md`](../../cite-status.md) — authoritative CITE snapshot (source of truth for the numbers above).
- [CITE conformance evidence](../../internal/contributor/ogc-cite-conformance-evidence.md) — stable, website-linkable evidence-run narrative; see `cite-status.md` for the numbers.
- [CITE runbook](../../internal/contributor/cite-runbook.md) — per-suite scope, scripts, and workflow files.
- [OGC certification path](../../internal/contributor/ogc-certification-path.md) — formal certification posture.
- [Supported clients](clients.md) — which clients consume these protocols, with tested versions.
- [Protocols overview](../../concepts/protocols.md) — every protocol Honua speaks.
