// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

// Pins the published form version a task is completed with; later publications never change a pinned task.
internal sealed class WorkTaskFormBinding(IAssetProfileStore profiles, AssetFormLookup forms)
{
    public async Task<(WorkTaskFormReference? Form, AssetFieldError? Error)> PinAsync(
        WorkTaskCreateRequest request,
        MetadataV2Service service,
        CancellationToken cancellationToken)
    {
        var formId = request.FormId;
        if (string.IsNullOrWhiteSpace(formId) && request.ProfileId is Guid profileId)
        {
            var profile = await profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
            formId = request.Type == WorkTaskType.Inspection ? profile?.InspectionFormId : profile?.ServiceFormId;
        }

        if (string.IsNullOrWhiteSpace(formId))
        {
            return (null, new AssetFieldError("formId", "No form is bound: pass formId or set the profile's default form for this task type."));
        }

        var published = await forms.FindPublishedAsync(formId, service, cancellationToken).ConfigureAwait(false);
        return published is null
            ? (null, new AssetFieldError("formId", $"Form '{formId}' has no published version on this service."))
            : (new WorkTaskFormReference { FormId = published.FormId, Version = published.Version }, null);
    }
}
