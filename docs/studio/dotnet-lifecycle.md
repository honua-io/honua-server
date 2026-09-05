# Use the .NET lifecycle client

`Honua.Sdk.Studio` exposes the durable draft/version lifecycle independently
of the preview browser UI. The candidate SDK can create and update a draft,
validate it, save an immutable version, fetch or compare versions, reopen a
version as a new draft, and create/poll a publish request.

```csharp
var draft = await packages.CreateDraftAsync(new CreateStudioPackageDraftRequest
{
    PackageKey = "parcels-review",
    Envelope = new StudioPackageEnvelope
    {
        Family = StudioPackageFamily.Map,
        SchemaVersion = "honua_map_package.v1",
    },
});

var version = await packages.CreateContentVersionAsync(
    draft.DraftId,
    new SaveStudioContentVersionRequest { ChangeNote = "Initial review map" });

var saved = await packages.GetVersionAsync(version.ItemId, version.VersionId);
var reopened = await packages.ReopenVersionAsync(saved.ItemId, saved.VersionId);
```

Use the exact request/model names from the installed SDK version; this snippet
must not be treated as a published-package receipt. Publication methods exist,
but a pending or rejected request has no public URL, and these docs do not
claim the blocked end-to-end approval journey is ready. Track
[#3304](https://github.com/honua-io/honua-server/issues/3304).
