# GPServer desktop fixture on trunk nightly d1fc139 (honua-server#4614)

The owned desktop fixture (`gpserver-4614-4616-*`) is where the operator runs the native ArcGIS Pro
and QGIS sessions (ruling B). Under operator ruling A (2026-09-16) the release pin stays frozen at
`87966c3`, and **this fixture tracks the newest imaged trunk nightly**, not the pin. This directory
records the fixture moved forward from `nightly-2cc2213`
([`../gp-desktop-fixture-2cc2213/`](../gp-desktop-fixture-2cc2213/README.md)) to `nightly-d1fc139`,
which carries the fixes for the two SOAP defects the operator's native Pro session found on the
`8862065` fixture: #4973 (Pro could not enumerate services — unqualified SOAP argument elements) and
#4974 (the site-root connection form failed — bare `GET /services`).

No desktop UI pass is claimed here. Every receipt records `desktop_ui_exercised: false`.

| Identity | Value |
|---|---|
| Image | `ghcr.io/honua-io/honua-server:nightly-d1fc139` (nightly build run [35142341579](https://github.com/honua-io/honua-server/actions/runs/35142341579)) |
| Index digest = `docker inspect .Image` | `sha256:4bac230b40b0b07e396af54e2fc801420d2957bb31a6b6dafb3d402a8444d350` (confirmed with `docker buildx imagetools inspect`) |
| Image revision label | `d1fc139a64ce33c817bd927bacb2103714221515`, `honua.runtime.compilation=native-aot` |
| Previous fixture image (superseded) | `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51` (nightly-2cc2213, [`../gp-desktop-fixture-2cc2213/`](../gp-desktop-fixture-2cc2213/README.md)) |
| Catalog schema | 120 (`120_AddStudioTenantOwnership`; unchanged since `2cc2213` — no newer migration on this sha) |
| Environment | `Production`, `Licensing__Mode=Disabled` |
| Postgres | `postgis/postgis:16-3.4` with `POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL` and `POSTGIS_ENABLE_OUTDB_RASTERS=1` (#4975, carried forward unchanged) |

`d1fc139` is a direct descendant of `2cc2213` on trunk (`git merge-base --is-ancestor` holds), so
this recreate carries #4973/#4974 forward on top of the #4975 Postgres fix; nothing regressed.

## Recreate: one command

```bash
docs/internal/evidence/gp-desktop-fixture-d1fc139/recreate-fixture.sh sha256:4bac230b40b0b07e396af54e2fc801420d2957bb31a6b6dafb3d402a8444d350
```

Unchanged from the `2cc2213` runbook (copied forward verbatim): the image digest is the only input,
it preserves the seeded Postgres catalog and the GDAL environment, and never removes a volume. See
[`../gp-desktop-fixture-2cc2213/README.md`](../gp-desktop-fixture-2cc2213/README.md) for the full
step-by-step description of what the script does. [`recreate-transcript.txt`](recreate-transcript.txt)
is this lane's full run, ending `READY`.

The same **bare** `sha256:…` digest is the `honua_image` input to the `GP toolbox client replay`
workflow in `honua-esri-compat` (`gp-toolbox-replay.yml`), because that workflow compares it with
`docker inspect .Image`.

## Readiness check: two new assertions for #4973 and #4974

[`ready-check.sh`](ready-check.sh) carries forward every `2cc2213` assertion (image identity,
Postgres GDAL drivers, catalog schema, the REST/SOAP surfaces, the unauthenticated `GetJobStatus`
401, and the `exportImage` TIFF bytes) and adds two, both green on this nightly:

| Assertion | Why |
|---|---|
| `GET /services` answers 200 with the catalog WSDL | #4974. Pro's site-root connection form is a bare GET with no `?wsdl`; before the fix this 404'd. |
| `POST /services` with ArcGIS Pro 3.7.1's captured `GetServiceDescriptionsEx` envelope (byte for byte, unqualified `<FolderName>` child) answers 200 and the response's `ServiceDescription` list includes at least one `Type=GPServer` entry | #4973. Before the fix, GPServer's SOAP binder required qualified arguments while the catalog's `GetServiceDescriptionsEx` needed exactly this unqualified form rejected with a 400 — this is the request shape Pro actually sends. |

This run found 14 services total, with 4 of type `GPServer`:
`browser_compat`, `desktop_ui_features`, `native_raster_public`, `qgis_extended_native`.

## Before/after for #4973 and #4974

The definitive before/after replay for both defects — the release-pinned `87966c3` control
reproducing the issue table (6/18 and 2/5 contract cases met) against the diagnostic branch build
(18/18 and 5/5) — lives in
[`../soap-argument-binding-4973/`](../soap-argument-binding-4973/README.md) and
[`../soap-services-site-root-4974/`](../soap-services-site-root-4974/candidate-87966c3-before-fix.json),
recorded when PRs #4983/#4985 were built. This lane does not re-run that isolated replay; it
confirms the same fix on the fixture's own release image with the two `ready-check.sh` assertions
above, both passing on `nightly-d1fc139` ([`recreate-transcript.txt`](recreate-transcript.txt)).

## Operator hand-off for the native Pro session

| What | Value |
|---|---|
| Origin | `https://127.0.0.1:18464` |
| GP service | `desktop_ui_features` (GPServer, 119 advertised tasks) |
| `ImportToolbox` argument | `https://127.0.0.1:18464/services;desktop_ui_features` |
| ImageServer (#4975) | `https://127.0.0.1:18464/rest/services/native_raster_public/ImageServer` |
| Image digest / dbSchema | `sha256:4bac230b40b0b07e396af54e2fc801420d2957bb31a6b6dafb3d402a8444d350` / 120 |
| Trust anchor | `CN = Caddy Local Authority - 2026 ECC Root` |
| Root SHA-256 thumbprint | `09A5CE33497BE0CF8E4B631E06010F807FA288B2F1DE635F19E6A29B0E8D51F0` |
| Root PEM on the host | `/home/mike/honua-io/gpserver-4614-4616-evidence/ca/root.crt` |
| Served leaf | `CN = 127.0.0.1`, SAN `IP:127.0.0.1, DNS:localhost`, issued directly by the root |
| **Leaf validity** | 2026-09-15T08:35:33Z → **2026-09-22T08:35:33Z** (unchanged — the Caddy container and its leaf were not touched) |
| Credential | The fixture container's `HONUA_ADMIN_PASSWORD`, sent as `X-API-Key`. Read it from `docker inspect gpserver-4614-4616-server`. It is deliberately absent from this repo |

A session after 2026-09-22 needs the leaf reissued first; the root stays the same.

## Replay evidence on this nightly

The earlier version of this page listed
`candidate-d1fc139-arcpy-and-sdk-scalar-verified.json` as passing. That file was never
committed. The dispatched [licensed replay, attempt 1](https://github.com/honua-io/honua-esri-compat/actions/runs/35151843737/attempts/1)
failed on 2026-09-17 while importing ArcPy, with Windows fatal exception
`0xe0000001` in `arcpy.geoprocessing._base`. It produced no uploaded receipt.
The successful installed-client receipts on `2cc2213` remain historical evidence;
they do not prove the `d1fc139` replay.

On 2026-09-19 the owned fixture was found stopped after a host shutdown. Its
existing containers were restarted in dependency order (Postgres, private Redis,
server, TLS proxy, then tracer); its image, catalog and TLS certificate were
preserved. The unchanged `ready-check.sh` passed on the digest above: verified
HTTPS, 119 advertised tasks, Pro's captured `GetServiceDescriptionsEx` envelope
returning 200, bare `/services` returning 200, unauthenticated SOAP job access
returning 401, and `exportImage` returning TIFF bytes. These are server readiness
checks, not desktop observations.

The unchanged `probe-soap-auth-controls.py` was also rerun on this digest. Its
[receipt](../../../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/EsriToolboxReplay/candidate-d1fc139-soap-auth-controls-verified.json)
passes all 24 checks over verified HTTPS:

- The literal 3 by 4 rectangle returns independently expected area 12, with
  `MeasureResult`, `geometry.area`, SRID 3857 and Polygon metadata.
- Anonymous, invalid API-key and invalid bearer callers each receive a 401 SOAP
  fault from all seven execution/job operations, with no job state disclosed.
- Denied cancellations leave the owner's successful job intact, and malformed
  authorized input returns 400.

The receipt explicitly records `desktop_ui_exercised: false`.

## What is still open

- **#4614:** the native ArcGIS Pro UI receipt belongs to the operator (ruling B). This lane hands
  off the fixture and stops; it does not run the Pro UI.
- **#4975:** "Pro renders the ImageServer layer" remains the operator's desktop observation. The
  fixture side stays proven by `ready-check.sh`'s `exportImage` assertion.

Fixture is on nightly-d1fc139 with #4983/#4985; ready for the operator's native Pro session
(ruling B).
