# Client Certification Fixture Contract (2026.1)

This is the prose half of the frozen 2026.1 client-certification fixture, server-configuration,
and auth-policy contract. Its machine-readable half is
[`docs/gis/data/client-certification-fixture.v1.json`](data/client-certification-fixture.v1.json),
and the two are paired 1:1: `CanonicalFixtureManifestTests` in
`tests/dotnet/Honua.Architecture.Tests/CanonicalFixtureManifestTests.cs` fails the build if this
document and that manifest disagree about the case-id set, the auth-profile set, or the published
revisions.

Owner: [honua-server#3393](https://github.com/honua-io/honua-server/issues/3393).
Expansion (explicitly out of scope here):
[honua-server#3435](https://github.com/honua-io/honua-server/issues/3435).

## 1. What is frozen

| Revision | Value |
|---|---|
| `fixtureRevision` | `sha256:cf41453e85cedd4dba536ae965aefc3b8d73f6bd23992aebeeb23fbe4aa131fb` |
| `serverConfigRevision` | `sha256:d4b2189558e492204909a75ccc71054741042fa7974d600e82a7a0ee0213435a` |
| `authPolicyRevision` | `sha256:9068f9d255f917b14ba5cff7c9a9defc268f69892e7605923f9d3f5dc3f5fea9` |

Any change to an input file changes at least one of these values. Publishing a changed input
without republishing the manifest is a build failure, not a warning.

### Digest algorithms

**`honua.file-digest/v1`** — SHA-256 over the raw bytes of one file, written as `sha256:` plus 64
lowercase hex characters. Reproduce with `sha256sum <path>`.

**`honua.input-set-digest/v1`** — the composite over a role's input set, used for
`fixtureRevision` and `serverConfigRevision`:

1. Select every `inputs[]` entry whose `role` is the target role.
2. Sort them ascending by the UTF-8 bytes of `inputs[].path`.
3. Render one line per entry: the 64 lowercase hex characters of its digest, two ASCII spaces, the
   repo-relative POSIX path, one LF. This is byte-identical to GNU `sha256sum` output.
4. Concatenate the lines as UTF-8 and SHA-256 the result.

Because step 3 reproduces `sha256sum` output exactly, the whole algorithm is reproducible by hand:

```console
$ LC_ALL=C sha256sum docker/client-compat/seed/run.sh tests/seed/apply-yaml-seed.sh \
    tests/seed/browser-compat.yaml tests/seed/client-compat-auth-wave1.yaml \
    tests/seed/client-compat-v1.sql tests/seed/portal-compat.yaml \
    | sha256sum
cf41453e85cedd4dba536ae965aefc3b8d73f6bd23992aebeeb23fbe4aa131fb  -
$ LC_ALL=C sha256sum tests/config/client-compat-server-v1.json | sha256sum
d4b2189558e492204909a75ccc71054741042fa7974d600e82a7a0ee0213435a  -
```

**`honua.canonical-json-digest/v1`** — SHA-256 over the canonical JSON serialization of the
manifest's `authPolicy` object: members sorted ascending by the code points of their names, no
insignificant whitespace, `,` and `:` separators, strings escaped only for `\`, `"`, and the JSON
control escapes, non-ASCII emitted literally as UTF-8. Numbers are forbidden anywhere inside
`authPolicy`, so the digest never depends on a numeric formatter. The auth policy is declared,
not file-backed, which is why it is digested from its declaration rather than from a file.

### Inputs

| Path | Role |
|---|---|
| `tests/seed/client-compat-v1.sql` | fixture |
| `tests/seed/browser-compat.yaml` | fixture |
| `tests/seed/portal-compat.yaml` | fixture |
| `tests/seed/apply-yaml-seed.sh` | fixture |
| `docker/client-compat/seed/run.sh` | fixture |
| `tests/config/client-compat-server-v1.json` | server-config |

`docker/client-compat/seed/run.sh` is a fixture input because it defines *which* seed files are
applied and in what order; adding a fixture input necessarily edits it and therefore moves
`fixtureRevision`.

`docker/client-compat/compose.yml` is deliberately **not** digested: it carries the runtime auth
settings alongside every per-lane service definition, so every new lane would churn
`serverConfigRevision` for reasons that have nothing to do with server configuration. The
auth-relevant settings it carries are instead pinned by name inside `authPolicy` and are therefore
covered by `authPolicyRevision` (gap `runtime-composition-not-content-addressed`).

`tests/python/shared/canonical_fixture.py` is referenced but not digested: it is compared symbol by
symbol, so a comment edit does not force a manifest revision while a value change still fails.

## 2. Stable identities

| Service | Role | Layers | Source |
|---|---|---|---|
| `test_service` | canonical vector | `0` (Point, 10 features) | `tests/seed/client-compat-v1.sql` |
| `browser_compat` | render and raster | `2000` Point + raster, `2001` LineString, `2002` Polygon | `tests/seed/browser-compat.yaml` |
| `portal_public` / `portal_org` / `portal_private` | authorization ladder | `3000` / `3001` / `3002` | `tests/seed/portal-compat.yaml` |

Ordering rules:

- `features.objectid` is a `BIGSERIAL`. PostgreSQL evaluates the seed's `VALUES` scan in
  declaration order, so on a fresh database the ids run `alpha` = 1 through `lambda` = 10, and the
  insert is guarded by `WHERE NOT EXISTS` so re-applying the seed never renumbers or duplicates
  rows. Lanes must still address features by attribute and treat `objectid` as stable-but-opaque;
  no case asserts a literal id value.
- Default and stable feature order is ascending `objectid`. Deterministic paging requires an
  explicit sort on it; page size 3 partitions the ten features as 3 / 3 / 3 / 1.
- The Metadata v2 snapshot is deterministic: `generatedAt` is the frozen literal
  `2024-01-01T00:00:00Z` and every collection is aggregated in a declared sort order, so the same
  inputs reproduce the same document and the same ETag.
- Portal item ids are derived at request time and are **not** stable literals. Clients discover
  them through `/sharing/rest/search`; the stable identity is the service name and its projected
  access tier.

Protocol projections of the canonical service and layer are listed in the manifest's
`protocolProjections`: OGC API Features collection `0`, WFS type name `honua:test_layer`, WMS and
WMTS layer identity `0`, OGC API Tiles and Maps collections, FeatureServer and MapServer layer
index `0`, STAC collection `0`, OData `Layers(0)` / `Features(0)`, and the Portal access tiers.

## 3. Vector seed coverage, honestly

Covered: point geometry, null geometry (one of ten rows), null attributes, numeric, boolean, date,
time, timestamp, UUID and array attributes, a stable attribute filter (`status = 'active'` selects
exactly five rows), a stable spatial filter (the subset bbox selects exactly the first three
points), and stable sort and pagination.

Not covered, recorded as gaps rather than claimed:

| Gap | Why |
|---|---|
| `multipart-geometry-absent` | No MultiPoint, MultiLineString, or MultiPolygon geometry exists in any input. |
| `polygon-hole-geometry-absent` | No polygon with an interior ring is seeded. |
| `unicode-attribute-values-absent` | Every seeded attribute value is ASCII; the only non-ASCII byte in the seed is inside a SQL comment. |
| `line-and-polygon-not-on-canonical-service` | Line and polygon coverage lives on `browser_compat`, whose layers carry only `objectid`, `name`, and `shape`. |
| `edit-path-uncertified` | The OWSLib lane certifies WFS-T insert/update/delete on scratch layers 10-12, but no lane mutates canonical layer 0 and the other advertised Create/Update/Delete/Sync protocol surfaces remain uncertified. |

Supporting fixtures: the deterministic raster (64×64, single 8BUI band, every pixel 180) and the
render geometry layers are realized on `browser_compat`; STAC is realized on the canonical service
with `eo:cloud_cover` as a real numeric queryable; a GP buffer fixture is **not required** for
2026.1 because no lane binds the GPServer surface; map and dashboard inputs are limited to Portal
service items (`portal-map-and-dashboard-items-absent`). Raster on the canonical service
(`raster-absent-on-canonical-service`) and stored style documents (`style-resources-absent`) are
gaps.

## 4. Auth profiles

The control plane authenticates with an **API key**, not HTTP Basic and not a bearer login flow:
the header is `X-API-Key` and its value is the server's `HONUA_ADMIN_PASSWORD`
(`ApiKeyAuthenticationHandler`). A 401 carries `WWW-Authenticate: ApiKey`. The development auth
bypass is disabled because the client-compat stack runs `ASPNETCORE_ENVIRONMENT=Production`, so the
server logs that the bypass is blocked rather than admitting an unauthenticated caller. The same
`admin` principal and secret are the named-user pair the Portal facade accepts at
`/sharing/rest/generateToken`, with `Authentication__PortalToken__RequireHttps=false` set because
the test network is HTTP-only.

The manifest names environment variables, headers, principals, and roles. The one literal it
carries is the well-known non-production bootstrap key that already appears in the compose file and
the shared Python projection; no production credential appears anywhere.

Roles: the bootstrap principal is projected with role `admin` only
(`AdminPortalCredentialVerifier`), and it carries no permission claims, so `AdminApiKeyPermission`
treats it as full admin. The role `portal-admin` that `portal_private` requires is granted to no
principal in this fixture, which is what makes the private tier the denial arm of the ladder.

<!-- auth-profiles:begin -->

| Profile | Expectation | Status | Realized by |
|---|---|---|---|
| `anonymous` | allow on public surfaces, deny elsewhere | realized | the three seeds plus the client-compat server config |
| `valid-credential` | allow | realized | `X-API-Key` on the admin probe path; `generateToken` for the Portal lane |
| `invalid-credential` | deny | realized | a wrong key yields 401 with the ApiKey challenge; `generateToken` yields the Esri error envelope |
| `expired-credential` | deny | gap (`expired-credential-unrealized`) | no fixture mints a genuinely expired credential |
| `insufficient-role-or-scope` | deny | realized but not asserted (`insufficient-role-assertion-absent`) | `portal_private` requires `portal-admin`, which no principal holds |
| `cross-tenant-denial` | deny | gap (`cross-tenant-denial-unrealized`) | the stack seeds a single tenant |
| `separate-proposer-approver` | allow only with two principals | gap (`proposer-approver-unrealized`) | only the `admin` principal is provisioned |
| `licensed-entitlement` | allow with entitlement | gap (`licensed-entitlement-unrealized-locally`) | realizable only on the licensed release overlay |

<!-- auth-profiles:end -->

## 5. Profiles and overlays

- **local-unlicensed** (PR and nightly): source-built server on the client-compat docker network,
  PostGIS 16-3.4, Redis, HTTP only. `CERT-CONN-02` is not applicable here
  (`tls-not-exercised-in-local-profile`).
- **exact-candidate** (release): the exact candidate image behind TLS, same seeds applied by the
  same applier. Additive only — it may pin the image digest and enable TLS and may not change any
  seeded identity, count, extent, or access policy. All three revisions carry through unchanged; a
  different value is drift, not an overlay. `CERT-CONN-02` becomes applicable.
- **licensed** (release): the entitlement-bound overlay for ArcGIS Pro/arcpy, Excel, Power BI, and
  Tableau. It binds the same identities and the same auth policy plus an entitlement; it may not
  substitute its own seed for the canonical fixture.

## 6. Case and scenario-facet identities

Every case id below carries a stable scenario-facet id in the manifest's `cases` array, and every
active lane binds each case as applicable, as a governed not-applicable, or as an extension.

**Common core (24).** Connection `CERT-CONN-01`, `CERT-CONN-02`; auth `CERT-AUTH-01`,
`CERT-AUTH-02`; discovery `CERT-DISC-01`, `CERT-DISC-02`; schema `CERT-SCHM-01`, `CERT-SCHM-02`;
query and filter `CERT-QFLT-01`, `CERT-QFLT-02`; pagination `CERT-PAGE-01`, `CERT-PAGE-02`;
geometry fidelity `CERT-GEOM-01`, `CERT-GEOM-02`; error handling `CERT-ERRH-01`, `CERT-ERRH-02`;
rendering and style `CERT-RNDR-01`, `CERT-RNDR-02`, `CERT-RNDR-SYM-01`, `CERT-RNDR-LIN-01`,
`CERT-RNDR-FIL-01`, `CERT-RNDR-LBL-01`, `CERT-RNDR-SPR-01`, `CERT-RNDR-URL-01`.

**Portal facade (13).** `CERT-PRTL-INFO-01`, `CERT-PRTL-SELF-01`, `CERT-PRTL-SELF-02`,
`CERT-PRTL-SRCH-01`, `CERT-PRTL-ITEM-01`, `CERT-PRTL-RBAC-01`, `CERT-PRTL-TOKN-01`,
`CERT-PRTL-TOKN-02`, `CERT-PRTL-AUTH-01`, `CERT-PRTL-OAUTH-01`, `CERT-PRTL-OAUTH-02`,
`CERT-PRTL-COMM-01`, `CERT-PRTL-COMM-02`.

**Lane extensions (23).** JavaScript: `JS-EXT-01`, `JS-EXT-02`, `JS-EXT-OL-COLL-01`,
`JS-EXT-OL-ITEMTYPE-01`, `JS-EXT-OL-ITEMS-01`, `JS-EXT-OL-GEOJSON-01`, `JS-EXT-OL-GEOJSON-02`,
`JS-EXT-TILES-DISC-01`, `JS-EXT-TILES-DISC-02`, `JS-EXT-TILES-SCHM-01`, `JS-EXT-OGC-MAPS-01`.
Cesium: `JS-CES-IMG-01`, `JS-CES-TILE-01`. Esri Leaflet: `EL-EXT-01`, `EL-EXT-02`, `EL-EXT-03`,
`EL-EXT-04`. Desktop: `DSK-EXT-01`, `DSK-EXT-02`. CLI: `CLI-EXT-01`, `CLI-EXT-02`. BI:
`BI-EXT-01`, `BI-EXT-02`.

### Lane bindings

| Lane | State | Protocol bindings |
|---|---|---|
| `js` | active | `ogc-features` on a discovered collection (configured to `browser_compat` 2000); `wfs` on a discovered feature type; `wms`, `wmts`, `ogc-maps`, `mvt` on `browser_compat` |
| `js-cesium` | active | `wms`, `wmts`, `ogc-tiles`, `ogc-maps` on `browser_compat` |
| `desktop-qgis` | active | `ogc-features` and `wfs` on the canonical service |
| `cli` | active | `ogc-features` and `wfs` on the canonical service |
| `arcgis-stub` | active | `featureserver` and `mapserver` on `browser_compat` 2000; `portal` on the tier ladder |
| `py-geopandas` | landing | `ogc-features` and `wfs` on the canonical service |
| `py-owslib` | landing | `ogc-features` and `wfs` on the canonical service; `wms` and `wmts` on `browser_compat` |
| `duckdb` | landing | `ogc-features` on the canonical service |
| `r-sf` | landing | `ogc-features` and `wfs` on the canonical service |
| `py-pystac` | landing | `stac` on the canonical service |

Six cases are bound by no 2026.1 lane and are governed as unbound in the manifest: the four Esri
Leaflet extensions (a documented browser sub-lane with no registered lane id or committed baseline)
and the two BI extensions (planned, licensed, release-tier lanes).

Two of the five active lanes do not bind the canonical vector identity today, and the manifest says
so rather than pretending otherwise. The `js` lane is configured onto `browser_compat` layer 2000
and still falls back to the first advertised collection, and its WFS binding picks the first feature
type that returns features (`js-lane-identity-not-pinned`); the `arcgis-stub` lane is pointed at
`browser_compat` 2000 by `ARCGIS_STUB_SERVICE_NAME` and `ARCGIS_STUB_LAYER_ID`. That layer carries
three point features and only `objectid`, `name`, and `shape`, so those lanes exercise neither the
canonical attribute-type spread nor the null geometry nor the ten-feature pagination shape
(`active-lanes-bind-render-fixture-not-canonical-vector`). Only `desktop-qgis` and `cli` bind
`test_service` layer `0` today.

## 7. Fail-closed rules

- **Applicable but unexecuted fails closed.** A case an active lane declares applicable and does
  not execute is emitted with status `skip` and treated as a failure by the strict baseline diff.
  It is never omitted from the envelope and never counted as a pass.
- **Unsupported requires a governed reason.** A case a lane cannot structurally execute is emitted
  as `not-applicable` and must carry a reason drawn from the manifest's `notApplicableReasons`
  vocabulary. An ungoverned or empty reason fails the gate.
- **Placeholder baselines are rejected.** An envelope in which no common-core result is `pass` or
  `fail` cannot register a lane.
- **Missing receipt bindings are rejected.** An envelope that omits a required receipt field, or
  binds a revision this manifest does not publish, cannot claim canonical-fixture provenance.
- **Missing evidence fails the tier.** A required lane that produces no envelope at all fails
  nightly and release; absence is never a pass.

## 8. Receipt bindings

Required today: `fixture_revision` and `server_config_revision`. Planned:
`auth_policy_revision`.

`tests/python/shared/cert_envelope.py` currently binds `fixture_revision` to the digest of
`tests/seed/client-compat-v1.sql` alone and `server_config_revision` to the digest of
`tests/config/client-compat-server-v1.json` alone. Both values are published by this manifest as
per-file input digests, so a receipt is verifiable today; converging them onto the composite
`fixtureRevision` and `serverConfigRevision` is gap
`receipt-binding-uses-per-file-digests`. Until it closes, a verifier accepts either the recorded
per-file value or the composite it converges to, and nothing else. No producer emits
`auth_policy_revision` yet (`auth-policy-revision-not-in-receipts`), and the committed baselines
predate receipt binding entirely (`active-baselines-lack-receipt-bindings`).

## 9. OGC CITE exception

OGC CITE suites retain their specification-mandated custom seed and setup procedures. A CITE run
binds only candidate identity, candidate image digest, auth policy revision, and capability
mapping; it is never relabelled as having used the canonical client fixture. The governed reason id
is `spec-owned-fixture`.

## 10. Scope fence

Exhaustive geometry, schema, raster, topography, cloud-native, and 3D fixture depth is 2026.2
expansion work owned by [honua-server#3435](https://github.com/honua-io/honua-server/issues/3435).
Its absence is recorded here as governed gaps and must not block the 2026.1 core.

## 11. How to verify

```console
$ python3 scripts/certification/verify-fixture-manifest.py
$ python3 -m pytest scripts/certification/test_verify_fixture_manifest.py -q
$ dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
    --filter "FullyQualifiedName~CanonicalFixtureManifestTests"
```

When an input legitimately changes: edit the file, recompute its digest and the affected composite
revision with the algorithms in section 1, update `inputs[]`, the revision fields, and the table in
section 1 of this document, and re-run all three checks.

<!-- analyst-extension-cases:begin -->

## Canonical analyst-client extension cases

The five canonical analyst lanes ([#3392](https://github.com/honua-io/honua-server/issues/3392)) carry lane-specific cases beyond the shared common core. Each is bound to a lane and protocol in `laneBindings` and mapped to a declared scenario facet, so the fixture contract closes over them the same way it closes over the `CERT-*` core.

### `py-geopandas` — GeoPandas via pyogrio/Fiona (42 cases)

| Case | Scenario facet | Description |
|---|---|---|
| `NB-GPD-AUTH-01` | `SF-AUTH-CRED` | An invalid X-API-Key produced 401 (not 403, which would imply an authenticated-but-forbidden principal, and not 500). |
| `NB-GPD-CRS-01` | `SF-GEOM-FIDELITY` | total_bounds=(np.float64(-122.49), np.float64(37.71), np.float64(-122.37), np.float64(37.79)) matches the canonical lon/lat extent, proving the default GeoJSON output uses CRS84 axis.... |
| `NB-GPD-CRS-02` | `SF-GEOM-FIDELITY` | Opened with the OAPIF driver's CRS=EPSG:3857 open option; the server returned EPSG:3857 and the anchor landed 5.587935447692871e-09 m from the pyproj reference (limit 0.01 m). |
| `NB-GPD-CRS-03` | `SF-GEOM-FIDELITY` | Compared 9 features; worst server-vs-pyproj EPSG:3857 deviation 5.587935447692871e-09 m. |
| `NB-GPD-CRS-04` | `SF-GEOM-FIDELITY` | bbox=-13636081.024722047,4537835.6179989865,-13631628.245090313,4542057.542774347 with bbox-crs=http://www.opengis.net/def/crs/EPSG/0/3857 returned 3 rows, matching the EPSG:4326 bbox result. |
| `NB-GPD-ENG-01` | `SF-EXT-CLIENT-IDIOM` | geopandas.read_file with engine='fiona' and engine='pyogrio' (two independently vendored GDAL builds) agreed on row count, ordering, CRS and geometry to within 0.0 deg. |
| `NB-GPD-ERR-01` | `SF-ERRH-UNKNOWN` | Status codes observed by the client: {'unknown-collection': 404, 'malformed-crs': 400, 'malformed-cql2': 400}. A missing resource is 404 while malformed CRS and CQL2 inputs are 400, so a.... |
| `NB-GPD-FLT-01` | `SF-QFLT-ATTRIBUTE` | datetime=2024-01-03T00:00:00Z/2024-01-05T23:59:59Z selected ['delta', 'epsilon', 'gamma'], i.e. exactly the three features whose created_at falls inside the interval. |
| `NB-GPD-FLT-02` | `SF-QFLT-ATTRIBUTE` | filter='count > 5' returned 5 rows ([6, 7, 8, 9, 10]); the conjunction with status='active' returned 2 rows, so the server evaluates CQL2-text predicates including AND. |
| `NB-GPD-FLT-03` | `SF-QFLT-ATTRIBUTE` | Both an unmatched OGR attribute filter and an unmatched CQL2 predicate produced empty GeoDataFrames with the schema intact rather than an HTTP error or a malformed payload. |
| `NB-GPD-FLT-04` | `SF-QFLT-ATTRIBUTE` | An Antarctic bbox in EPSG:4326 (lat/lon axis order) returned an empty FeatureCollection, confirming the server treats a disjoint-but-valid envelope as a zero-result query. |
| `NB-GPD-GEO-01` | `SF-GEOM-FIDELITY` | 9 geometries round-tripped through shapely WKB and WKT with a worst-case deviation of 0.0 deg. |
| `NB-GPD-GEO-02` | `SF-GEOM-FIDELITY` | declared collection extent (-122.5, 37.7, -122.35, 37.84) contains the materialized data bounds (-122.49, 37.71, -122.37, 37.79), which equal the canonical fixture extent. |
| `NB-GPD-IO-01` | `SF-EXT-FORMAT` | Round-tripped the server response through {'GPKG': 10, 'FlatGeobuf': 10}; worst coordinate deviation 0.0 deg and the null-geometry row stayed null in both formats. |
| `NB-GPD-IO-02` | `SF-EXT-FORMAT` | GeoParquet round trip preserved 10 rows, EPSG:4326 and the datetime64[ms, UTC] timestamp dtype. |
| `NB-GPD-NUL-01` | `SF-GEOM-NULL` | 4 of 10 rows carried a null description; the remainder are non-empty strings, so JSON null was not coerced to an empty string. |
| `NB-GPD-NUL-02` | `SF-GEOM-NULL` | 10 rows returned with 1 null geometry (['lambda']); the geometry-less feature is neither dropped nor given a placeholder geometry. |
| `NB-GPD-PAG-01` | `SF-PAGE-LIMIT` | 4 pages of limit=3 produced 10 distinct features with no repeats and no gaps, so limit/offset paging is stable and complete. |
| `NB-GPD-PAG-02` | `SF-PAGE-LIMIT` | limit=1000000 was clamped and returned 10 rows; offset=10000 returned an empty FeatureCollection rather than an error. |
| `NB-GPD-SCH-01` | `SF-SCHM-FIELDS` | The declared queryable 'eo:cloud_cover' is present in the feature properties and filterable (3 rows matched > 50); ?properties= answered HTTP 200 with columns ['eo:cloud_cover',.... |
| `NB-GPD-SRT-01` | `SF-QFLT-ATTRIBUTE` | sortby=-count produced [10, 9, 8, 7, 6, 5, 4, 3, 2, 1]; the first two pages match the corresponding slices of the full ordering, so the sort is applied before paging. |
| `NB-GPD-TYP-01` | `SF-SCHM-FIELDS` | count dtype=int32, ratio dtype=float64; anchor count=1, ratio=1.25 round-tripped exactly. |
| `NB-GPD-TYP-02` | `SF-SCHM-FIELDS` | active dtype=bool with 5 true rows, consistent with status='active' on every row. |
| `NB-GPD-TYP-03` | `SF-SCHM-FIELDS` | created_at dtype=datetime64[ms, UTC] (anchor 2024-01-01 12:00:00+00:00), event_date dtype=datetime64[ms] (anchor 2024-02-01 00:00:00); the RFC 3339 timestamps the server emits are parsed.... |
| `NB-GPD-TYP-04` | `SF-SCHM-FIELDS` | Through pyogrio's Arrow path: event_time=datetime.time(12, 34, 56) (a real time value, not a string), tags=['red', 'blue'], numbers=[np.int32(0), np.int32(1), np.int32(2)],.... |
| `NB-GPD-WFS-BBX-01` | `SF-EXT-CLIENT-IDIOM` | BBOX in urn:ogc:def:crs:EPSG::4326 lat/lon order matched 3 features; the same numbers supplied in lon/lat order yielded HTTP 400, proving the server applies the CRS-declared axis order.... |
| `NB-GPD-WFS-CAP-01` | `SF-EXT-CLIENT-IDIOM` | All 5 feature types advertised by GetCapabilities resolved through DescribeFeatureType and reported a geometry type: ['honua:test_layer', 'honua:browser_points', 'honua:browser_lines',.... |
| `NB-GPD-WFS-CRS-01` | `SF-EXT-CLIENT-IDIOM` | total_bounds=(np.float64(-122.49), np.float64(37.71), np.float64(-122.37), np.float64(37.79)) after GDAL applied the axis swap implied by srsName=urn:ogc:def:crs:EPSG::4326; the server's.... |
| `NB-GPD-WFS-CRS-02` | `SF-EXT-CLIENT-IDIOM` | SRSNAME=urn:ogc:def:crs:EPSG::3857 returned EPSG:3857; the anchor landed 2.7939677238464355e-09 m from the pyproj reference (limit 0.01 m). |
| `NB-GPD-WFS-ERR-01` | `SF-EXT-CLIENT-IDIOM` | Client-observed statuses: {'unknown-typename': '400', 'malformed-srsname': '400'}. The unknown-typename response body was a well-formed ows:ExceptionReport, so a GeoPandas caller.... |
| `NB-GPD-WFS-FLT-01` | `SF-EXT-CLIENT-IDIOM` | fes:PropertyIsEqualTo(status, 'active') returned 5 features, all with status='active', so the server evaluates OGC Filter Encoding 2.0 predicates. |
| `NB-GPD-WFS-HIT-01` | `SF-EXT-CLIENT-IDIOM` | GDAL satisfies GetFeatureCount with GetFeature&RESULTTYPE=hits (fast_feature_count=True) and the server reported numberMatched=10, matching the seeded row count without transferring features. |
| `NB-GPD-WFS-IDN-01` | `SF-EXT-CLIENT-IDIOM` | 10 unique gml:id values (e.g. ['test_layer.1', 'test_layer.2', 'test_layer.3']), identical across two requests and each suffixed with the feature's objectid, so a client can key on them.... |
| `NB-GPD-WFS-IO-01` | `SF-EXT-CLIENT-IDIOM` | GeoPackage round trip preserved 10 features, EPSG:4326, the objectid ordering and the null geometry, with a worst deviation of 0.0 deg. |
| `NB-GPD-WFS-NS-01` | `SF-EXT-CLIENT-IDIOM` | TYPENAMES='honua:test_layer' and TYPENAMES='test_layer' returned the same 10 features in the same order, so the unprefixed name used by the server's own paging links and.... |
| `NB-GPD-WFS-NUL-01` | `SF-EXT-CLIENT-IDIOM` | 10 features returned with 1 nil geometry; the server emits the nillable geometry property rather than dropping the feature or writing an empty gml:Point. |
| `NB-GPD-WFS-PAG-01` | `SF-EXT-CLIENT-IDIOM` | 4 pages of COUNT=3 produced 10 distinct features with no repeats and no gaps. |
| `NB-GPD-WFS-PAG-02` | `SF-EXT-CLIENT-IDIOM` | COUNT=1000000 returned 10 features (clamped, not rejected); STARTINDEX=10000 returned an empty FeatureCollection rather than a service exception. |
| `NB-GPD-WFS-PRP-01` | `SF-EXT-CLIENT-IDIOM` | PROPERTYNAME=name,status produced exactly ['name', 'status'] on 3 features; no unrequested property was serialized. |
| `NB-GPD-WFS-SCH-01` | `SF-EXT-CLIENT-IDIOM` | WFS exposed the namespaced field as 'eo_x003A_cloud_cover' (GML escapes ':' as _x003A_) with 9 populated values, e.g. [5.0, 8.0, 25.0]. |
| `NB-GPD-WFS-TYP-01` | `SF-EXT-CLIENT-IDIOM` | objectid dtype=int32, count=int32, ratio=float64, active=bool; xsd:int/xsd:double/xsd:boolean from DescribeFeatureType survived into pandas dtypes. |
| `NB-GPD-WFS-TYP-02` | `SF-EXT-CLIENT-IDIOM` | created_at='2024-01-01T12:00:00+00:00', event_date='2024-02-01', event_time='12:34:56.0000000' - all parse to the seeded instants. |

### `py-owslib` — OWSLib (65 cases)

| Case | Scenario facet | Description |
|---|---|---|
| `NB-OWS-OAF-AUTH-03` | `SF-EXT-CLIENT-IDIOM` | A syntactically valid but incorrect X-API-Key returns 401, not 403 or 500. |
| `NB-OWS-OAF-COLL-01` | `SF-EXT-CLIENT-IDIOM` | collection.extent.spatial.bbox=[-122.5, 37.7, -122.35, 37.84] encloses the seeded feature envelope [-122.49, 37.71, -122.37, 37.79] and is declared in CRS84; temporal interval.... |
| `NB-OWS-OAF-COLL-02` | `SF-EXT-CLIENT-IDIOM` | Features.feature_collections() -> ['2000', '2001', '2002', '3000', '0']; every collection declares itemType=feature. |
| `NB-OWS-OAF-CONF-01` | `SF-EXT-CLIENT-IDIOM` | /conformance declares 26 classes including Features 1.0 core/geojson/oas30. |
| `NB-OWS-OAF-CONF-02` | `SF-EXT-CLIENT-IDIOM` | Declared conformance classes were exercised rather than trusted: ['crs', 'queryables', 'cql2-text'] all behaved as advertised. |
| `NB-OWS-OAF-CRS-01` | `SF-EXT-CLIENT-IDIOM` | CRS84 -> lon/lat, EPSG:4326 -> lat/lon (axis order honoured, not just echoed), EPSG:3857 -> (-13635524.42726808, 4538539.15341347) which is within 3.725290298461914e-09 m of the.... |
| `NB-OWS-OAF-CRS-02` | `SF-EXT-CLIENT-IDIOM` | All 3 CRSs advertised on the collection were accepted by /items and echoed back in Content-Crs: ['http://www.opengis.net/def/crs/EPSG/0/3857',.... |
| `NB-OWS-OAF-DATE-01` | `SF-EXT-CLIENT-IDIOM` | A closed RFC 3339 interval on the seeded created_at column selects exactly the three features inside it (alpha/beta/gamma). |
| `NB-OWS-OAF-ERR-01` | `SF-EXT-CLIENT-IDIOM` | All 5 deliberate client errors (unknown collection, unknown item, bad CRS, negative offset, short bbox) returned RFC 7807 problem+json with matching status, title, detail and instance. |
| `NB-OWS-OAF-ITEM-01` | `SF-EXT-CLIENT-IDIOM` | /items/1 returned the identical Feature the collection listing carried, with self and collection link relations. |
| `NB-OWS-OAF-LAND-01` | `SF-EXT-CLIENT-IDIOM` | Landing page carries typed self/conformance/data/service-desc links plus an alternate representation, which is what OWSLib navigates from. |
| `NB-OWS-OAF-LAND-02` | `SF-EXT-CLIENT-IDIOM` | Features.api() resolved the service-desc link to an OpenAPI 3.0.3 document describing 15 paths, including the landing page, collections, single collection and collection items resources. |
| `NB-OWS-OAF-LINK-01` | `SF-EXT-CLIENT-IDIOM` | Paged responses advertise self+next and no prev on page 1; a response that already covers numberMatched advertises no next link. |
| `NB-OWS-OAF-LINK-02` | `SF-EXT-CLIENT-IDIOM` | Followed the advertised next href verbatim (http://honua:5000/ogc/features/collections/0/items?limit=3&offset=3&f=geojson); it returned a disjoint GeoJSON page, so OWSLib-style link.... |
| `NB-OWS-OAF-PAGE-03` | `SF-EXT-CLIENT-IDIOM` | A limit=3 walk over the collection yielded 10 distinct feature ids, exactly matching numberMatched=10: no gaps, no repeats, stable ordering. |
| `NB-OWS-OAF-QFLT-03` | `SF-EXT-CLIENT-IDIOM` | CQL2-text `count > 7` compares numerically (3 rows: theta/iota/lambda); a lexical comparison would also return rows 8 and 9 or drop 10. |
| `NB-OWS-OAF-QRYB-01` | `SF-EXT-CLIENT-IDIOM` | Queryables is a 2020-12 JSON Schema with $id, every property typed, and the non-queryable JSON array columns correctly excluded. |
| `NB-OWS-OAF-SCHM-03` | `SF-EXT-CLIENT-IDIOM` | All 12 seeded attributes round-trip with their JSON types preserved (bool/int/double/array), not stringified. |
| `NB-OWS-OAF-SORT-01` | `SF-EXT-CLIENT-IDIOM` | OWSLib's (property, direction) sortby tuple maps to the server's `-name` convention; desc is the exact reverse of asc over all 10 features. |
| `NB-OWS-WFS-100-01` | `SF-EXT-CLIENT-IDIOM` | OWSLib negotiated WFS 1.0.0, parsed capabilities, discovered the canonical layer, and executed GetFeature with longitude/latitude axis order. |
| `NB-OWS-WFS-110-01` | `SF-EXT-CLIENT-IDIOM` | OWSLib negotiated WFS 1.1.0, parsed capabilities, discovered the canonical layer, and executed GetFeature with latitude/longitude axis order. |
| `NB-OWS-WFS-BBOX-01` | `SF-EXT-CLIENT-IDIOM` | The 4-element BBOX (default CRS, longitude/latitude) and the 5-element CRS84 BBOX select the identical feature set ['alpha', 'beta', 'gamma'], so the server's bbox axis-order handling.... |
| `NB-OWS-WFS-CAP-01` | `SF-EXT-CLIENT-IDIOM` | OperationsMetadata advertises 27 entries covering every mandatory WFS 2.0 operation, and GetFeature offers both ['get', 'post'] DCP bindings. |
| `NB-OWS-WFS-CAP-02` | `SF-EXT-CLIENT-IDIOM` | Capabilities declare the WFS 2.0 conformance constraint set including ImplementsBasicWFS, ImplementsResultPaging, KVPEncoding and XMLEncoding; the paging and encoding claims are.... |
| `NB-OWS-WFS-CAP-03` | `SF-EXT-CLIENT-IDIOM` | All 5 advertised GetFeature output formats returned real payloads: application/gml+xml; version=3.2=1707B, GML3.2=1671B, application/geo+json=518B, text/csv=375B, application/json=518B. |
| `NB-OWS-WFS-CRS-02` | `SF-EXT-CLIENT-IDIOM` | 4 (CRS, spelling) combinations from the advertised crsOptions were served, each labelled with a matching srsName and reprojected within tolerance. |
| `NB-OWS-WFS-CRS-03` | `SF-EXT-CLIENT-IDIOM` | All three CRS84 spellings (URN, CRS:84, OGC URI) return longitude/latitude ordinates labelled with the CRS84 URN, so srsName and axis order agree. |
| `NB-OWS-WFS-DFT-01` | `SF-EXT-CLIENT-IDIOM` | DescribeFeatureType returned a well-formed XSD declaring 'test_layer' in the gml:AbstractFeature substitution group and importing GML 3.2. |
| `NB-OWS-WFS-ERR-02` | `SF-EXT-CLIENT-IDIOM` | Every deliberate client error produced an ows:ExceptionReport OWSLib could parse: bad-srsname=InvalidParameterValue, structurally-invalid-filter=InvalidParameterValue,.... |
| `NB-OWS-WFS-FILT-02` | `SF-EXT-CLIENT-IDIOM` | fes:PropertyIsGreaterThan on the integer `count` column returns exactly the three rows above 7; a lexical comparison would mis-order 10. |
| `NB-OWS-WFS-HITS-01` | `SF-EXT-CLIENT-IDIOM` | RESULTTYPE=hits reported numberMatched=10 with numberReturned=0 and no wfs:member elements, which is what a client uses to size a query before fetching. |
| `NB-OWS-WFS-PAGE-03` | `SF-EXT-CLIENT-IDIOM` | A COUNT=3 walk produced 10 distinct gml:id values summing exactly to numberMatched: no gaps, no repeats, stable ordering across pages. |
| `NB-OWS-WFS-PROP-01` | `SF-EXT-CLIENT-IDIOM` | propertyname=['name','status'] narrowed the payload to exactly those two columns, and the PROPERTYNAME=* wildcard widened it back to all 13 properties. |
| `NB-OWS-WFS-SORT-01` | `SF-EXT-CLIENT-IDIOM` | SORTBY=name returned all 10 features in ascending name order. |
| `NB-OWS-WFS-STQ-01` | `SF-EXT-CLIENT-IDIOM` | ListStoredQueries advertises 1 queries including the mandatory urn:ogc:def:query:OGC-WFS::GetFeatureById; invoking it through OWSLib's storedQueryID/storedQueryParams returned the.... |
| `NB-OWS-WFS-T-DEL-01` | `SF-EXT-CLIENT-IDIOM` | OWSLib posts a WFS 2.0 Delete to a dedicated scratch layer; the transaction summary reports one deletion and a follow-up OWSLib GetFeature query observes an empty layer. |
| `NB-OWS-WFS-T-INS-01` | `SF-EXT-CLIENT-IDIOM` | OWSLib posts a WFS 2.0 Insert to a dedicated scratch layer; the transaction summary reports one insertion and a follow-up OWSLib GetFeature query observes the new feature. |
| `NB-OWS-WFS-T-UPD-01` | `SF-EXT-CLIENT-IDIOM` | OWSLib posts a WFS 2.0 Update to a dedicated scratch layer; the transaction summary reports one update and a follow-up OWSLib GetFeature query observes only the new value. |
| `NB-OWS-WFS-VER-01` | `SF-EXT-CLIENT-IDIOM` | OWSLib's bare getfeature() (which sends PROPERTYNAME=*) works on both legacy versions. |
| `NB-OWS-WFS-XPRO-01` | `SF-EXT-CLIENT-IDIOM` | WFS and OGC API - Features agree on the same layer: extent (-122.5, 37.7, -122.35, 37.84), numberMatched 10, and every WFS-advertised EPSG code [3857, 4326] is also offered by the OGC.... |
| `NB-OWS-WMS-111-01` | `SF-RNDR-IMAGE` | WMS 1.1.1 identifies as OGC:WMS, advertises SRS (not CRS:84) and the 1.1.1 exception MIME type, and its longitude-first EPSG:4326 GetMap is pixel-identical to the 1.3.0 CRS:84 render of.... |
| `NB-OWS-WMS-111-WITNESS-01` | `SF-RNDR-IMAGE` | OWSLib negotiated WMS 1.1.1, parsed capabilities, discovered the canonical raster layer, and executed a non-empty GetMap using the 1.1.1 SRS request shape. |
| `NB-OWS-WMS-CAP-01` | `SF-RNDR-IMAGE` | Service block: Name=WMS, Title='browser_compat', Abstract='Honua WMS service', 3 keywords ['WMS', 'OGC', 'browser_compat'], OnlineResource.... |
| `NB-OWS-WMS-CAP-02` | `SF-RNDR-IMAGE` | OWSLib parsed 8 ContactInformation fields: {'name': 'Honua Support', 'organization': 'Honua', 'position': 'Support Engineer', 'address': '1 Honua Way', 'city': 'Honolulu', 'region':.... |
| `NB-OWS-WMS-CAP-03` | `SF-RNDR-IMAGE` | EX_GeographicBoundingBox (-122.44, 37.76, -122.4, 37.79); on the wire the BoundingBox for CRS:84 is (-122.44, 37.76, -122.4, 37.79) (longitude first) and for EPSG:4326 is (37.76,.... |
| `NB-OWS-WMS-CAP-04` | `SF-RNDR-IMAGE` | The advertised LegendURL for style 'default' returned a decodable 122x32 PNG. |
| `NB-OWS-WMS-CAP-05` | `SF-RNDR-IMAGE` | GetMap formats ['image/png', 'image/jpeg']; GetFeatureInfo formats ['text/plain', 'application/vnd.ogc.gml', 'application/json']; exception formats ['XML']; every operation carries an.... |
| `NB-OWS-WMS-ERR-01` | `SF-RNDR-IMAGE` | All 5 deliberate GetMap errors (unsupported-format, unknown-crs, inverted-bbox, degenerate-bbox, oversize-width) raised owslib ServiceException from an ogc:ServiceExceptionReport served.... |
| `NB-OWS-WMS-GFI-01` | `SF-RNDR-IMAGE` | GetFeatureInfo at I/J (111, 107) -- the pixel the seeded pt-alpha point projects to in the requested view -- returned that feature with its attributes, so the server's pixel-to-world.... |
| `NB-OWS-WMS-GFI-02` | `SF-RNDR-IMAGE` | All 3 advertised GetFeatureInfo formats identified the same feature with a body matching the declared media type: text/plain=46B, application/vnd.ogc.gml=570B, application/json=117B. |
| `NB-OWS-WMS-GFI-03` | `SF-RNDR-IMAGE` | A GetFeatureInfo aimed at empty space returns a well-formed response with an empty feature list rather than an exception, which is what identify tools rely on. |
| `NB-OWS-WMS-MAP-01` | `SF-RNDR-IMAGE` | Every advertised GetMap format decoded at the requested size: image/png -> PNG/RGBA, image/jpeg -> JPEG/RGB. |
| `NB-OWS-WMS-MAP-02` | `SF-RNDR-IMAGE` | CRS:84 and EPSG:4326 requests for the same ground area are pixel-identical even though OWSLib sends latitude-first ordinates for EPSG:4326, and the EPSG:3857 reprojection of the same.... |
| `NB-OWS-WMS-MAP-03` | `SF-RNDR-IMAGE` | TRANSPARENT=TRUE -> fully transparent background; TRANSPARENT=FALSE -> opaque white; BGCOLOR=0xFF0000 -> opaque red, with the features still drawn on top (103 non-background pixels). |
| `NB-OWS-WMS-MAP-04` | `SF-RNDR-IMAGE` | STYLES=default matches the implicit default style pixel for pixel, and a 3-layer LAYERS request composites strictly more content than the single layer alone. |
| `NB-OWS-WMS-MAP-05` | `SF-RNDR-IMAGE` | A well-formed bbox outside the layer extent returns a correctly sized, fully transparent PNG rather than a ServiceException, so tiled clients degrade to empty tiles instead of error tiles. |
| `NB-OWS-WMS-VER-01` | `SF-RNDR-IMAGE` | Version negotiation: {'1.3.0': '1.3.0', '1.1.1': '1.1.1', '1.1.0': '1.1.1', '1.0.0': '1.1.1', '9.9.9': '1.3.0', '<absent>': '1.3.0'}. A request above the supported range degrades to the.... |
| `NB-OWS-WMS-XPRO-01` | `SF-RNDR-IMAGE` | Layer 'Browser Points' has the same title and the same WGS84 extent (-122.44, 37.76, -122.4, 37.79) through WMS capabilities and the OGC API - Features collection, so the two adapters.... |
| `NB-OWS-WMTS-CAP-01` | `SF-RNDR-TILE` | OperationsMetadata declares ['GetCapabilities', 'GetFeatureInfo', 'GetTile']; GetTile offers both ['KVP', 'RESTFUL'] GetEncoding constraints and OWSLib selects the KVP binding from them. |
| `NB-OWS-WMTS-CAP-02` | `SF-RNDR-TILE` | Both tile matrix sets are internally consistent: contiguous zoom identifiers, 256px tiles, a fixed TopLeftCorner, power-of-two matrix growth and halving scale denominators. |
| `NB-OWS-WMTS-CAP-03` | `SF-RNDR-TILE` | 38 TileMatrixLimits entries across both grids stay within the row/column range of the tile matrix they constrain, so a limits-aware client cannot be steered at a tile that does not exist. |
| `NB-OWS-WMTS-CAP-04` | `SF-RNDR-TILE` | 3 ResourceURL entries (['FeatureInfo', 'tile']); OWSLib's own buildTileResource substituted the tile template to.... |
| `NB-OWS-WMTS-CAP-05` | `SF-RNDR-TILE` | The default style's LegendURL returned a decodable 256x256 PNG matching the declared LegendURL width/height (256, 256). |
| `NB-OWS-WMTS-ERR-01` | `SF-RNDR-TILE` | Every out-of-contract GetTile returned a coded ows:ExceptionReport: row-out-of-range=TileOutOfRange, column-out-of-range=TileOutOfRange, unknown-tilematrixset=InvalidParameterValue,.... |
| `NB-OWS-WMTS-GFI-01` | `SF-RNDR-TILE` | The advertised FeatureInfo ResourceURL, substituted for tile 14/6332/2620 at pixel I/J (142, 194) -- derived from the capabilities tile geometry -- identified the seeded pt-alpha.... |
| `NB-OWS-WMTS-TILE-01` | `SF-RNDR-TILE` | Tile indices derived from the capabilities scale denominators with the WMTS pixel-span formula produced in-range tiles at every sampled level and all decoded as 256x256 PNGs: {'0': (0,.... |
| `NB-OWS-WMTS-TILE-02` | `SF-RNDR-TILE` | WorldCRS84Quad declares the OGC 2x1 level-0 grid with its origin at (-180, 90) in CRS84 longitude/latitude order, and GetTile(WorldCRS84Quad/4/4/5) -- derived from that geometry --.... |
| `NB-OWS-WMTS-TILE-03` | `SF-RNDR-TILE` | The advertised RESTful ResourceURL (http://honua:5000/rest/services/browser_compat/MapServer/WMTS/2000/default/WebMercatorQuad/5/12/5.png) and the KVP GetTile binding return.... |
| `NB-OWS-WMTS-TILE-04` | `SF-RNDR-TILE` | Two identical GetTile requests returned byte-identical 558-byte payloads, so the tile stream is cacheable and reproducible. |
| `NB-OWS-WMTS-TILE-05` | `SF-RNDR-TILE` | GetTile(WebMercatorQuad/5/18/24) -- a valid index far from the seeded extent -- returned a fully transparent 256x256 PNG rather than a ServiceException. |
| `NB-OWS-WMTS-XPRO-01` | `SF-RNDR-TILE` | Layer 'Browser Points' carries the same title and the same WGS84 bounding box (-122.44, 37.76, -122.4, 37.79) through the WMTS capabilities and the OGC API - Features collection for the.... |

### `duckdb` — DuckDB Spatial (28 cases)

| Case | Scenario facet | Description |
|---|---|---|
| `NB-DDB-AUTH-03` | `SF-AUTH-CRED` | GET /api/v1/admin/services with an invalid X-API-Key -> 401 (not 403, not 500). |
| `NB-DDB-AUTH-04` | `SF-AUTH-CRED` | DuckDB httpfs authenticated the same control-plane read via CREATE SECRET (TYPE http, EXTRA_HTTP_HEADERS MAP) on duckdb=1.5.5;spatial=eb1e57c; the header survives into the HTTP GET. |
| `NB-DDB-CRS-01` | `SF-GEOM-FIDELITY` | Content-Crs echoes the negotiated CRS for every supported value and defaults to CRS84: {'<default>': '<http://www.opengis.net/def/crs/OGC/1.3/CRS84>',.... |
| `NB-DDB-CRS-02` | `SF-GEOM-FIDELITY` | crs=CRS84 -> (-122.49, 37.71) = (lon, lat); crs=EPSG:4326 -> (37.71, -122.49) = (lat, lon). |
| `NB-DDB-CRS-03` | `SF-GEOM-FIDELITY` | bbox-crs=EPSG:3857 with the DuckDB-reprojected envelope selected [1, 2, 3], identical to the CRS84 bbox. |
| `NB-DDB-ERR-01` | `SF-ERRH-UNKNOWN` | Unknown collection -> 404 application/problem+json; malformed bbox -> 400 application/problem+json. |
| `NB-DDB-ERR-02` | `SF-ERRH-UNKNOWN` | Unsupported format/CRS/bbox-crs each answered 400 application/problem+json rather than 500 or a hang: {'f=nosuchformat': "Unsupported format 'nosuchformat'", 'crs=bogus': "Unsupported.... |
| `NB-DDB-ERR-04` | `SF-ERRH-UNKNOWN` | Unparseable paging parameters answered structured problem+json 400s: {'limit=abc': (400, 'application/problem+json'), 'limit=1.5': (400, 'application/problem+json'), 'offset=abc': (400,.... |
| `NB-DDB-FMT-01` | `SF-EXT-FORMAT` | COPY ... TO PARQUET then read_parquet preserved 10 rows, 9 WKB geometries and the anchor within 0.0 degrees. |
| `NB-DDB-FMT-02` | `SF-EXT-FORMAT` | COPY ... (FORMAT GDAL, DRIVER 'GeoJSON') then ST_Read preserved 9 geometries and the anchor within 0.0 degrees of the server's coordinates. |
| `NB-DDB-GEOM-03` | `SF-GEOM-FIDELITY` | ST_AsText(geom) = POINT (-122.49 37.71); WKB round-trip is bit-identical (deviation 0.0), so the server's coordinates survive DuckDB's binary encoding without precision loss. |
| `NB-DDB-GEOM-04` | `SF-GEOM-FIDELITY` | ST_IsValid returned true for all 9 emitted geometries. |
| `NB-DDB-GEOM-05` | `SF-GEOM-FIDELITY` | Collection 2001: 2 LINESTRINGs, lengths [2634.4, 2634.5] m in EPSG:3857. |
| `NB-DDB-GEOM-06` | `SF-GEOM-FIDELITY` | Server crs=EPSG:3857 -> (-13635524.42726808, 4538539.15341347); DuckDB ST_Transform(always_xy) -> (-13635524.427268079, 4538539.1534134755); max deviation 5.587935447692871e-09 m <= 0.01.... |
| `NB-DDB-NULL-01` | `SF-GEOM-NULL` | 4 NULL and 6 populated description values across 10 rows — the server emits JSON null (not an omitted key or an empty string) and no row was dropped. |
| `NB-DDB-NULL-02` | `SF-GEOM-NULL` | 1 null-geometry row ('lambda', status 'inactive') and 9 geometry rows totalling 10. |
| `NB-DDB-PAGE-03` | `SF-PAGE-LIMIT` | 5 pages of limit=3 produced [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] — exactly the full set, no duplicates, no gaps, and the walk terminated on an empty page. |
| `NB-DDB-PAGE-04` | `SF-PAGE-LIMIT` | limit=100000 -> numberMatched/numberReturned 10/10; bbox subset with limit=2 -> 3/2 (numberMatched counts the filtered set, not the page); offset=100000 -> 0 rows via DuckDB; limit=0 ->.... |
| `NB-DDB-PUSH-01` | `SF-QFLT-SPATIAL` | SERVER-SIDE bbox pushdown and CLIENT-SIDE ST_Intersects over a full fetch both selected [1, 2, 3]. |
| `NB-DDB-PUSH-02` | `SF-QFLT-SPATIAL` | SERVER-SIDE cql2-text filter status='active' and the CLIENT-SIDE DuckDB predicate both selected [1, 3, 5, 7, 9]. |
| `NB-DDB-PUSH-03` | `SF-QFLT-SPATIAL` | SERVER-SIDE datetime=2024-01-01T00:00:00Z/2024-01-03T23:59:59Z and the CLIENT-SIDE DuckDB TIMESTAMPTZ BETWEEN predicate both selected [1, 2, 3], so the server's temporal field binding.... |
| `NB-DDB-QRY-01` | `SF-QFLT-ATTRIBUTE` | A zero-match cql2-text filter returned an empty FeatureCollection (HTTP 200) that GDAL opened without error, so DuckDB sees 0 rows rather than an exception. |
| `NB-DDB-QRY-02` | `SF-QFLT-ATTRIBUTE` | min/max/sum over 'count' = 1/10/55, avg(ratio) = 6.875, GROUP BY status = {'active': 5, 'inactive': 5}. Every value matches the canonical fixture, so the server delivered a complete,.... |
| `NB-DDB-QRY-03` | `SF-QFLT-ATTRIBUTE` | Spatial join of collections 0 and 2000 via ST_DWithin produced 27 pairs; row_number() OVER (PARTITION BY status) picked [('active', 'alpha', 1), ('inactive', 'beta', 1)]. |
| `NB-DDB-TYPE-01` | `SF-SCHM-FIELDS` | All 13 columns kept their natural type through the server -> GeoJSON -> GDAL -> DuckDB path; nothing was silently coerced to VARCHAR. |
| `NB-DDB-TYPE-02` | `SF-SCHM-FIELDS` | Cross-checked 11 fields from http://honua:5000/ogc/features/collections/0/queryables against the DuckDB types ST_Read produced. |
| `NB-DDB-TYPE-03` | `SF-SCHM-FIELDS` | The server declares uid with JSON-Schema format 'uuid'; GeoJSON has no UUID type so GDAL/DuckDB materialize it as VARCHAR. |
| `NB-DDB-TYPE-04` | `SF-SCHM-FIELDS` | tags -> VARCHAR[] ['red', 'blue'], numbers -> INTEGER[] [0, 1, 2]; list indexing and len() work, so the server emitted real JSON arrays rather than JSON-encoded strings. |

### `r-sf` — R sf and ows4R (50 cases)

| Case | Scenario facet | Description |
|---|---|---|
| `NB-RSF-AUT-01` | `SF-AUTH-CRED` | 401 challenge shape on /api/v1/admin/services: WWW-Authenticate: ApiKey realm="Honua Admin", header="X-API-Key". |
| `NB-RSF-AUT-02` | `SF-AUTH-CRED` | A wrong X-API-Key value returned HTTP 401 on /api/v1/admin/services; it must be 401 (bad credential), not 403 (authenticated but forbidden) and never 500. |
| `NB-RSF-AUT-03` | `SF-AUTH-CRED` | GDAL_HTTP_HEADERS carried 'X-API-Key: <admin key>' through sf::st_layers() on the WFS DSN: 7 layer(s) listed and the certification target 'honua:test_layer' is present. |
| `NB-RSF-CRS-01` | `SF-GEOM-FIDELITY` | WFS advertises CRS list {4326, 3857} for the certification layer; both the storage CRS (EPSG:4326) and the Web Mercator alternative (EPSG:3857) must be offered or R users cannot request.... |
| `NB-RSF-CRS-02` | `SF-GEOM-FIDELITY` | Server-side reprojection to EPSG:3857 (urn:ogc:def:crs:EPSG::3857) returned (-13635524.4273, 4538539.1534); PROJ 9.4.0 via sf::st_transform() computes (-13635524.4273, 4538539.1534); max.... |
| `NB-RSF-CRS-03` | `SF-GEOM-FIDELITY` | Axis order with srsName=urn:ogc:def:crs:EPSG::4326: raw GML gml:pos is '37.71 -122.49' (spec requires lat lon for the urn form) and sf/GDAL recovered (-122.4900, 37.7100) as lon/lat. |
| `NB-RSF-CRS-04` | `SF-GEOM-FIDELITY` | bbox axis-order contract for WFS: the specified order (the feature type's default CRS, lat,lon for urn EPSG::4326) selected 3 feature(s) (expected 3), and the reversed order returned.... |
| `NB-RSF-ERR-01` | `SF-ERRH-UNKNOWN` | WFS error shape: unknown typeName returned 400 with an ows:ExceptionReport (exceptionCode present); an unsupported REQUEST returned 501. |
| `NB-RSF-ERR-02` | `SF-ERRH-UNKNOWN` | Malformed CRS 'urn:ogc:def:crs:BOGUS::9999' returned HTTP 400 with a structured error body (problem+json / ExceptionReport). |
| `NB-RSF-ERR-03` | `SF-ERRH-UNKNOWN` | Unsupported output format returned HTTP 400 (expected a 4xx). |
| `NB-RSF-ERR-04` | `SF-ERRH-UNKNOWN` | Truncated protocol filter ("status = 'active' AND") returned HTTP 400 (expected a 4xx structured error, never a 500 and never a silent full result set). |
| `NB-RSF-FMT-01` | `SF-EXT-FORMAT` | End-to-end GeoPackage fidelity: the WFS response was written with sf::st_write() and read back with sf::st_read() — 10/10 rows, names match, empty geometries match, max coordinate.... |
| `NB-RSF-FMT-02` | `SF-EXT-FORMAT` | End-to-end GeoJSON fidelity: the WFS response was written with sf::st_write() and read back with sf::st_read() — 10/10 rows, names match, empty geometries match, max coordinate deviation.... |
| `NB-RSF-FMT-03` | `SF-EXT-FORMAT` | Advertised output formats all serve the complete feature set: application/geo+json -> HTTP 200, 4059 byte(s), anchor present, last feature present; text/csv -> HTTP 200, 2516 byte(s),.... |
| `NB-RSF-GEO-01` | `SF-GEOM-FIDELITY` | st_bbox() of the returned features (-122.49000 37.71000 -122.37000 37.79000) against the WFS declared extent (-122.50000 37.70000 -122.35000 37.84000): the data extent must lie inside.... |
| `NB-RSF-GEO-02` | `SF-GEOM-FIDELITY` | WKB (st_as_binary) and WKT (st_as_text) round-trip of 9 server geometries: max coordinate deviation 0.000e+00 (WKB 0.000e+00, WKT 0.000e+00), threshold 1e-06. |
| `NB-RSF-GEO-03` | `SF-GEOM-FIDELITY` | sf::st_is_valid() over 9 server geometries: 9 valid, 0 invalid, 0 indeterminate. |
| `NB-RSF-NUL-01` | `SF-GEOM-NULL` | Nullable `description`: 4 of 10 rows are NA (fixture seeds 4 NULLs); 'alpha' is NA and 'beta' is 'description_1'. A server that emitted "" or the string "null" would fail this. |
| `NB-RSF-NUL-02` | `SF-GEOM-NULL` | Null-geometry handling: 10 row(s) returned with 1 empty geometry/geometries (fixture seeds 10 features, 9 with geometry); the null-geometry row 'lambda' is present. |
| `NB-RSF-OAF-01` | `SF-EXT-CLIENT-IDIOM` | Landing page http://honua:5000/ogc/features returned HTTP 200 with link relations {self, alternate, service-desc, conformance, data, https://www.opengis.net/def/rel/ogc/1.0/map,.... |
| `NB-RSF-OAF-02` | `SF-EXT-CLIENT-IDIOM` | Declared-vs-honoured conformance: 7 of 26 declared classes were probed and all held. |
| `NB-RSF-OAF-03` | `SF-EXT-CLIENT-IDIOM` | Link relations: self=http://honua:5000/ogc/features/collections/0/items?limit=3; following `next` returned HTTP 200 with 3 feature(s) disjoint from page one (overlap 0); the final page.... |
| `NB-RSF-OAF-04` | `SF-EXT-CLIENT-IDIOM` | Single-item retrieval /items/1 returned HTTP 200 as a GeoJSON Feature with name 'alpha' (expected the 'alpha' anchor). |
| `NB-RSF-OAF-05` | `SF-EXT-CLIENT-IDIOM` | datetime=2024-01-01T00:00:00Z/2024-01-03T00:00:00Z returned HTTP 200 with 2 of 10 feature(s); every returned created_at is inside the interval. |
| `NB-RSF-OAF-06` | `SF-EXT-CLIENT-IDIOM` | CQL2-text `filter=status = 'active'` returned HTTP 200 with 5 feature(s) (expected 5) and status values {active}. This is the protocol filter parameter, not a client-side filter. |
| `NB-RSF-OWS-01` | `SF-EXT-CLIENT-IDIOM` | ows4R parsed ows:ServiceIdentification: title='Honua WFS 2.0', serviceType='WFS', serviceTypeVersion={2.0.0}. |
| `NB-RSF-OWS-02` | `SF-EXT-CLIENT-IDIOM` | ows4R parsed ows:OperationsMetadata with 9 operation(s): {GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction, ListStoredQueries, DescribeStoredQueries,.... |
| `NB-RSF-OWS-03` | `SF-EXT-CLIENT-IDIOM` | ows4R parsed 5 feature type(s) from the WFS 2.0.0 capabilities: {honua:test_layer, honua:browser_points, honua:browser_lines, honua:browser_polygons, honua:portal_public_points}; the.... |
| `NB-RSF-OWS-04` | `SF-EXT-CLIENT-IDIOM` | ows4R per-feature-type metadata: DefaultCRS parsed to epsg:4326 (expected EPSG:4326) and ows:WGS84BoundingBox parsed. |
| `NB-RSF-OWS-05` | `SF-EXT-CLIENT-IDIOM` | ows4R DescribeFeatureType returned 15 element(s); 12 of 12 canonical attribute fields present. |
| `NB-RSF-OWS-06` | `SF-EXT-CLIENT-IDIOM` | ows4R WFSFeatureType$getFeatures() returned a sf/data.frame with 10 row(s); expected 10. |
| `NB-RSF-OWS-07` | `SF-EXT-CLIENT-IDIOM` | ows4R getFeatures(count=3) then getFeatures(count=3, startIndex=3) returned 3 and 3 row(s) with overlap 0: {alpha,beta,gamma} then {delta,epsilon,zeta}. |
| `NB-RSF-OWS-08` | `SF-EXT-CLIENT-IDIOM` | RESULTTYPE=hits (httr transport-shape check) returned HTTP 200 with numberMatched=10, numberReturned=0 and 0 wfs:member element(s). |
| `NB-RSF-OWS-09` | `SF-EXT-CLIENT-IDIOM` | PROPERTYNAME=name,status returned HTTP 200 with properties {name, status}; expected exactly {name, status}. Property subsetting that silently returns everything wastes the bandwidth the.... |
| `NB-RSF-OWS-10` | `SF-EXT-CLIENT-IDIOM` | OGC Filter Encoding 2.0 fes:PropertyIsEqualTo(status='active') returned HTTP 200 with 5 feature(s) (expected 5) and status values {active}. |
| `NB-RSF-OWS-11` | `SF-EXT-CLIENT-IDIOM` | SORTBY=name A produced {alpha,beta,delta,epsilon,eta,gamma,iota,lambda,theta,zeta} (sorted) and SORTBY=name D produced {zeta,theta,lambda,iota,gamma,eta,epsilon,delta,beta,alpha} (sorted). |
| `NB-RSF-OWS-12` | `SF-EXT-CLIENT-IDIOM` | ows4R against the advertised legacy WFS versions: 1.1.0 -> 5 feature type(s), target present; 1.0.0 -> 5 feature type(s), target present. |
| `NB-RSF-PAG-01` | `SF-PAGE-LIMIT` | Full paginated walk in pages of 3 over 5 request(s) collected 10 unique feature name(s) with 0 duplicate(s); the fixture seeds 10. |
| `NB-RSF-PAG-02` | `SF-PAGE-LIMIT` | Oversized page request (limit/COUNT=100000) returned HTTP 400 with n/a row(s). |
| `NB-RSF-PAG-03` | `SF-PAGE-LIMIT` | Paging counters on a 2-feature page (httr transport-shape check): numberMatched=10 (expected 10), numberReturned=2, actual features=2. numberMatched must be the unpaged total, not the.... |
| `NB-RSF-PAG-04` | `SF-PAGE-LIMIT` | Offset/startIndex past the end returned HTTP 200 with 0 feature(s) and numberMatched=10 (expected 200, 0 features, 10 matched). |
| `NB-RSF-PAG-05` | `SF-PAGE-LIMIT` | Zero-size page request (limit/COUNT=0) returned HTTP 200 with 0 feature(s): a structured 4xx or an empty 200 are both defensible; a 5xx or a full result set is not. |
| `NB-RSF-TYP-01` | `SF-SCHM-FIELDS` | Numeric/boolean typing through WFS: count=integer, ratio=numeric, active=logical. |
| `NB-RSF-TYP-02` | `SF-SCHM-FIELDS` | Temporal typing through WFS: created_at as character, event_date as character, event_time as character. |
| `NB-RSF-TYP-03` | `SF-SCHM-FIELDS` | JSON array columns through WFS/GML: tags as character, numbers as character; values tags=[red\|blue] numbers=[0\|1\|2]. |
| `NB-RSF-TYP-04` | `SF-SCHM-FIELDS` | uuid column materialised as character with value '00000000-0000-0000-0000-000000000001' (expected '00000000-0000-0000-0000-000000000001'). |
| `NB-RSF-XPR-01` | `SF-EXT-CLIENT-IDIOM` | Cross-protocol extent agreement: OGC API Features collection extent [-122.50000 37.70000 -122.35000 37.84000] vs WFS ows:WGS84BoundingBox [-122.50000 37.70000 -122.35000 37.84000]; max.... |
| `NB-RSF-XPR-02` | `SF-EXT-CLIENT-IDIOM` | Cross-protocol count agreement: OGC API Features numberMatched=10, WFS resultType=hits numberMatched=10, fixture total=10. |
| `NB-RSF-XPR-03` | `SF-EXT-CLIENT-IDIOM` | Cross-protocol CRS agreement: OGC API Features offers {3857, 4326}, WFS offers {3857, 4326}. A CRS available on one protocol but not the other is a metadata bug, not a capability difference. |
| `NB-RSF-XPR-04` | `SF-EXT-CLIENT-IDIOM` | Cross-protocol attribute agreement: 13 field(s) in OGC API Features items vs 13 in WFS DescribeFeatureType. |

### `py-pystac` — pystac-client (39 cases)

| Case | Scenario facet | Description |
|---|---|---|
| `NB-STAC-COLL-01` | `SF-EXT-CLIENT-IDIOM` | Declared temporal extent [2024-01-01 12:00:00+00:00, 2024-01-10 12:00:00+00:00] covers all 10 item datetimes (observed [2024-01-01 12:00:00+00:00, 2024-01-10 12:00:00+00:00]). |
| `NB-STAC-COLL-02` | `SF-EXT-CLIENT-IDIOM` | Required members present (license='proprietary', stac_version=1.0.0, links ['alternate', 'http://www.opengis.net/def/rel/ogc/1.0/queryables', 'items', 'parent', 'root', 'self']);.... |
| `NB-STAC-COLL-03` | `SF-EXT-CLIENT-IDIOM` | /stac/collections returned 5 entries, each rehydrating under pystac.Collection with a spatial extent. |
| `NB-STAC-CONF-01` | `SF-EXT-CLIENT-IDIOM` | core honored: landing page carries ['child', 'conformance', 'data', 'http://www.opengis.net/def/rel/ogc/1.0/queryables', 'root', 'search', 'self', 'service-desc', 'service-doc'] and.... |
| `NB-STAC-CONF-02` | `SF-EXT-CLIENT-IDIOM` | collections honored: all 5 listed collections (0, 2000, 2001, 2002, 3000) round-tripped through get_collection(id). |
| `NB-STAC-CONF-03` | `SF-EXT-CLIENT-IDIOM` | ogcapi-features honored: /items honored limit and bbox and every returned feature rehydrated under pystac.Item.from_dict. |
| `NB-STAC-CONF-04` | `SF-EXT-CLIENT-IDIOM` | item-search honored: landing page advertises ['GET', 'POST'] search links and both methods returned 10 items. |
| `NB-STAC-CONF-05` | `SF-EXT-CLIENT-IDIOM` | item-search#fields honored: include narrowed properties to the requested set and exclude removed only properties.tags. |
| `NB-STAC-CONF-06` | `SF-EXT-CLIENT-IDIOM` | item-search#sort honored: +properties.name and -properties.name produced exactly reversed orderings over 10 items. |
| `NB-STAC-CONF-07` | `SF-EXT-CLIENT-IDIOM` | item-search#filter honored: queryables publish 16 properties (dialect https://json-schema.org/draft/2019-09/schema) and a CQL2-JSON comparison on eo:cloud_cover narrowed to 5 items. |
| `NB-STAC-CONF-08` | `SF-EXT-CLIENT-IDIOM` | oas30 honored: service-desc served OpenAPI 3.0.3 with 9 paths; service-doc link present. |
| `NB-STAC-CONF-09` | `SF-EXT-CLIENT-IDIOM` | basic-cql2 honored in both dialects: an AND of an equality and a numeric comparison returned the same 3 items via cql2-json and cql2-text. |
| `NB-STAC-ERR-01` | `SF-ERRH-UNKNOWN` | GET an unknown item -> status=404 title='Not Found' detail="Item '999999' not found in collection '0'."; pystac-client surfaced it as APIError(status_code=404). |
| `NB-STAC-ERR-02` | `SF-ERRH-UNKNOWN` | 3-value bbox -> status=400 title='Bad Request' detail='bbox must contain four or six numeric values.'; inverted bbox -> status=400 title='Bad Request' detail='bbox latitude values are.... |
| `NB-STAC-ERR-03` | `SF-ERRH-UNKNOWN` | datetime=not-a-date -> status=400 title='Bad Request' detail='Invalid datetime parameter.'; reversed interval -> status=400 title='Bad Request' detail='Invalid datetime parameter.'. |
| `NB-STAC-ERR-04` | `SF-ERRH-UNKNOWN` | filter-lang=bogus-lang -> status=400 title='Bad Request' detail="Invalid filter-lang 'bogus-lang'."; filter-lang without filter -> status=400 title='Bad Request' detail='filter-lang and.... |
| `NB-STAC-ERR-05` | `SF-ERRH-UNKNOWN` | GET /api/v1/admin/services with an incorrect X-API-Key -> 401. |
| `NB-STAC-ERR-06` | `SF-ERRH-UNKNOWN` | An unknown search query parameter -> status=400 title='Bad Request' detail='Unknown query parameter: not-a-real-parameter'. |
| `NB-STAC-ITEM-01` | `SF-EXT-CLIENT-IDIOM` | All 1 assets on item 1 (geojson) carried href/type/roles and resolved with a matching content type. |
| `NB-STAC-ITEM-02` | `SF-EXT-CLIENT-IDIOM` | 9 items had a bbox containing their geometry; the 1 null-geometry item correctly omitted bbox. |
| `NB-STAC-ITEM-03` | `SF-EXT-CLIENT-IDIOM` | Item 1 carried ['collection', 'parent', 'root', 'self'] links; the self link re-fetched the same item and the collection link resolved to the owning collection. |
| `NB-STAC-ITEM-04` | `SF-EXT-CLIENT-IDIOM` | All 10 items carried an RFC 3339 UTC datetime that pystac parsed into a timezone-aware value with zero offset. |
| `NB-STAC-ITEM-05` | `SF-EXT-CLIENT-IDIOM` | stac_extensions declared: none; eo:cloud_cover present on items: True. |
| `NB-STAC-ITEM-06` | `SF-EXT-CLIENT-IDIOM` | Catalog: validated against the published STAC JSON Schemas; Collection: validated against the published STAC JSON Schemas; Item: validated against the published STAC JSON Schemas. |
| `NB-STAC-PAGE-01` | `SF-PAGE-LIMIT` | pages() walked to exhaustion in page sizes [3, 3, 3, 1], collecting all 10 seeded items with no duplicates and terminating without a next link. |
| `NB-STAC-PAGE-02` | `SF-PAGE-LIMIT` | ItemSearch.matched()=10 agrees with numberMatched=10 and numberReturned=3 equals the actual feature count on the page. |
| `NB-STAC-PAGE-03` | `SF-PAGE-LIMIT` | GET next uses a token query parameter; POST next is a body-bearing method=POST merge=true link whose token advanced the cursor to a disjoint page. |
| `NB-STAC-PAGE-04` | `SF-PAGE-LIMIT` | limit=1000000 clamped to the server maximum and still answered 200; limit=0 was rejected with status=400 title='Bad Request' detail='limit must be greater than or equal to 1.'. Raw httpx.... |
| `NB-STAC-PAGE-05` | `SF-PAGE-LIMIT` | A token past the end returned an empty FeatureCollection with no next link; a malformed token was rejected with status=400 title='Bad Request' detail='Invalid pagination token.'. |
| `NB-STAC-SEARCH-01` | `SF-QFLT-SPATIAL` | intersects(Polygon) and the equivalent bbox both returned ['1', '2', '3']. |
| `NB-STAC-SEARCH-02` | `SF-QFLT-SPATIAL` | intersects(Point) at the anchor coordinate matched exactly the anchor item; a degenerate point geometry is the classic spatial-predicate edge case. |
| `NB-STAC-SEARCH-03` | `SF-QFLT-SPATIAL` | datetime=2024-01-03T12:00:00Z matched exactly the one seeded item at that instant. |
| `NB-STAC-SEARCH-04` | `SF-QFLT-SPATIAL` | A closed RFC 3339 interval selected the three items inside it, with both endpoints treated inclusively. |
| `NB-STAC-SEARCH-05` | `SF-QFLT-SPATIAL` | Open-start ../T returned ['1', '2', '3'] and open-end T/.. returned ['10', '8', '9']; the two halves are disjoint. |
| `NB-STAC-SEARCH-06` | `SF-QFLT-SPATIAL` | ids=['2', '4'] returned exactly those items; an unknown id returned an empty FeatureCollection rather than an error or the whole collection. |
| `NB-STAC-SEARCH-07` | `SF-QFLT-SPATIAL` | collections=['0'] returned 10 items all bearing that collection back-reference; the unscoped search spanned ['0', '2000', '2001', '2002', '3000']; an unknown collection matched nothing. |
| `NB-STAC-SEARCH-08` | `SF-QFLT-SPATIAL` | An identical bbox + datetime + sortby search returned the same ordered ids (['1', '2', '3']) over both GET and POST. |
| `NB-STAC-SEARCH-09` | `SF-QFLT-SPATIAL` | limit=3 with max_items=4 crossed a page boundary and yielded four distinct items. |
| `NB-STAC-VALID-01` | `SF-EXT-CLIENT-IDIOM` | stac-api-validator validated core, collections, features, item-search, filter against http://honua:5000/stac with exit code 0. |

<!-- analyst-extension-cases:end -->
