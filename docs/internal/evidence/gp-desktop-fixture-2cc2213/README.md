# GPServer desktop fixture on trunk nightly 2cc2213 (honua-server#4614, #4975)

Evidence identifier for the owned fixture on `nightly-2cc2213`. Ruling B keeps
#4614 open until a native ArcGIS Pro UI receipt exists. No desktop UI pass is
claimed here. Every receipt records `desktop_ui_exercised: false`.

Runner, licence, and session setup stay in private `honua-esri-compat`.

| Identity | Value |
|---|---|
| Image | `ghcr.io/honua-io/honua-server:nightly-2cc2213` (nightly build run [35084811526](https://github.com/honua-io/honua-server/actions/runs/35084811526)) |
| Index digest | `sha256:61e06ef3a94d00e4c8fc57ce93e008a5e31b2dcf1da5deb22781fdd42d2d4e51` |
| Image revision label | `2cc221388ea47d78c29e79eaee62737e4c792351`, `honua.runtime.compilation=native-aot` |
| Previous fixture image (superseded) | `sha256:0b16046533e5…` (candidate 8862065, [`../gp-desktop-fixture-8862065/`](../gp-desktop-fixture-8862065/README.md)) |
| Catalog schema | 120 (`120_AddStudioTenantOwnership`) |

## Receipts

Receipts are under
`tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/GeoServicesToolboxReplay/`.

| Receipt | Run | Result |
|---|---|---|
| `candidate-2cc2213-arcpy-and-sdk-scalar-verified.json` | [35131473081](https://github.com/honua-io/honua-esri-compat/actions/runs/35131473081) | Pass. ArcGIS SDK 2.4.3 and ArcPy 3.7.1 each import 119 advertised tasks and return area 12. |
| `candidate-2cc2213-arcpy-complex-values-verified.json` | [35131750779](https://github.com/honua-io/honua-esri-compat/actions/runs/35131750779) | Pass. Six ArcPy complex-value checks. |
| `candidate-2cc2213-soap-auth-controls-verified.json` | local probe retained with the fixture | Pass. 24/24. Authorized job area 12. Anonymous, unknown key, and unknown bearer callers receive 401. Malformed authorized input returns 400. |

## Still open

- **#4614:** native ArcGIS Pro UI receipt. Not linked. `desktop_ui_exercised` stays false.
- **#4975:** a desktop observation that Pro renders the ImageServer layer. Not linked. Server-side `exportImage` TIFF readiness on this fixture is not that observation.
