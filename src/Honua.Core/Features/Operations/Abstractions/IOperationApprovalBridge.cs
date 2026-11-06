// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

// Success is returned only after the bounded request, its canonical envelope, and the proposal audit identity are durable.
public interface IOperationApprovalBridge
{
    Task<OperationApprovalBridgeResult> CreateProposalAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision,
        CancellationToken cancellationToken = default);
}

public sealed record OperationApprovalBridgeResult
{
    public required bool IsDurable { get; init; }

    public string? ProposalId { get; init; }

    public string? AuditId { get; init; }

    public string? Reason { get; init; }
}
