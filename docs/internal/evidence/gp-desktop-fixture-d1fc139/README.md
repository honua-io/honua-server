# GPServer desktop fixture on trunk nightly d1fc139 (honua-server#4614)

Evidence identifier for the owned fixture on `nightly-d1fc139`, moved forward
from [`nightly-2cc2213`](../gp-desktop-fixture-2cc2213/README.md). This image
carries the server fixes for #4973 and #4974 found on the `8862065` fixture.
Ruling B keeps #4614 open until a native ArcGIS Pro UI receipt exists. No
desktop UI pass is claimed here. Every receipt records
`desktop_ui_exercised: false`.

Runner, licence, and session setup stay in private `honua-esri-compat`.

| Identity | Value |
|---|---|
| Image | `ghcr.io/honua-io/honua-server:nightly-d1fc139` (nightly build run [35142341579](https://github.com/honua-io/honua-server/actions/runs/35142341579)) |
| Index digest | `sha256:4bac230b40b0b07e396af54e2fc801420d2957bb31a6b6dafb3d402a8444d350` |
| Image revision label | `d1fc139a64ce33c817bd927bacb2103714221515`, `honua.runtime.compilation=native-aot` |
| Previous fixture image (superseded) | `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51` (nightly-2cc2213) |
| Catalog schema | 120 |
| Environment recorded on the fixture | `Production`, `Licensing__Mode=Disabled` |

`d1fc139` descends from `2cc2213` on trunk, so this fixture carries #4973 and
#4974 forward. The isolated before/after receipts stay in
[`../soap-argument-binding-4973/`](../soap-argument-binding-4973/README.md)
(6/18 then 18/18) and
[`../soap-services-site-root-4974/`](../soap-services-site-root-4974/README.md)
(2/5 then 5/5). Readiness on this image recorded bare `GET /services` 200 and
the captured `GetServiceDescriptionsEx` request 200. Those are server checks,
not a desktop pass.

## Replay evidence

An earlier draft of this page listed
`candidate-d1fc139-arcpy-and-sdk-scalar-verified.json` as passing before that
file existed. [Attempt 1](https://github.com/honua-io/honua-esri-compat/actions/runs/35151843737/attempts/1)
failed on 2026-09-17 while importing ArcPy, with Windows fatal exception
`0xe0000001`. It produced no uploaded receipt. The `2cc2213` receipts do not
prove this image.

[Attempt 2](https://github.com/honua-io/honua-esri-compat/actions/runs/35151843737/attempts/2)
passed on 2026-09-19. The retained
[SDK/ArcPy receipt](../../../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/EsriToolboxReplay/candidate-d1fc139-arcpy-and-sdk-scalar-verified.json)
records four passing checks: ArcGIS API for Python 2.4.3 and ArcPy 3.7.1 each
import 119 tasks and return area 12. The attempt 1 failure stays in the run
history. The later pass does not explain it.

The retained
[SOAP auth receipt](../../../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/EsriToolboxReplay/candidate-d1fc139-soap-auth-controls-verified.json)
passes 24/24 over verified HTTPS: authorized area 12, 401 for anonymous, invalid
key, and invalid bearer callers, and 400 for malformed authorized input.

The [complex-value replay](https://github.com/honua-io/honua-esri-compat/actions/runs/35433375337)
passed on 2026-09-19. Its
[receipt](../../../../tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/EsriToolboxReplay/candidate-d1fc139-arcpy-complex-values-verified.json)
records six passing ArcPy checks. `desktop_ui_exercised` is false.

Producer revisions: `903a0663a0dad13c6276ddbf4bc7f530ef5bfe38` (scalar) and
`16e1bd9c206ad2fb11f12bece58e1e604bf5e1ed` (complex).

## Still open

- **#4614:** native ArcGIS Pro UI receipt. Not linked.
- **#4975:** "Pro renders the ImageServer layer" remains a desktop observation. Not linked.
