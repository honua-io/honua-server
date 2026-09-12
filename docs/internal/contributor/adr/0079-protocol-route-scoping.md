# ADR-0079: A protocol's route is scoped by its own addressable unit

## Status

Accepted (2026-09)

Written after the asymmetry it describes cost a release. It records a rule the
server has followed since the classic OGC surfaces landed; nothing here changes
behaviour.

## Context

Honua serves ten-odd protocols, and their URL shapes do not look alike:

| Protocol | Route |
| --- | --- |
| OGC API Features / Coverages / Maps / Tiles / Records / Styles / Processes | `/ogc/{family}/collections/{collectionId}/…` |
| WMS | `/ogc/services/{serviceId}/wms` |
| WMTS | `/ogc/services/{serviceId}/wmts` |
| WCS | `/ogc/services/{serviceId}/wcs` |
| **WFS** | **`/wfs`** |
| GeoServices | `/rest/services/{serviceId}/{MapServer,FeatureServer,ImageServer}` |
| STAC | `/stac/…` |
| OData | `/odata/…` |
| SensorThings | `/sta/v1.1/…` |
| Vector tiles | `/tiles/{layerId}/…` |
| Scenes | `/api/scenes/{sceneId}` |

WFS is the one that reads as a mistake: three of the four classic OGC protocols
are scoped under `/ogc/services/{serviceId}/` and the fourth is not. It also has
no GeoServices alias, where WMS, WMTS and WCS each have one.

This is not documented anywhere, so it is guessed. In September 2026 the
honua-sdk-dotnet release pipeline guessed it. Its ephemeral staging job read the
WFS type name from `/ogc/services/{serviceId}/wfs`, which 404s. The fetch was
configured to fail hard on HTTP errors and stay quiet, so a 404 surfaced as a
transport-style failure with an empty body, and the job read as *the server did
not come up* rather than *that route does not exist*. The job died before
`Publish Packages`, and `dotnet-sdk-v1.6.1` and `v1.6.2` sat tagged and
unpublished until someone re-read the route table (honua-sdk-dotnet#355).

The cost was not the 404. It was that a wrong URL is indistinguishable from a
broken server, and there was no written rule to check the URL against.

## Decision

**A protocol's route carries exactly the scope its own specification makes the
addressable unit — no more, and no less.**

Applying it:

- **WMS, WMTS, WCS are scoped by service.** They render a *composition*: which
  layers, in what order, over what extent. "Service" is the name of that
  composition, and there is no meaningful `GetMap` without it. The scope is in
  the path because the request is about the thing the path names.

- **WFS is not scoped.** It returns features, and `TYPENAMES` already identifies
  the layer globally. A service segment would be redundant, and worse, it would
  make one feature addressable by more than one URL — two cache keys, two
  permalinks, two things to keep in step.

- **OData, STAC and SensorThings are not scoped** for the same reason: each
  addresses its records through the request (entity set, search body, entity
  path) rather than through the path prefix.

- **Vector tiles and scenes are scoped by layer and scene** — the unit a client
  actually fetches.

- **The rule discriminates by unit, not by resemblance.** Native vector tiles
  are at `/tiles/{layerId}/{z}/{x}/{y}.mvt` and PMTiles at
  `/api/v1/tiles/pmtiles/{artifactId}`. Both are "tiles", and the superficial
  read is that one of them is in the wrong place. They are addressed
  differently: a vector tile is a live view of a **layer**, requested per
  z/x/y; a PMTiles archive is a published **artifact**, retrieved whole. Two
  units, two scopes, two roots. Resembling each other is not a reason to share
  a prefix.

- **GeoServices aliases exist only where Esri has one.** `MapServer` exposes
  WMS/WMTS and `ImageServer` exposes WCS/WMTS because Esri's own products do;
  `FeatureServer` has no `WFS` alias here because it has none there. The alias
  surface is a compatibility surface, and it copies rather than extends.

The corollary, which is the part worth enforcing: **adding a path scope that the
protocol does not need is a defect, not a convenience.** Every redundant segment
is a second URL for the same resource.

## Consequences

- A new protocol's route shape is decided by reading its specification for the
  addressable unit, not by matching whichever neighbouring protocol looks
  closest.
- WFS stays at `/wfs`. Adding `/ogc/services/{serviceId}/wfs` as a convenience
  would be a regression under this ADR even though it would have made the
  honua-sdk-dotnet job pass.
- `docs/reference/protocols/wms-wfs-wcs-wmts.md` states the rule for readers,
  including the 404-as-exit-22 shape, so the next person debugging an empty WFS
  response rules out the URL first.
- Callers that construct protocol URLs by pattern rather than from a documented
  table are relying on an inference this ADR declines to make true. The route
  table is the contract.

## Alternatives considered

**Add a service-scoped WFS alias for symmetry.** It would have prevented the
failure and it is what the surrounding routes lead you to expect. Rejected: it
buys consistency of *appearance* at the cost of consistency of *meaning*, and
duplicate addressing for the same feature is a real cost — cache keys,
permalinks, access rules and audit records all have to agree across both forms
forever, to spare readers one lookup.

**Move WMS/WMTS/WCS to unscoped roots instead.** Not possible; those requests
are meaningless without naming the composition to render.

**Leave it undocumented and fix callers as they break.** That is the status quo
that produced this ADR, and it had already cost two unpublished releases.
