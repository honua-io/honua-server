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

The release's candidate-specific replay and fresh native Pro desktop UI receipts
remain pending a candidate containing these SOAP changes. No desktop UI pass is
claimed by these observations.
