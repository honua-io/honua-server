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
    OperationRequest MapReplay(OperationGatewayRequest request);
}
