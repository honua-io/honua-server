// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Safe typed mapping from a public operation descriptor to its sealed durable approval request.
/// A public approval-capable descriptor must have exactly one registered mapper.
/// </summary>
public interface IOperationApprovalRequestMapper
{
    /// <summary>Gets the descriptor identity this mapper supports.</summary>
    string OperationId { get; }

    /// <summary>Maps an accepted typed request to its durable proposal/replay contract.</summary>
    OperationGatewayRequest Map(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision);

    /// <summary>Rebuilds the sealed typed actuator request from the persisted proposal payload.</summary>
    OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request);
}

/// <summary>
/// Typed request and accepted authority context reconstructed from a persisted
/// proposal. The gateway, never a protocol caller, applies this context to replay.
/// </summary>
public sealed record OperationApprovalReplayMapping
{
    /// <summary>Exact typed actuator request reconstructed from the sealed plan.</summary>
    public required OperationRequest Request { get; init; }

    /// <summary>Tenant identity captured when the proposal was accepted.</summary>
    public string? TenantId { get; init; }

    /// <summary>Database schema route captured when the proposal was accepted.</summary>
    public string? SchemaName { get; init; }
}
