# Use the .NET Studio lifecycle client

`Honua.Sdk.Studio` provides `IHonuaStudioPackageClient` and
`HonuaStudioPackageClient` for the family-agnostic draft, validation, version,
publication-request, reopen, and rollback API.

```csharp
using Honua.Sdk.Studio.Packages;

var packages = new HonuaStudioPackageClient(httpClient);
var draft = await packages.CreateDraftAsync(new CreateStudioPackageDraftRequest
{
    PackageKey = "planning-map",
    WorkspaceId = "planning",
    Envelope = new StudioPackageEnvelope
    {
        Family = StudioPackageFamily.Map,
        SchemaVersion = "1.0",
        Format = "honua_map_package.v1",
        Body = mapBody,
    },
});

var validation = await packages.ValidateDraftAsync(draft.DraftId);
var version = await packages.CreateContentVersionAsync(
    draft.DraftId,
    new SaveStudioContentVersionRequest { ChangeNote = "Ready for review" });
var request = await packages.CreatePublishRequestAsync(
    version.ItemId,
    version.VersionId,
    new CreateStudioPublicationRequest());
```

The client shipped from [honua-sdk-dotnet#252](https://github.com/honua-io/honua-sdk-dotnet/issues/252).
The 2026.1 SDK projection predates the polling endpoints added by
honua-server#3304, so poll
`GET /api/v1/studio/content-items/{itemId}/publish-requests/{requestId}` with the
same authenticated `HttpClient` until the SDK adds that convenience method.
The response remains `pending` until Console publishes the exact version, then
contains `status: "published"`, `publicationId`, and `publicUrl`.
