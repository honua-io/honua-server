// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class DeployWorkflowService
{
    internal async Task<WorkflowOperationRecord?> CreateApprovedWithBackupAsync(
        OperationGatewayRequest request, DeployExecutionPayload payload, string? restoreOwnerId,
        CancellationToken cancellationToken)
    {
        var proposal = _proposalStore is null || string.IsNullOrWhiteSpace(request.ApprovedProposalId)
            ? null : await _proposalStore.GetAsync(request.ApprovedProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal?.Plan.ExecutionPayload is not { } approvedPayload
            || !OperationHumanApprovalEvidenceValidation.MatchesExecutionRequest(proposal, request, approvedPayload)
            || !MatchesApprovedPayload(approvedPayload, payload, restoreOwnerId))
            throw new InvalidOperationException("The deployment must match its durable human approval.");

        var operation = await CreateWithBackupAsync(payload.TargetId, payload.DesiredRevision, payload.CurrentRevision,
            request.RequestedBy, request.Reason, request.IdempotencyKey, request.CorrelationId, payload.Priority,
            submitImmediately: true, payload.ParameterOverrides, principal: null,
            preMigrationBackupId: payload.Rollback ? payload.BackupId : null, restoreOwnerId,
            request.ApprovedBy, cancellationToken).ConfigureAwait(false);
        if (operation is not null && operation.BlockingReasons.Count == 0
            && operation.Status is WorkflowOperationStatus.AwaitingApproval or WorkflowOperationStatus.Planned)
        {
            return await SubmitCoreAsync(new DeploySubmission(operation.OperationId, request.ApprovedBy,
                request.Reason, restoreOwnerId), proposal, cancellationToken).ConfigureAwait(false);
        }

        return operation;
    }

    private static bool MatchesApprovedPayload(string approvedPayload, DeployExecutionPayload payload, string? restoreOwnerId)
    {
        var approved = DeployExecutionPayload.Parse(approvedPayload);
        if (approved is null) return false;
        if (approved.Serialize() == payload.Serialize()) return true;
        return restoreOwnerId is not null
            && OperationHumanApprovalEvidenceValidation.MatchesObservedForwardDeploy(approvedPayload, payload);
    }
}
