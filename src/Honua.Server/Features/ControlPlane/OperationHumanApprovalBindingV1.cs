// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Keep v1 projections fixed: additive Core fields must not invalidate already claimed execution.
internal sealed class OperationInitiationEvidenceV1
{
    public required int Version { get; init; }
    public required OperationInitiationKind Kind { get; init; }
    public required OperationInitiationChannel Channel { get; init; }
    public required string? PrincipalId { get; init; }
    public required string? TenantId { get; init; }
    public required string PlanHash { get; init; }
    public required string PolicyRevision { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }

    public static OperationInitiationEvidenceV1? From(OperationInitiationEvidence? evidence) => evidence is null ? null : new()
    {
        Version = evidence.Version,
        Kind = evidence.Kind,
        Channel = evidence.Channel,
        PrincipalId = evidence.PrincipalId,
        TenantId = evidence.TenantId,
        PlanHash = evidence.PlanHash,
        PolicyRevision = evidence.PolicyRevision,
        AcceptedAt = evidence.AcceptedAt,
    };
}

internal sealed class OperationProposalEvidenceV1
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
    public required string VerifierDecision { get; init; }

    public static OperationProposalEvidenceV1? From(OperationProposalEvidence? evidence) => evidence is null ? null : new()
    {
        ToolName = evidence.ToolName,
        OperationId = evidence.OperationId,
        CandidateId = evidence.CandidateId,
        TenantId = evidence.TenantId,
        TargetId = evidence.TargetId,
        DescriptorRevision = evidence.DescriptorRevision,
        PolicyRevision = evidence.PolicyRevision,
        AuthorizationDecision = evidence.AuthorizationDecision,
        RequestDigest = evidence.RequestDigest,
        CanonicalRequest = evidence.CanonicalRequest,
        PayloadDigest = evidence.PayloadDigest,
        CanonicalPayload = evidence.CanonicalPayload,
        TranscriptDigest = evidence.TranscriptDigest,
        TranscriptKeyId = evidence.TranscriptKeyId,
        CanonicalTranscript = evidence.CanonicalTranscript,
        TranscriptSignature = evidence.TranscriptSignature,
        ReleaseId = evidence.ReleaseId,
        ActionId = evidence.ActionId,
        RunNonce = evidence.RunNonce,
        McpSessionId = evidence.McpSessionId,
        McpCallId = evidence.McpCallId,
        IssuedAt = evidence.IssuedAt,
        ExpiresAt = evidence.ExpiresAt,
        VerifierDecision = evidence.VerifierDecision,
    };
}

