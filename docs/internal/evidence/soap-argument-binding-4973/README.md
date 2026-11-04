# SOAP argument binding replay (server#4973)

ArcGIS Pro 3.7.1 could not list services. Its `GetServiceDescriptionsEx` request
qualifies the operation but not the `FolderName` argument
(`elementFormDefault="unqualified"`), and the catalog rejected it with HTTP 400.
The GPServer adapter had the opposite defect: it accepted only unqualified
arguments and rejected qualified or default-namespace ones.

The fix binds SOAP arguments by local name in the shared SOAP read path,
`ArcGisSoapProtocol.BindArgumentsByLocalName`. The catalog, GPServer and
ImageServer adapters all go through it. The same change makes the fault for an
unrecognised argument name that element instead of reporting a count.

The replay producer stays with the evidence. This page records the receipts.

## Receipts

| Receipt | Target | Contract cases met |
| --- | --- | --- |
| `candidate-87966c3-before-fix.json` | Release-pinned candidate image: server `87966c3`, index `sha256:069f196bfa5c7201223d4d89868934242c4ace8805a6e48c122a88d84fa6eb1a`. Pre-fix control. | 6 / 18 |
| `branch-fix-4973-after-fix.json` | Diagnostic build at checkpoint `75fb3a1`. Its `src/` tree matches the PR head. | 18 / 18 |

Recorded outcomes:

- On the candidate, Pro's envelope and every unqualified `FolderName` return 400 with "accepts only one folderName argument". Qualified and default-namespace forms return 200.
- On the candidate, qualified and default-namespace GPServer `GetToolInfo` return 400, and `SubmitJob` returns 404 because the qualified `ToolName` is not bound.
- On the branch build, every accepted form returns 200 with the same service descriptions as `GetServiceDescriptions`, and each unrecognised-argument fault names the element.
- `SubmitJob` with an unrecognised argument still returns HTTP 400 with "The GP operation failed." on both targets. The binder's named-element message is covered by `GPServerSoapExecutionTests.Submission_UnrecognisedOrRepeatedArgument_NamesTheElement`.

The pinned candidate cannot contain this fix. A post-fix run on a release image
happens when the candidate is re-pinned past the merge. Native Pro confirmation
is evidence identifier `desktop-arcgis` in honua-esri-compat and is not linked
here.
