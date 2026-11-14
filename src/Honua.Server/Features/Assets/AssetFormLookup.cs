// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Assets;

// Forms bound to profiles and tasks must be published and belong to the same service.
internal sealed class AssetFormLookup(IFormPackageStore forms, IMetadataV2GraphProvider graph)
{
    public async Task<FormPackageVersion?> FindPinnedAsync(WorkTask task, CancellationToken cancellationToken)
    {
        var snapshot = await graph.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = snapshot.FindServiceByNameOrId(task.ServiceId);
        var form = await forms.GetVersionAsync(task.Form.FormId, task.Form.Version, cancellationToken).ConfigureAwait(false);
        return service is not null && form is not null && AssetServiceAccess.Matches(service, form.Package.Target?.ServiceId)
            ? form : null;
    }

    public async Task<FormPackageVersion?> FindPublishedAsync(string formId, MetadataV2Service service, CancellationToken cancellationToken)
    {
        var published = await forms.GetCurrentVersionAsync(formId, FormPackageStatus.Published, cancellationToken).ConfigureAwait(false);
        return published is not null && AssetServiceAccess.Matches(service, published.Package.Target?.ServiceId)
            ? published
            : null;
    }
}
