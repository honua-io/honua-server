# SOAP argument binding replay (server#4973)

ArcGIS Pro 3.7.1 could not list services: its `GetServiceDescriptionsEx` request
qualifies the operation but not the `FolderName` argument
(`elementFormDefault="unqualified"`), and the catalog rejected it with HTTP 400.
The GPServer adapter had the opposite defect: it accepted only unqualified
arguments and rejected qualified or default-namespace ones.

The fix binds SOAP arguments by local name in the shared SOAP read path,
`ArcGisSoapProtocol.BindArgumentsByLocalName`. The catalog, GPServer and
ImageServer adapters all go through it. The same change makes the fault for an
unrecognised argument name that element instead of reporting a count.

## What was replayed

`replay-soap-argument-binding.py` needs only the Python standard library. It posts
18 cases to a running server:

- Pro's captured envelope, byte for byte.
- The issue's isolation table: qualified, unqualified, default-namespace, casing,
  the legacy 9.0 namespace, an unrecognised `Recurse`, and a repeated `FolderName`.
- The same argument forms for GPServer `GetToolInfo` and `SubmitJob`
  (`geometry.area`).

Each case states the post-fix contract. The receipt records what the target
actually answered.

```
python3 replay-soap-argument-binding.py <base-url> geoprocessing <receipt.json> --label <text>
```

`SubmitJob` cases run only when `HONUA_REPLAY_API_KEY` holds an admin key. The key
is never written to a receipt.

## Targets

Both runs used the same PostGIS 16-3.4 database and private Redis, with
`ASPNETCORE_ENVIRONMENT=Production` and `Licensing__Mode=Disabled`. The only
published service was the seeded `geoprocessing` GPServer.

| Receipt | Target | Contract cases met |
| --- | --- | --- |
| `candidate-87966c3-before-fix.json` | Release-pinned candidate image: server `87966c3`, index `sha256:069f196bfa5c7201223d4d89868934242c4ace8805a6e48c122a88d84fa6eb1a`. This is the pre-fix control. | 6 / 18 |
| `branch-fix-4973-after-fix.json` | Diagnostic build of this branch at checkpoint `75fb3a1`, whose `src/` tree is identical to the PR head: framework-dependent `Honua.Server` output on a .NET runtime image, since NativeAOT publish is not available on the lane host. | 18 / 18 |

The candidate control reproduces the issue's table exactly:

- Pro's envelope and every unqualified `FolderName` return 400 with
  "accepts only one folderName argument".
- The qualified and default-namespace forms return 200.
- GPServer shows the mirror image: qualified and default-namespace `GetToolInfo`
  return 400, and `SubmitJob` returns 404 because the qualified `ToolName` is not
  bound.

The branch build returns 200 for every accepted form, with the same service
descriptions as `GetServiceDescriptions`, and names the element in each
unrecognised-argument fault.

`SubmitJob` with an unrecognised argument still returns HTTP 400 with the generic
"The GP operation failed." fault on both targets. That is the existing GP
execution fault mapping, which this change leaves alone. The binder's
named-element message is covered by
`GPServerSoapExecutionTests.Submission_UnrecognisedOrRepeatedArgument_NamesTheElement`.

The pinned candidate cannot contain this fix. The post-fix run on a release image
happens when the candidate is re-pinned past the merge.
