// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public enum OperationGatewayOutcome
{
    Executed = 0,

    ProposalCreated = 1,

    Blocked = 2,

    NotSupported = 3,

    Failed = 4,

    RolledBack = 5,

    Indeterminate = 6,

    Canceled = 7,
}

public sealed record OperationGatewayRequest
{
    public string? TenantId { get; init; }

    public string? OperationId { get; init; }

    public string? OperationInstanceId { get; init; }

    public required OperationClass Kind { get; init; }

    public string? ActionDiscriminator { get; init; }

    public string? RequestedBy { get; init; }

    public string? ApprovedBy { get; init; }

    public string? ApprovedProposalId { get; init; }

    public string? RequestedByAgent { get; init; }

    public string? Reason { get; init; }

    public string? CorrelationId { get; init; }

    public string? RootProvisioningOperationId { get; init; }

    public string? IdempotencyKey { get; init; }

    public OperationProposalPlan? Plan { get; init; }

    public string? ExecutionPayload { get; init; }

    public bool ScopeGoverned { get; init; }

    public IReadOnlyList<string> RecognizedScopes { get; init; } = [];

    public OperationGatewayAutonomyContext? AutonomyContext { get; init; }

    public OperationProposalEvidence? Evidence { get; init; }
}

public sealed record OperationGatewayAutonomyContext
{
    public Honua.Core.Features.Observability.Domain.OpsFindingSeverity? Severity { get; init; }

    public required string FindingId { get; init; }

    public required string Rule { get; init; }

    public string? ActionDiscriminator { get; init; }

    public required bool ActionMarkedAutoSafe { get; init; }

    public int BlastRadius { get; init; } = 1;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}

public sealed record OperationGatewayResult
{
    public required OperationGatewayOutcome Outcome { get; init; }

    public required GuardrailDecision Decision { get; init; }

    public string? ProposalId { get; init; }

    public string? AuditId { get; init; }

    public string? ExecutionOperationId { get; init; }

    public string? Message { get; init; }
}

public interface IOperationGateway
{
    Task<OperationGatewayResult> RouteAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationGatewayResult> CreateApprovalProposalAsync(
        string operationInstanceId,
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationProposal?> ApplyApprovedProposalAsync(
        string proposalId,
        OperationProposalApprovalContext approval,
        CancellationToken cancellationToken = default);

    Task<OperationProposal?> RejectProposalAsync(
        string proposalId,
        string rejectedBy,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed record OperationProposalApprovalContext
{
    public required string ApprovedBy { get; init; }
    public required string TenantId { get; init; }

}

public interface IOperationProposalEvidenceValidator
{
    Task ValidateApprovalAsync(
        OperationProposal proposal,
        OperationProposalApprovalContext approval,
        CancellationToken cancellationToken = default);
}

public interface IOperationExecutor
{
    OperationClass OperationClass { get; }

    Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default);

    Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default);

    Task<OperationBackendStatus?> GetBackendStatusAsync(
        string executionOperationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<OperationBackendStatus?>(null);
}

public sealed record OperationBackendStatus
{
    public required OperationBackendState State { get; init; }

    public string? Reason { get; init; }
}

public enum OperationBackendState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Indeterminate,
}
