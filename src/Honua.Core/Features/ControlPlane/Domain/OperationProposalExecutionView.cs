// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>Shared proposal read projection over the canonical execution outcome.</summary>
public sealed record OperationProposalExecutionView
{
    /// <summary>Proposal lifecycle status, including a completed asynchronous execution.</summary>
    public required string Status { get; init; }

    /// <summary>Execution failure reason, or the original approval resolution reason.</summary>
    public string? ResolutionReason { get; init; }

    /// <summary>Latest proposal or canonical execution update.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Projects only the exact, same-tenant execution bound to an approved proposal.</summary>
    public static OperationProposalExecutionView From(OperationProposal proposal, OperationHandle? execution)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var view = new OperationProposalExecutionView
        {
            Status = proposal.Status.ToString(),
            ResolutionReason = proposal.ResolutionReason,
            UpdatedAt = proposal.UpdatedAt
        };
        if (proposal.Status is not (OperationProposalStatus.Submitted or OperationProposalStatus.Reconciling) ||
            execution is null || string.IsNullOrWhiteSpace(proposal.ExecutionOperationId) ||
            !string.Equals(execution.OperationInstanceId, proposal.Audit.OperationInstanceId, StringComparison.Ordinal) ||
            !string.Equals(execution.ProposalId, proposal.ProposalId, StringComparison.Ordinal) ||
            !string.Equals(execution.OperationId, proposal.OperationId, StringComparison.Ordinal) ||
            !string.Equals(execution.TenantId, proposal.TenantId, StringComparison.Ordinal) ||
            !string.Equals(execution.JobId, proposal.ExecutionOperationId, StringComparison.Ordinal)) return view;

        return execution.Status is OperationHandleStatus.Completed or OperationHandleStatus.Failed or
            OperationHandleStatus.Cancelled or OperationHandleStatus.Indeterminate
            ? view with
            {
                Status = execution.Status.ToString(),
                ResolutionReason = execution.Reason ?? proposal.ResolutionReason,
                UpdatedAt = execution.UpdatedAt > proposal.UpdatedAt ? execution.UpdatedAt : proposal.UpdatedAt
            }
            : view;
    }
}
