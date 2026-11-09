// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Features.Capabilities;

namespace Honua.Server.Features.Catalog;

internal sealed class CatalogManifestProtocolProvider(ICapabilityManifestService manifestService, ITenantContext tenantContext)
    : ICatalogProtocolProvider
{
    public async Task<IReadOnlyList<CatalogProtocolDescriptor>> GetProtocolsAsync(ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var request = new CapabilityManifestRequest(principal, tenantContext.TenantId, tenantContext.Source,
            null, null, principal.Identity?.IsAuthenticated == true);
        var manifest = await manifestService.GetManifestAsync(request, cancellationToken).ConfigureAwait(false);
        return manifest.ProtocolEndpoints;
    }
}
