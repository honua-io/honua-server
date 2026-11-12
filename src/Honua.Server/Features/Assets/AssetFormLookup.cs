// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Assets;

// Forms bound to profiles and tasks must be published and belong to the same service.
internal sealed class AssetFormLookup(IFormPackageStore forms)
{
    public async Task<FormPackageVersion?> FindPublishedAsync(string formId, MetadataV2Service service, CancellationToken cancellationToken)
    {
        var published = await forms.GetCurrentVersionAsync(formId, FormPackageStatus.Published, cancellationToken).ConfigureAwait(false);
        return published is not null && AssetServiceAccess.Matches(service, published.Package.Target?.ServiceId)
            ? published
            : null;
    }
}
