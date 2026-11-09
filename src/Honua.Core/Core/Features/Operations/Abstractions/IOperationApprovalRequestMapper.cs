// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationApprovalRequestMapper
{
    string OperationId { get; }

    OperationGatewayRequest Map(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision);

    OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request);
}

// The gateway applies the reconstructed authority on replay; a protocol caller does not.
public sealed record OperationApprovalReplayMapping
{
    public required OperationRequest Request { get; init; }

    public string? TenantId { get; init; }

    public string? SchemaName { get; init; }
}
