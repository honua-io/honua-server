// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Assets;

namespace Honua.Server.Features.Tasks;

// Pins the published form version a task is completed with; later publications never change a pinned task.
internal sealed class WorkTaskFormBinding(IAssetProfileStore profiles, AssetFormLookup forms, FormPackageValidator validator)
{
    public async Task<(WorkTaskFormReference? Form, AssetFieldError? Error)> PinAsync(
        WorkTaskCreateRequest request,
        (MetadataV2Service Service, HttpContext Context) target,
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

        var published = await forms.FindPublishedAsync(formId, target.Service, cancellationToken).ConfigureAwait(false);
        if (published is null || !await IsCompatibleAsync(request, published, target.Context).ConfigureAwait(false))
        {
            return (null, new AssetFieldError("formId", "The published form is not compatible with this task target."));
        }

        return (new WorkTaskFormReference { FormId = published.FormId, Version = published.Version }, null);
    }

    private async Task<bool> IsCompatibleAsync(WorkTaskCreateRequest request, FormPackageVersion form, HttpContext context)
    {
        if (form.Package.Target is not { } target)
        {
            return false;
        }

        var grant = await AssetServiceAccess.RequireLayerEditorAsync(context, request.ServiceId, target.LayerId).ConfigureAwait(false);
        if (grant.Denied is not null)
        {
            return false;
        }

        var profile = request.ProfileId is Guid id ? await profiles.GetAsync(id, context.RequestAborted).ConfigureAwait(false) : null;
        if (profile is not null && profile.LayerId != target.LayerId)
        {
            return false;
        }

        var validation = await validator.ValidateForPublishAsync(form.Package, context.RequestAborted).ConfigureAwait(false);
        return validation.IsValid;
    }
}
