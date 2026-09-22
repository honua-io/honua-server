# GPServer desktop fixture on candidate pin 8862065 (honua-server#4614, ruling B)

Operator ruling B (2026-09-05) keeps #4614 open until the operator runs a **native ArcGIS Pro UI
session** against the owned GP fixture. This directory records the lane half of that hand-off:
the owned fixture was recreated on the re-pinned 2026.1 candidate, the installed-client receipts
were re-minted on it, and the origin and trust anchor the operator needs are stated below.

No desktop UI pass is claimed here. Every receipt in this change records
`desktop_ui_exercised: false`.

| Identity | Value |
|---|---|
| Release manifest | honua-io/honua-release@`31ed9cc44ffa42f8d96a16643460bb1842a30318` (PR #354, second re-pin) |
| Candidate image | `ghcr.io/honua-io/honua-server@sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388` |
| Local image id (`docker inspect .Image`) | `sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388` |
| Image revision label | `886206527cc97bad1bbaa5fa6358910ebc45e9c0`, `honua.runtime.compilation=native-aot` |
| Previous fixture pin (superseded) | `sha256:29974ee7b722…` (548b7a5) |
| Catalog schema after start-up | 119 (`118_AddReplicaScopeDefinition`, `119_CreateGeoprocessingWorkspaces` applied to the fixture catalog copy) |
| Environment | `Production`, `Licensing__Mode=Disabled` |

## Operator hand-off for the native Pro session

| What | Value |
|---|---|
| Origin | `https://127.0.0.1:18464` |
| Service | `desktop_ui_features` (GPServer, 119 advertised tasks) |
| `ImportToolbox` argument | `https://127.0.0.1:18464/services;desktop_ui_features` |
| Trust anchor | `CN = Caddy Local Authority - 2026 ECC Root` |
| Root SHA-256 thumbprint | `09A5CE33497BE0CF8E4B631E06010F807FA288B2F1DE635F19E6A29B0E8D51F0` |
| Root validity | 2026-09-06T05:08:55Z → 2036-07-15T05:08:55Z |
| Served leaf | `CN = 127.0.0.1`, SAN `IP:127.0.0.1, DNS:localhost`, issued directly by the root |
| **Leaf validity** | 2026-09-15T08:35:33Z → **2026-09-22T08:35:33Z** |
| Root PEM on the host | `/home/mike/honua-io/gpserver-4614-4616-evidence/ca/root.crt` |
| Credential | the fixture container's `HONUA_ADMIN_PASSWORD`, sent as `X-API-Key`; read it from `docker inspect gpserver-4614-4616-server` — it is deliberately absent from this repo |

The leaf expires 2026-09-22. A Pro session after that date needs the fixture Caddy to reissue
before the run; the root does not change.

## What this lane changed on the host

The fixture is a hand-run container family on the shared Docker engine, not a compose project.
The whole family had exited (255) at the 2026-09-15 power loss, and the server container was still
on the 548b7a5 digest — which is why replay run
[35059447573](https://github.com/honua-io/honua-esri-compat/actions/runs/35059447573) failed with
`Fixture image identity mismatch.` before this lane.

- `gpserver-4614-4616-server` was removed and re-run on the candidate digest with **identical**
  explicitly-set environment, the `keyring.pfx` bind mount, `127.0.0.1:18164->8080/tcp`, and both
  networks (`gpserver-4614-4616`, `gpserver-4614-4616-tls`) with alias `honua`. Only the image
  reference changed; the four image-default variables the old container had inherited
  (including `HONUA_GIT_SHA`) were *not* copied forward, so the new container reports the
  candidate's own revision.
- Siblings were restarted, never recreated: `gpserver-4614-548b7a5-redis` (the server's private
  Redis), `gpserver-4614-4616-postgres` (fixture catalog copy), `gpserver-4614-4616-tls` (Caddy,
  `127.0.0.1:18464->8443`) and `gpserver-4614-4616-tracer` (the sanitising relay Caddy proxies to
  on `127.0.0.1:8082` inside the TLS container's network namespace).
- The licensed certification stack (`honua-esri-compat-*` containers, networks and volumes) was
  not touched, and nothing was taken down with `-v`.

`fixture-identity.txt` is the verbatim identity and migration transcript. `tls-surface.txt` is the
TLS check and the four endpoint probes, all made with `--cacert` against the fixture root — no
certificate verification was disabled.

`POST /services/desktop_ui_features/GPServer` answers **401** to an unauthenticated caller. That is
the route existing and challenging; the defect #4614 was filed on was a **404** at that path.

## Receipts re-minted on this pin

All three are committed under
`tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/Fixtures/EsriToolboxReplay/`.

| Receipt | Run | What it establishes |
|---|---|---|
| `candidate-8862065-arcpy-and-sdk-scalar-verified.json` | [35061750279](https://github.com/honua-io/honua-esri-compat/actions/runs/35061750279) | Installed ArcGIS SDK 2.4.3 and licensed ArcPy 3.7.1 both import all 119 advertised tasks and remotely compute `geometry.area` = 12 for the literal 3 by 4 rectangle (MeasureResult, `input-crs-units-squared`, SRID 3857, Polygon; ArcPy async job status 4 `Completed`). |
| `candidate-8862065-arcpy-complex-values-verified.json` | [35061927963](https://github.com/honua-io/honua-esri-compat/actions/runs/35061927963) | ArcPy 3.7.1 passes the six complex-value oracles on this pin: Buffer feature output, multivalue Union, Clip of two FeatureSet inputs, attribute filter, GenerateNearTable RecordSet output, and truthful `Cancelled` cancellation. |
| `candidate-8862065-soap-auth-controls-verified.json` | local, `probe-soap-auth-controls.py` | 24/24 controls: the authorized literal job reaches `esriJobSucceeded` with area 12; anonymous, unknown `X-API-Key` and unknown bearer callers each get a 401 SOAP fault from all seven SOAP operations (21 denials) leaking no job id, status, task name or result; the owner's job is still `esriJobSucceeded` after the refused cancels; an authorized malformed submission returns 400. |

### Dispatch note for the next re-pin

`gp-toolbox-replay.yml` compares `docker inspect`'s `.Image` — the **bare local image id** — against
its `honua_image` input. Dispatching it with the full `ghcr.io/honua-io/honua-server@sha256:…`
reference fails `Fixture image identity mismatch.` even when the fixture is correct; run
[35061647383](https://github.com/honua-io/honua-esri-compat/actions/runs/35061647383) is that
failure on an already-correct fixture. Pass `sha256:…` alone.

## What is still open on #4614

The native ArcGIS Pro UI receipt. It is operator-owned under ruling B and cannot be produced by
this lane — the licensed runner executes ArcPy and the .NET SDK, not an interactive Pro session.
The fixture is up, on the pinned candidate, and ready for it.
