# Client exclusion audit — September 20, 2026

Historical stage: see the [deeper exclusion follow-up](client-exclusion-followup-2026-09-20.md) for current totals and additional native paths.

Follow-up: the [Python lane report](python-lane-status-2026-09-20.md) resolves six
of these audited exclusions with native SDK receipts. The counts and observations
below describe the initial audit; the generated checklist carries current totals.

The four-lane checklist previously closed 167 cells as exclusions. Review found
that several reasons establish only an incomplete probe or an unsuitable fixture,
not the absence of a client capability. The current projection reopens 85 cells
as `blocked` for exclusion-evidence review. This does not assert that all 85 are
supported. It means the recorded reason cannot yet justify removing them from
open work. Every reopened cell retains its original state and citation in
`previous_exclusion`; historical runs and receipts are unchanged.

The denominator stays at 94 operations / 376 cells. The 153 recorded passes and
four existing failures are unchanged. There are now 85 review-blocked cells and
52 not-started cells: 235 closed, 141 open, with 82 remaining exclusions. Remaining
exclusions are not automatically endorsed by this first audit.

| Reopened claim | Cells | Why the exclusion is insufficient |
|---|---:|---|
| Generic ArcPy module-list citation | 38 | Modules are not a complete inventory of tools, layer files, connection files, or CIM-backed access. Installed `arcpy.management.MakeWCSLayer` directly contradicts the WCS claim. |
| ArcPy `Map.addDataFromPath` service-type probe | 3 | Failure through one method does not exclude other native ArcPy entry points. |
| Unnamed missing QGIS provider | 30 | The citation supplies neither the provider name nor a receipt. Shared GDAL/OGR drivers must be considered; the installed GDAL exposes OGCAPI. |
| QGIS WCS 2.0.1 excluded using the dedicated WCS provider | 6 | The stock GDAL provider successfully loads WCS 2.0.1 and reads a pixel; the dedicated provider does not define the entire client surface. |
| Pro WCS 1.0 superseded by default negotiation | 3 | A default preference for 2.0.1 does not establish that explicitly selecting 1.0.0 is impossible. |
| Pro OGC API Tiles vector-only fixture | 2 | Pro documents map tiles support. Missing map tiles in a fixture is a setup or server metadata problem to investigate. |
| Pro OData excluded using an OGC classic service list | 2 | OData is not an OGC classic service; the citation cannot establish absence. |
| ArcPy SOAP excluded using a REST-only claim | 1 | Existing ArcPy GP SOAP execution contradicts the broad premise. SOAP catalog discovery needs its own probe. |

## Direct checks in this session

These are setup and SDK diagnostics, not native UI receipts or new certification
passes. The running server image was not replaced and no desktop project was
changed during this audit.

- Installed ArcPy reports version 3.7.1 and build 1901 through `GetInstallInfo`.
  The previously verified Pro executable reports 3.7.1.1904. Preserve both values
  and reconcile that binding before issuing new client certificates.
- `arcpy.management.MakeWCSLayer` is present; `GetParameterInfo` returns
  `in_wcs_coverage`, `out_wcs_layer`, `template`, and `band_index`. The tool is
  documented for Basic, Standard, and Advanced. An ArcPy-specific WCS client exists.
- Calls to that tool against the current fixture's
  `/ogc/services/test_service/wcs?coverage=coverage_0&version=1.0.0` and the
  corresponding `2.0.1` URL both returned `ERROR 999999` on September 20 at
  approximately 04:29 UTC. This diagnostic has not established the cause or which
  protocol operations the client reached. It is not a protocol pass and does not
  justify a no-client exclusion.
- QGIS 3.44.14's bundled GDAL reports version 3.13.3 and registers `OGCAPI`, `WCS`,
  `WMS`, `OAPIF`, and `STACIT`. A dedicated QGIS provider key is not the only
  possible native path. GDAL documents OGCAPI Tiles, Maps, and Coverage access,
  including experimental/older specification support; exact compatibility still
  requires a provider run and separate native UI evidence.
- A fresh stock `QgsVectorLayer` using provider `ogr` and URI
  `OGCAPI:https://host.docker.internal:18443/ogc/tiles/collections/0` is valid and
  `getFeatures()` returns feature ID 0 with fields including `objectid`, `uid`,
  `name`, `count`, and `status`. This directly disproves the blanket QGIS Tiles
  no-client claim. No plugin or adapter was loaded. Native UI execution and full
  operation/result validation remain required before awarding a pass.
  The first raster attempt reported a missing CA issuer; supplying the fixture's
  public Caddy root through `CURL_CA_BUNDLE` removed that TLS error without
  disabling verification. The raster layer then reported no raster band for
  this vector collection, while the vector layer read a feature successfully.
- A fresh `QgsRasterLayer` with provider `gdal` and a standard WCS_GDAL service
  definition selecting WCS 2.0.1 loads coverage_0 as 64x64, one band, EPSG:4326.
  A 2x2 block read returns 100.0 at row 0 / column 0, with `isNoData=false`.
  Thus the QGIS WCS 2.0.1 exclusions must also reopen. This is an SDK diagnostic,
  not a native UI pass or complete pixel/conformance certification.
  The reproducible probe is `honua-client-compat/scripts/probe-qgis-excluded-raster-paths.py`;
  its fresh redacted output is
  `honua-client-compat/evidence/exclusion-audit-qgis-20260920-a/observations.json`.
- The compatibility server image remains
  `sha256:f2e3ee8d7c3975efec1fe84fc9778a119dad77e7af55af8ff9caf3bd0123b4cf`.
  Allowlisted Docker environment inspection shows `ASPNETCORE_ENVIRONMENT=Development`
  and the following per-capability experimental flags already set to `true`:
  `serve.sensorthings`, `versioning.branch`, `serve.i3s-scene`, `sync.offline`,
  and `serve.3d-tiles-scene`. The inspection did not print credentials.
- An authenticated read of `/api/v1/admin/license/status` reports `isValid=true`,
  `validationState=Valid`, `edition=Enterprise`, and an active
  `editing.branch-versioning` entitlement. This establishes the current development
  fixture's server-side grant, not a production license or the ArcGIS client seat.
- `client-compat-postgres-1` has both `postgis` and `postgis_raster` extension
  version 3.4.3. `postgis_raster_lib_version()` returns `3.4.3 e365945`.
  `postgis.gdal_enabled_drivers` is `ENABLE_ALL`; `ST_GDALDrivers()` confirms
  read/write support for GTiff, PNG and JPEG. `raster_columns` lists
  `honua.raster_data.raster`. A missing raster extension is not the explanation
  for the current fixture's WCS failure. This inventory alone is not proof of
  correct raster data, session configuration or rendering.
- The live `/ogc/tiles/collections/0/tiles` returns vector tilesets. Adding
  `?f=png` to this metadata URL returned 400. The current source also has PNG
  rendering paths; raster representation discovery needs investigation rather
  than a no-client verdict.

## Licensing and flags

Honua's configuration supports both `Capabilities__Experimental__Enabled=true`
and `Capabilities__Experimental__<capability-id>__Enabled=true`. These flags do
not provide an entitlement, seed a dataset, install a client provider, or fix an
incorrect metadata response. Prefer explicit capability flags in a bound fixture.

The existing ArcPy reconcile diagnostic contains `ERROR 000824: The tool is not
licensed`. Fresh headless metadata again reports `ArcView`, with ArcEditor and
ArcInfo `NotLicensed`; this does not by itself establish the interactive Pro
named-user seat's entitlements. Esri documents additional licensing requirements
for reconcile/post. Check the actual authorized client license and server
entitlement separately. A missing entitlement is an execution blocker, not proof
that the protocol has no client. Do not bypass a vendor license check.

## Follow-through

For each reopened cell, name the exact client entry point, provider/driver and
version; check fixture data, feature flags, protocol enablement and entitlements;
then capture actual client requests and results. A fixture or license problem
stays open. A defensible no-client result needs operation-specific evidence that
also addresses shared providers, saved layers and documented alternate entry
points. Plugins/adapters must have separate scope and cannot silently certify a
stock native client.

Sources checked September 20, 2026:

- [Esri Make WCS Layer](https://doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/make-wcs-layer.html)
- [Esri Reconcile Versions](https://doc.esri.com/en/arcgis-pro/latest/tool-reference/data-management/reconcile-versions.html)
- [Esri OGC API service use](https://doc.esri.com/en/arcgis-pro/latest/help/data/services/use-ogc-api-services.html)
- [GDAL OGCAPI driver](https://gdal.org/en/stable/drivers/raster/ogcapi.html)
- [GDAL WCS driver and supported versions](https://gdal.org/en/stable/drivers/raster/wcs.html)
- [PostGIS GDAL driver inventory](https://postgis.net/docs/RT_ST_GDALDrivers.html)
- [PostGIS enabled-driver configuration](https://postgis.net/docs/postgis_gdal_enabled_drivers.html)

Validation: all 152 Python certification tests pass, including two new exclusion
regressions; checklist validation passes with the unchanged 376-cell denominator.
The broader test run exposed an existing Windows newline assumption in the raw
byte digest fixture. Writing that fixture as explicit bytes makes the test check
the intended byte sequence on Windows and Linux; production hashing is unchanged.
