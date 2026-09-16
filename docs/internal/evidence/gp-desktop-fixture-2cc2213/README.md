# GPServer desktop fixture on trunk nightly 2cc2213 (honua-server#4614, #4975)

The owned desktop fixture (`gpserver-4614-4616-*`) is where the operator runs the native ArcGIS Pro
and QGIS sessions (ruling B). Under operator ruling A (2026-09-16) the release pin stays frozen at
`87966c3`, and **this fixture tracks the newest imaged trunk nightly**, not the pin. This
directory records the fixture on `nightly-2cc2213`, the #4975 Postgres fix, and the installed-client
receipts re-minted on it.

No desktop UI pass is claimed here. Every receipt records `desktop_ui_exercised: false`.

| Identity | Value |
|---|---|
| Image | `ghcr.io/honua-io/honua-server:nightly-2cc2213` (nightly build run [35084811526](https://github.com/honua-io/honua-server/actions/runs/35084811526)) |
| Index digest = `docker inspect .Image` | `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51` |
| Image revision label | `2cc221388ea47d78c29e79eaee62737e4c792351`, `honua.runtime.compilation=native-aot` |
| Previous fixture image (superseded) | `sha256:0b16046533e5…` (candidate 8862065, [`../gp-desktop-fixture-8862065/`](../gp-desktop-fixture-8862065/README.md)) |
| Catalog schema | 120 (`120_AddStudioTenantOwnership` applied to the fixture catalog copy on first start) |
| Environment | `Production`, `Licensing__Mode=Disabled` |
| Postgres | `postgis/postgis:16-3.4` with `POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL` and `POSTGIS_ENABLE_OUTDB_RASTERS=1` |

## Recreate on the next nightly: one command

```bash
docs/internal/evidence/gp-desktop-fixture-2cc2213/recreate-fixture.sh sha256:<index digest of the nightly>
```

The image digest is the only input. Get it from the nightly build summary or from
`docker pull ghcr.io/honua-io/honua-server:nightly-<sha7>`. Pass the **bare** `sha256:…`. The same
bare value goes to `gp-toolbox-replay.yml` as `honua_image`, because that workflow compares it with
`docker inspect .Image`. A `ghcr.io/…@sha256:…` reference fails `Fixture image identity mismatch.`
even when the fixture is correct.

[`recreate-fixture.sh`](recreate-fixture.sh) does the following:

1. Pulls the digest and checks that the local image id equals it.
2. Makes sure `gpserver-4614-4616-postgres` runs with both PostGIS GDAL variables. If either is
   missing, it recreates the container on the **same** `gpserver-4614-4616-pgdata` volume, so the
   catalog data survives. It never removes a volume.
3. Recreates `gpserver-4614-4616-server` on the digest:
   - It copies only the old container's **explicitly-set** environment: the container env minus
     its image's env. Image defaults such as `HONUA_GIT_SHA` are left behind, so the fixture reports
     the new revision.
   - The values go through a 0600 file under
     `gpserver-4614-4616-evidence/recreate/` and are never printed. That file is reused if the
     server container is already gone.
   - The container keeps the `keyring.pfx` bind mount, `127.0.0.1:18164->8080` and both networks
     (`gpserver-4614-4616`, `gpserver-4614-4616-tls`), each with the alias `honua`.
   - On start, the server applies any pending migrations to the catalog copy.
4. Starts the siblings without recreating them: `gpserver-4614-548b7a5-redis`, the server's private
   Redis; `gpserver-4614-4616-tls`, Caddy on `127.0.0.1:18464->8443`; and
   `gpserver-4614-4616-tracer`, the relay Caddy proxies to. Without the tracer, the TLS surface
   answers 502.
5. Waits for `healthy`, then runs the readiness check. It exits non-zero unless every assertion
   holds.

The script refuses any `honua-esri-compat*` name. It never touches the licensed certification stack
and never takes anything down with `-v`.

## Readiness check

[`ready-check.sh`](ready-check.sh) `sha256:<digest>` can also be run on its own. All HTTP probes use
`--cacert` against the fixture Caddy root; certificate verification is never disabled.

| Assertion | Why |
|---|---|
| `docker inspect .Image` equals the digest, and the container is `running healthy` | Same identity check the licensed replay makes |
| Postgres env has `POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL` and `POSTGIS_ENABLE_OUTDB_RASTERS=1` | #4975 |
| Prints the latest applied catalog migration (dbSchema) | Schema matches the nightly |
| `GET /rest/info`, `GET /services?wsdl`, and `GET /rest/services/desktop_ui_features/GPServer` answer 200, with more than 0 tasks | The REST and SOAP surfaces Pro discovers |
| Unauthenticated SOAP `GetJobStatus` `POST /services/desktop_ui_features/GPServer` answers **401** | The route exists and challenges; #4614 was filed on a 404 |
| `GET …/native_raster_public/ImageServer/exportImage?f=image&format=tiff&…&size=256,256` answers 200 `image/tiff` and the body starts with TIFF magic (`II*\0` / `MM\0*`) | #4975. Esri errors come back as HTTP 200 with a JSON body, so the check tests the bytes, not only the status |

The exportImage assertion retries up to five times, 20 s apart. The server's Redis output cache is
kept in the private Redis, so it survives a server recreate. It also stores Esri error envelopes
because they are HTTP 200. This fixture showed the effect: a 501 cached before the Postgres fix
was served again for the rest of its 60 s TTL after the fix (product defect #4980). A real missing-driver failure fails
all five attempts.

## #4975: why ImageServer drew nothing, and what changed

`exportImage` renders through `ST_AsGDALRaster`. That needs the PostGIS GDAL output drivers enabled
in the database process. The fixture Postgres had been started with only the image's own
environment. `PostgresRasterStore` maps PostGIS's `Could not load the output GDAL driver` to
`NotSupportedException`, so every format answered
`{"error":{"code":501,…"The requested raster export is not supported by the configured provider."}}`.
[`before-recreate.txt`](before-recreate.txt) is that state: the Postgres env keys, the 501 body, and
`ready-check.sh` refusing the old fixture.

This lane stopped and removed the Postgres container, then ran it again on the preserved
`gpserver-4614-4616-pgdata` volume with both variables. It did **not** re-seed. When the server
started on the nightly, it migrated the preserved catalog from 119 to 120. After that,
`exportImage?format=tiff` returns 200 `image/tiff` (525 068 bytes for 256×256), and `ST_GDALDrivers()`
lists 152 drivers. [`recreate-transcript.txt`](recreate-transcript.txt) is the full one-command
run, ending `READY`.

Whether Pro **renders** the ImageServer layer is a desktop observation for the operator's session.
This lane does not claim it.

## Operator hand-off for the native Pro session

| What | Value |
|---|---|
| Origin | `https://127.0.0.1:18464` |
| GP service | `desktop_ui_features` (GPServer, 119 advertised tasks) |
| `ImportToolbox` argument | `https://127.0.0.1:18464/services;desktop_ui_features` |
| ImageServer (#4975) | `https://127.0.0.1:18464/rest/services/native_raster_public/ImageServer` |
| Image digest / dbSchema | `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51` / 120 |
| Trust anchor | `CN = Caddy Local Authority - 2026 ECC Root` |
| Root SHA-256 thumbprint | `09A5CE33497BE0CF8E4B631E06010F807FA288B2F1DE635F19E6A29B0E8D51F0` |
| Root validity | 2026-09-06T05:08:55Z → 2036-07-15T05:08:55Z |
| Root PEM on the host | `/home/mike/honua-io/gpserver-4614-4616-evidence/ca/root.crt` |
| Served leaf | `CN = 127.0.0.1`, SAN `IP:127.0.0.1, DNS:localhost`, issued directly by the root |
| **Leaf validity** | 2026-09-15T08:35:33Z → **2026-09-22T08:35:33Z** |
| Credential | The fixture container's `HONUA_ADMIN_PASSWORD`, sent as `X-API-Key`. Read it from `docker inspect gpserver-4614-4616-server`. It is deliberately absent from this repo |

The Caddy container and its leaf were not changed. A session after 2026-09-22 needs the leaf reissued
first; the root stays the same.

## Receipts re-minted on this nightly

All three receipts are under
`tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/EsriToolboxReplay/`. Apart
from `at`, `source` and `image`, each is identical to its `candidate-8862065-*` counterpart.

| Receipt | Run | What it establishes |
|---|---|---|
| `candidate-2cc2213-arcpy-and-sdk-scalar-verified.json` | [35131473081](https://github.com/honua-io/honua-esri-compat/actions/runs/35131473081) (`trunk`, `client_set=both`) | Over verified TLS, installed ArcGIS SDK 2.4.3 and licensed ArcPy 3.7.1 both import all 119 advertised tasks. Both remotely compute `geometry.area` = 12 for the literal 3 by 4 rectangle. |
| `candidate-2cc2213-arcpy-complex-values-verified.json` | [35131750779](https://github.com/honua-io/honua-esri-compat/actions/runs/35131750779) (`probe/gp-soap-complex-4614`, `client_set=arcpy`) | ArcPy 3.7.1 passes all six complex-value oracles: Buffer feature output, multivalue Union, Clip of two FeatureSet inputs, attribute filter, GenerateNearTable RecordSet output, and truthful `Cancelled` cancellation. |
| `candidate-2cc2213-soap-auth-controls-verified.json` | local, `probe-soap-auth-controls.py` | 24/24 controls pass. The authorized literal job succeeds with area 12. Anonymous callers, an unknown `X-API-Key` and an unknown bearer each get a 401 SOAP fault from all seven operations, with nothing leaked. The owner's job survives the refused cancels. A malformed authorized submission returns 400. |

## What is still open

- **#4614:** the native ArcGIS Pro UI receipt belongs to the operator (ruling B). The licensed runner
  runs ArcPy and the .NET SDK, not an interactive Pro session. The Pro-reported SOAP defects filed
  from the 8862065 session (#4973 and siblings) are separate issues. This lane neither fixes nor
  re-tests them.
- **#4975:** "Pro renders the ImageServer layer" is the operator's desktop observation. The fixture
  side is done: the recipe sets the variables, readiness exercises `exportImage`, and the fixture
  returns TIFF bytes.
