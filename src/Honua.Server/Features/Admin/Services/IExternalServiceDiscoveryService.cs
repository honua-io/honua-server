// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin.Services;

internal interface IExternalServiceDiscoveryService
{
    Task<ExternalServiceDiscoveryResponse> DiscoverAsync(
        ExternalServiceDiscoveryRequest request,
        CancellationToken cancellationToken = default);
}

internal interface IExternalServiceDiscoveryNetworkGuard
{
    Task<bool> IsDisallowedAsync(Uri uri, CancellationToken cancellationToken = default);
}
