// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane;

internal interface IOpsRemediationGateway
{
    Task<OperationGatewayResult> RouteRemediationAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);
}
