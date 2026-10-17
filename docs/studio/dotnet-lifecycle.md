---
type: guide
title: "Use the .NET lifecycle client"
description: "Honua.Sdk.Studio exposes the durable draft/version lifecycle independently of the preview browser UI."
---
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

Use the request and model names from the installed SDK version. Follow the
publication method's actual returned status and its documented polling contract;
a pending or rejected request has no active URL. The
[MCP publication tool](mcp-tools.md) has an admin direct-publication branch and
a non-admin proposal branch. That MCP behavior is not a substitute for checking
the installed .NET client's REST contract.
