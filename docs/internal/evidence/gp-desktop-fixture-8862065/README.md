# GPServer desktop fixture on candidate pin 8862065 (honua-server#4614, ruling B)

Evidence identifier for the owned fixture on the re-pinned 2026.1 candidate
`8862065`. Ruling B (2026-09-05) keeps #4614 open until a native ArcGIS Pro UI
receipt exists. No desktop UI pass is claimed here. Every receipt records
`desktop_ui_exercised: false`.

Runner, licence, and session setup stay in private `honua-esri-compat`.

| Identity | Value |
|---|---|
| Release manifest | honua-io/honua-release@`31ed9cc44ffa42f8d96a16643460bb1842a30318` (PR #354, second re-pin) |
| Candidate image | `ghcr.io/honua-io/honua-server@sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388` |
| Image revision label | `886206527cc97bad1bbaa5fa6358910ebc45e9c0`, `honua.runtime.compilation=native-aot` |
| Previous fixture pin (superseded) | `sha256:29974ee7b722…` (548b7a5) |
| Catalog schema | 119 |

## Receipts

Receipts are under
`tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/EsriToolboxReplay/`.

| Receipt | Run | Result |
|---|---|---|
| `candidate-8862065-arcpy-and-sdk-scalar-verified.json` | [35061750279](https://github.com/honua-io/honua-esri-compat/actions/runs/35061750279) | Pass. ArcGIS SDK 2.4.3 and ArcPy 3.7.1 each import 119 advertised tasks and return area 12. Async job status 4 `Completed`. |
| `candidate-8862065-arcpy-complex-values-verified.json` | [35061927963](https://github.com/honua-io/honua-esri-compat/actions/runs/35061927963) | Pass. Six ArcPy complex-value checks, including truthful `Cancelled`. |
| `candidate-8862065-soap-auth-controls-verified.json` | local probe retained with the fixture | Pass. 24/24. Authorized job reaches `esriJobSucceeded` with area 12. Anonymous, unknown key, and unknown bearer callers receive a 401 SOAP fault from all seven SOAP operations (21 denials). Malformed authorized input returns 400. |

`POST /services/desktop_ui_features/GPServer` answers 401 to an unauthenticated
caller. #4614 was filed on a 404 at that path.

## Still open

**#4614:** native ArcGIS Pro UI receipt. Not linked. `desktop_ui_exercised` stays false.
