// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane.Domain;

public enum OperationProposalStatus
{
    Planned = 0,

    AwaitingApproval = 1,

    Executing = 8,

    Submitted = 2,

    Reconciling = 3,

    Succeeded = 4,

    Failed = 5,

    Rejected = 6,

    RolledBack = 7,

    Cancelled = 9,

    Expired = 10,
}

public enum ProposalRiskLevel
{
    Low = 0,

    Medium = 1,

    High = 2,
}

public sealed record OperationProposalPlan
{
    public FolderRequesterAuthorization? FolderAuthorization { get; init; }

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> Diff { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DryRun { get; init; } = Array.Empty<string>();

    public ProposalRiskLevel RiskLevel { get; init; } = ProposalRiskLevel.Low;

    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string? ExecutionPayload { get; init; }
}

public sealed record FolderRequesterAuthorization
{
    public required string RequesterId { get; init; }
    public string? SubjectId { get; init; }
    public string? SubjectIssuer { get; init; }
    public required string OperationId { get; init; }
    public required string SnapshotEtag { get; init; }
    public required string PolicyRevision { get; init; }
    public required IReadOnlyList<string> ServiceIds { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record OperationProposalAutonomyMetadata
{
    public Honua.Core.Features.Observability.Domain.OpsFindingSeverity? Severity { get; init; }

    public required string FindingId { get; init; }

    public required string Rule { get; init; }

    public string? ActionDiscriminator { get; init; }

    public bool ActionMarkedAutoSafe { get; init; }

    public int BlastRadius { get; init; } = 1;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}

public sealed record OperationProposalEvidence
{
    public required string ToolName { get; init; }
    public required string OperationId { get; init; }
    public required string CandidateId { get; init; }
    public required string TenantId { get; init; }
    public required string TargetId { get; init; }
    public required string DescriptorRevision { get; init; }
    public required string PolicyRevision { get; init; }
    public required string AuthorizationDecision { get; init; }
    public required string RequestDigest { get; init; }
    public required string CanonicalRequest { get; init; }
    public required string PayloadDigest { get; init; }
    public required string CanonicalPayload { get; init; }
    public required string TranscriptDigest { get; init; }
    public required string TranscriptKeyId { get; init; }
    public required string CanonicalTranscript { get; init; }
    public required string TranscriptSignature { get; init; }
    public required string ReleaseId { get; init; }
    public required string ActionId { get; init; }
    public required string RunNonce { get; init; }
    public required string McpSessionId { get; init; }
    public required string McpCallId { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string VerifierDecision { get; init; } = "verified";
}

public sealed record OperationProposal
{
    public OperationInitiationEvidence? Initiation { get; init; }

    public OperationHumanApprovalEvidence? HumanApproval { get; init; }

    public string? TenantId { get; init; }

    public required string ProposalId { get; init; }

    public bool ScopeGoverned { get; init; }

    public IReadOnlyList<string> RecognizedScopes { get; init; } = [];

    public string? OperationId { get; init; }

    public required OperationClass Kind { get; init; }

    public string? ActionDiscriminator { get; init; }

    public required OperationProposalStatus Status { get; init; }

    public string? RequestedBy { get; init; }

    public string? RequestedByAgent { get; init; }

    public long Version { get; init; }

    public OperationProposalPlan Plan { get; init; } = new();

    public string? SealedPlanHash { get; init; }

    public GuardrailDecision? GuardrailDecision { get; init; }

    public OperationAuditInfo Audit { get; init; } = new();

    public OperationProposalAutonomyMetadata? AutonomyMetadata { get; init; }

    public OperationProposalEvidence? Evidence { get; init; }

    public string? ExecutionOperationId { get; init; }

    public string? ResolvedBy { get; init; }

    public string? ResolutionReason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public DateTimeOffset? ApprovalExpiresAt { get; init; }
}
