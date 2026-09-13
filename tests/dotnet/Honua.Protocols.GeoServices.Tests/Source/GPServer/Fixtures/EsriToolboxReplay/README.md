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

The release's candidate-specific replay and fresh native Pro desktop UI receipts
remain pending a candidate containing the SOAP fix. No desktop UI pass or complete
SOAP complex-value compatibility is claimed by these observations.
