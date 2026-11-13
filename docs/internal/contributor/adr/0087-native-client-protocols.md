# ADR-0087: Native client protocols are gRPC and cloud-native files; OGC API and GeoServices REST are compatibility surfaces

## Status
Proposed (2026-11-13). Founder direction. Builds on [ADR-0083](0083-cdn-is-the-shared-cache.md)
(the CDN is the shared cache) and [ADR-0085](0085-postgres-realtime-event-log.md) (cross-node
realtime on a PostgreSQL event log).

## Context
Honua Server answers the same data through several interfaces: GeoServices REST, OGC API (Features,
Tiles, Processes), the protobuf services defined in
[geospatial-grpc](https://github.com/honua-io/geospatial-grpc), and static cloud-native files
(PMTiles, GeoParquet, Cloud Optimized GeoTIFF) served through the CDN. The SDKs, samples, demos and
migration guidance did not say which one an application should build on, so examples led with
whichever interface their author knew, and customers moving to Honua were steered onto another
JSON-over-HTTP interface.

The interfaces differ in cost to the client and the server:

- Protobuf messages are compact binary and parse without text decoding. gRPC multiplexes calls over
  one HTTP/2 connection, streams in both directions, and generates typed clients from one contract.
- Cloud-native files are read with HTTP range requests from object storage through the CDN. The
  server does no work per request, every byte is cacheable, and a client fetches only the parts it
  needs.
- GeoServices REST and OGC API carry JSON (or GeoJSON) per request and need the server for every
  read. They are what existing desktop and third-party tools speak.

Clients run in three kinds of environment, with different transport options:

- Mobile and desktop applications (.NET MAUI, Swift, Kotlin, desktop .NET) speak native gRPC.
- Browsers cannot speak native gRPC: page scripts have no access to HTTP/2 trailers or framing.
  gRPC-Web carries the same protobuf contracts over browser HTTP (unary calls and server streaming,
  no client streaming), and ASP.NET Core supports it directly. The Connect protocol adds all
  streaming modes and cacheable GET reads over plain `fetch`, but has no ASP.NET Core server
  implementation today. WebTransport offers multiplexed streams and datagrams over HTTP/3 and is
  available in current Chromium, Firefox and Safari releases; its wire format is still an IETF draft.
- Browsers use HTTP/3 automatically when the server or CDN offers it, so gRPC-Web calls and range
  requests gain QUIC transport without application changes.

gRPC requests are POSTs, so a CDN does not cache them. Public, read-heavy data therefore belongs in
cloud-native files, not behind gRPC.

## Decision
1. **The native path for applications is gRPC plus cloud-native files.**
   - Authenticated, live and edited data (feature reads with access control, edits, sync, jobs)
     uses the geospatial-grpc services.
   - Public and read-heavy data (basemaps, published layers, bulk extracts) uses PMTiles for tiles,
     GeoParquet for vectors and Cloud Optimized GeoTIFF for rasters, read with range requests through
     the CDN. Private files are served through signed URLs and regenerated when their data changes.
2. **Transport by client:**
   - Mobile and desktop: native gRPC.
   - Browsers: gRPC-Web for data and edits.
   - Live collaboration and realtime feeds in browsers: WebTransport, falling back to WebSockets where
     WebTransport is unavailable, carrying the same protobuf messages over the ADR-0085 event log.
   - The Connect protocol is the candidate for CDN-cacheable protobuf reads once an ASP.NET Core
     implementation exists; adopting it needs its own ADR.
3. **OGC API and GeoServices REST are compatibility surfaces.** They stay complete and tested for
   third-party tools, existing desktop GIS and clients that cannot change during a migration. New
   Honua clients do not build on them, and Honua's own applications (Studio, Console, Collect, the
   SDKs) use the native path.
4. **Examples lead with the native path.** Every SDK getting-started page, sample, tutorial and demo
   shows gRPC and cloud-native files first. OGC API and GeoServices REST examples follow in sections
   labelled compatibility. Lead examples show the gRPC-Web setup for browsers, the cloud-native file
   path for public data, and signed URLs for private files.
5. **Migrations end on the native path.** A migrated customer's applications move to gRPC and
   cloud-native files; GeoServices REST serves only as the bridge while their existing clients are
   replaced.

## Consequences
- **SDKs:** each SDK (JS, .NET, Python, mobile) exposes a gRPC client for features, edits and sync,
  with gRPC-Web in the JS SDK, and readers for PMTiles, GeoParquet and COG. Gaps are filed as issues
  against the SDK that lacks them.
- **Field collection:** Honua Collect syncs GeoPackage changes over gRPC; third-party field clients
  use OGC API Features as a compatibility surface.
- **Server:** gRPC-Web is enabled on the gRPC services; cloud-native exports stay current with their
  layers and carry the ADR-0086 generation in their cache keys.
- **Docs:** the developer documentation is reordered with the native path first and the OGC API and
  GeoServices REST chapters marked compatibility.
- **Review rule:** a new example, sample or Honua application that builds on OGC API or GeoServices
  REST for data it could read natively cites this ADR with the reason, or the review rejects it.
- **Not decided here:** adopting the Connect protocol, and any change to the OGC API or GeoServices
  REST feature scope.
