// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Persists a policy-gated catalog operation in the shared control-plane proposal lane.
/// The server implementation stores enough request and authorization context to execute the
/// original operation after approval without evaluating the same approval policy twice.
/// </summary>
public interface IOperationApprovalProposalBridge
{
    /// <summary>
    /// Creates or returns the durable approval proposal for an operation invocation.
    /// </summary>
    Task<OperationHandle> CreateProposalAsync(
        OperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision,
        CancellationToken cancellationToken = default);
}
