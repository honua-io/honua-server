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
using Honua.Sdk.Studio.Content;

var draft = await content.CreateDraftAsync(new CreateStudioContentDraftRequest
{
    PackageKey = "parcels-review",
    Envelope = new StudioContentEnvelope
    {
        Family = StudioContentKind.Map,
        SchemaVersion = "honua_map_package.v1",
    },
});

var version = await content.CreateContentVersionAsync(
    draft.DraftId,
    new SaveStudioContentVersionRequest { ChangeNote = "Initial review map" });

var saved = await content.GetVersionAsync(version.ItemId, version.VersionId);
var reopened = await content.ReopenVersionAsync(saved.ItemId, saved.VersionId);
```

`content` is an `IHonuaStudioContentClient`. Use the request and model names
from the installed SDK version. `SubmitPublishRequestAsync` separates a created
publication request (HTTP 201) from an operation that needs approval (HTTP 202),
as described in the
[SDK's Studio README](https://github.com/honua-io/honua-sdk-dotnet/blob/trunk/src/Honua.Sdk.Studio/README.md);
a pending or rejected request has no active URL. The
[MCP publication tool](mcp-tools.md) always files a proposal for a person to
approve. That MCP behavior is not a substitute for checking the installed .NET
client's REST contract.
