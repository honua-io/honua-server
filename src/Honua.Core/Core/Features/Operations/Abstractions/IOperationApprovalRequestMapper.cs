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
        PolicyDecision decision) =>
        Map(new OperationApprovalRequestContext
        {
            Descriptor = descriptor,
            Request = request,
            Context = context,
            Decision = decision,
        });

    OperationGatewayRequest Map(OperationApprovalRequestContext input) =>
        throw new NotImplementedException();

    OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request);
}

public sealed record OperationApprovalRequestContext
{
    public required IOperationDescriptor Descriptor { get; init; }

    public required OperationRequest Request { get; init; }

    public required OperationPolicyContext Context { get; init; }

    public required PolicyDecision Decision { get; init; }
}

// The gateway applies the reconstructed authority on replay; a protocol caller does not.
public sealed record OperationApprovalReplayMapping
{
    public required OperationRequest Request { get; init; }

    public string? TenantId { get; init; }

    public string? SchemaName { get; init; }
}
