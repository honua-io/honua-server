# GP toolbox execution evidence (#4614, #4616)

The installed-client runner and independent Python oracle live in
`honua-esri-compat/scripts/replay-gp-toolbox.py` and
`honua-esri-compat/src/honua_esri_compat/gp_toolbox_oracle.py`.
These retained observations are not desktop certificates.

| Receipt | What it establishes |
| --- | --- |
| `pinned-7ba4226.json` | The then-pinned NativeAOT image reproduces SDK SyntaxError and ArcPy import failure. |
| `proposed-9f2f16a-sdk-verified.json` | Prerequisite fixes support actual SDK 2.4.3 catalog import and remote area 12 on proposed NativeAOT source 9f2f16a. |
| `managed-arcpy-and-sdk-verified.json` | Actual SDK 2.4.3 and licensed ArcPy 3.7.1 import all 119 tasks and remotely compute area 12 on the managed SOAP diagnostic image. |
| `managed-soap-trace.json` | Captured ArcPy SOAP operations, request shapes, six default environment controls, and successful HTTP statuses. |
| `soap-auth-controls.json` | Verified HTTPS anonymous/invalid-key requests return 401 SOAP faults; an authorized malformed submission returns 400. |
| `managed-metadata-arcpy-and-sdk-verified.json` | Both installed clients pass again after SOAP metadata reads were separated from result storage; run [34743150468](https://github.com/honua-io/honua-esri-compat/actions/runs/34743150468). |
| `nightly-3c52a4b-arcpy-scalar-verified.json` | On imaged nightly 3c52a4b (NativeAOT, contains #4760), ArcPy 3.7.1 imports all 119 tasks and remotely computes area 12. The same run records the separate SDK catalog-name check failure reported on #4616; run [34778082086](https://github.com/honua-io/honua-esri-compat/actions/runs/34778082086). |
| `nightly-3c52a4b-arcpy-complex-values-rejected.json` | Before this change, on the same nightly, ArcPy Buffer, multivalue Union, FeatureSet Clip, attribute filter, GenerateNearTable and cancel all fail at SubmitJob with HTTP 400; run [34778651549](https://github.com/honua-io/honua-esri-compat/actions/runs/34778651549). |
| `managed-complex-values-arcpy-verified.json` | On the managed diagnostic image of this change, ArcPy 3.7.1 loads RecordSet outputs and passes literal-derived oracles: Buffer bbox (-1,-1,4,5) with area inside the octagon/circle bounds; multivalue Union area 20; Clip of two FeatureSet inputs to [2,3]x[0,4] with area 4 and input attributes kept; attribute filter keeps the feature; GenerateNearTable returns a RecordSet row with distance 0; and cancellation reaches Cancelled (status 8). Run [34785122176](https://github.com/honua-io/honua-esri-compat/actions/runs/34785122176). |

The successful licensed run is
[34741056478](https://github.com/honua-io/honua-esri-compat/actions/runs/34741056478).
Source and image identities are retained in each receipt. The managed diagnostic
is explicitly labelled and must not be represented as a cut NativeAOT candidate.
The positive fixtures use Production with supported `Licensing__Mode=Disabled`.

`GPServerDurableRuntimeTests.SoapArea_WithProductionExecutor` separately proves
SOAP Execute and SubmitJob against the real canonical Redis job runtime and
production geometry executor. Both C# and Python fixtures encode literal
rectangle ordinates independently of server geometry serialization. Expected
area is width times height (`3 * 4`); assertions also cover MeasureResult type,
geometry.area identity, area measure, squared input-CRS units, SRID 3857 and Polygon.
Nodata does not apply to this vector scalar measure. Adapter tests separately
cover authorization, route binding, owner denial, status/messages/results,
truthful cancellation and synchronous failure faults; REST regression coverage
remains in the existing GPServer suites.

The adapter owner-denial test substitutes the job service. It therefore cannot show
what the canonical runtime does when another caller names a job.
`GPServerDurableRuntimeTests.SoapJobOperation_OtherCaller_IsDeniedByCanonicalJobOwnership`
closes that gap. It runs the real job service over the Redis job store with the
production executor, and substitutes only the operator grant so that two distinct
callers may both execute GP work.

- The owner submits the literal 3 by 4 area job through SOAP and waits for success.
- The second caller then sends `GetJobStatus`, `GetJobMessages`, `GetJobToolName`,
  `GetJobResult` and `CancelJob` for that job.
- Each call returns a 404 SOAP fault that leaks no status, task name or result.
  Without the ownership check a read would return 200, and a cancel of the
  already-terminal job would return 412.
- The owner still reads `esriJobSucceeded` and area 12 afterwards.

Job ownership, the SOAP job adapter and the operator evaluator are unchanged
between the pinned candidate `548b7a5` and the trunk that added this test; the
only GPServer source difference is the task-alias table.

`GPServerDurableRuntimeTests.SoapBuffer_WithProductionExecutor` and
`SoapUnion_WithMultiValueInput` prove SOAP RecordSet outputs and GPMultiValue inputs
against the same runtime, sending ArcPy's captured default controls. Their expected
bounding boxes, offset vertices and shoelace areas come from the literal input
rectangles. `GPServerSoapExecutionTests` reads the RecordSet captured from ArcPy
(`arcpy.AsShape`) through the REST FeatureSet translation, and round-trips a REST
FeatureSet result through a SOAP RecordSet.

The Python replay that produced the complex-value receipts is
`scripts/probe-gp-soap-complex.py` on honua-esri-compat branch
`probe/gp-soap-complex-4614`.

## Replay on the pinned 2026.1 candidate (548b7a5)

honua-release trunk `52cc3f2c` (#349) pins the candidate to source
`548b7a5263da5a3f2381eb43f232687cdf92b0bf`, NativeAOT index
`sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`.
That source contains #4760 and #4812. The owned `gpserver-4614-4616-server`
fixture was recreated on that exact image (Production,
`Licensing__Mode=Disabled`). Since #4722 the image needs a key-ring
certificate, so the fixture now gets a throwaway PKCS#12 and a private Redis. It
also uses a copy of the fixture catalog database. Through the fixture's HTTPS
proxy the image advertises all 119 GPServer tasks and answers the SOAP catalog.
The replay workflow verifies that the running container image is this digest
before it starts the client.

| Receipt | What it establishes |
| --- | --- |
| `candidate-548b7a5-arcpy-and-sdk-scalar-verified.json` | On the pinned candidate over verified TLS, installed SDK 2.4.3 imports all 119 advertised tasks. SDK and ArcPy 3.7.1 (documented SOAP `ImportToolbox` syntax, async job status 4 `Completed`) both remotely compute `geometry.area` = 12 for the literal 3 by 4 rectangle, with MeasureResult type, area measure, squared input-CRS units, SRID 3857 and Polygon input. Run [34915379462](https://github.com/honua-io/honua-esri-compat/actions/runs/34915379462). |
| `candidate-548b7a5-arcpy-complex-values-verified.json` | On the pinned candidate, installed ArcPy 3.7.1 passes the same literal-derived oracles as `managed-complex-values-arcpy-verified.json`: Buffer bbox (-1,-1,4,5) with area 29.12 inside the octagon/circle bounds; multivalue Union area 20; Clip of two FeatureSet inputs with area 4 and attributes kept; attribute filter area 12 with `label=keep`; GenerateNearTable RecordSet row with distance 0; and cancel reaching Cancelled (status 8). Run [34913953581](https://github.com/honua-io/honua-esri-compat/actions/runs/34913953581). |

| `candidate-548b7a5-soap-auth-controls-verified.json` | On the pinned candidate over verified TLS, SOAP `SubmitJob`, `Execute`, `GetJobStatus`, `GetJobMessages`, `GetJobToolName`, `GetJobResult` and `CancelJob` each return a 401 SOAP fault to an anonymous caller, an unknown `X-API-Key` and an unknown bearer token (21 denials). No denial leaks job status, task name, job id or result. As controls, the authorized caller's literal 3 by 4 area job still succeeds with area 12 and MeasureResult metadata, is still `esriJobSucceeded` after the refused cancels, and an authorized malformed submission returns 400. Produced by `probe-soap-auth-controls.py`. |

`soap-auth-controls.json` recorded the first three of these controls on the managed
diagnostic image only. The probe declares every expected status and value before
sending a request. It reads the authorized credential from the fixture container's
environment and refuses to write a receipt that contains it.
`GPServerDurableRuntimeTests.SoapJobOperation_UnauthenticatedCaller_IsChallengedWithoutJobState`
keeps the same controls as a regression. It runs the real API-key handler (the dev
bypass is off), the real job service, the Redis job store and the production
executor. It also asserts that no challenged submission creates a job.

## Replay on the re-pinned 2026.1 candidate (8862065)

honua-release trunk `31ed9cc4` (#354) re-pins the candidate to source
`886206527cc97bad1bbaa5fa6358910ebc45e9c0`, NativeAOT index
`sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388`.
The owned `gpserver-4614-4616-server` fixture was recreated on that exact image
with the same environment, key-ring bind, private Redis, fixture catalog copy and
HTTPS proxy as the `548b7a5` fixture; the catalog copy took migrations 118 and 119
on start-up (`dbSchema` 119). Through the fixture's HTTPS proxy the image again
advertises all 119 GPServer tasks, and `POST /services/desktop_ui_features/GPServer`
answers 401 to an unauthenticated caller rather than the 404 this issue was filed on.
The fixture identity, the operator hand-off (origin, service, CA root thumbprint,
leaf expiry) and the host-side recreation transcript are in
[`docs/internal/evidence/gp-desktop-fixture-8862065/`](../../../../../../../docs/internal/evidence/gp-desktop-fixture-8862065/README.md).

| Receipt | What it establishes |
| --- | --- |
| `candidate-8862065-arcpy-and-sdk-scalar-verified.json` | On the re-pinned candidate over verified TLS, installed SDK 2.4.3 and ArcPy 3.7.1 repeat the `548b7a5` scalar result: all 119 advertised tasks import, and both clients remotely compute `geometry.area` = 12 for the literal 3 by 4 rectangle with MeasureResult type, area measure, squared input-CRS units, SRID 3857 and Polygon input (ArcPy async job status 4 `Completed`). Run [35061750279](https://github.com/honua-io/honua-esri-compat/actions/runs/35061750279). |
| `candidate-8862065-arcpy-complex-values-verified.json` | On the re-pinned candidate, installed ArcPy 3.7.1 passes the same six literal-derived complex-value oracles as `candidate-548b7a5-arcpy-complex-values-verified.json`: Buffer feature output, multivalue Union, Clip of two FeatureSet inputs, attribute filter, GenerateNearTable RecordSet output, and cancellation reaching `Cancelled`. Run [35061927963](https://github.com/honua-io/honua-esri-compat/actions/runs/35061927963). |
| `candidate-8862065-soap-auth-controls-verified.json` | On the re-pinned candidate over verified TLS, the same 24 controls as `candidate-548b7a5-soap-auth-controls-verified.json` all hold: 21 denials across three unauthorized callers and seven SOAP operations, each a 401 SOAP fault leaking no job id, status, task name or result; the authorized literal job still succeeds with area 12 and is still `esriJobSucceeded` after the refused cancels; an authorized malformed submission returns 400. Produced by `probe-soap-auth-controls.py`. |

`gp-toolbox-replay.yml` compares its `honua_image` input against `docker inspect`'s
`.Image`, which is the bare local image id. Dispatch it with `sha256:...` alone;
the full `ghcr.io/honua-io/honua-server@sha256:...` reference fails the fixture
identity check even when the fixture is on the right image.

Native Pro desktop UI receipts are still separate. Each receipt above records
`desktop_ui_exercised: false`, and no desktop UI pass is claimed.

## Replay after the Pro SOAP discovery fixes (nightly d1fc139)

On 2026-09-19 the existing fixture was restarted on NativeAOT source
`d1fc139a64ce33c817bd927bacb2103714221515`, image
`sha256:4bac230b40b0b07e396af54e2fc801420d2957bb31a6b6dafb3d402a8444d350`.
This nightly contains the local-name SOAP argument binding and site-root fixes.
The [fixture evidence](../../../../../../../docs/internal/evidence/gp-desktop-fixture-d1fc139/README.md#replay-evidence-on-this-nightly)
records readiness, the earlier failed client run, and the fresh passing replays.

| Receipt | Proof |
| --- | --- |
| [SDK and ArcPy scalar](candidate-d1fc139-arcpy-and-sdk-scalar-verified.json) | Four checks: each installed client imports all 119 tasks and returns independently expected area 12 with measure metadata. |
| [ArcPy complex values](candidate-d1fc139-arcpy-complex-values-verified.json) | Six checks: Buffer, multivalue Union, FeatureSet Clip, attribute filter, near-table output and actual cancellation. |
| [SOAP authorization](candidate-d1fc139-soap-auth-controls-verified.json) | 24 checks: authorized area result, 21 denials without job-state leaks, intact owner job after denied cancellations, and malformed authorized input. |

These replays do not fulfill the operator-owned native Pro desktop criterion of
#4614. A passing native receipt and screenshots are still required; all three
files truthfully record `desktop_ui_exercised: false`.
