// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Keep v1 projections fixed: additive Core fields must not invalidate already claimed execution.
internal sealed record OperationInitiationEvidenceV1(
    int Version,
    OperationInitiationKind Kind,
    OperationInitiationChannel Channel,
    string? PrincipalId,
    string? TenantId,
    string PlanHash,
    string PolicyRevision,
    DateTimeOffset AcceptedAt)
{
    public static OperationInitiationEvidenceV1? From(OperationInitiationEvidence? evidence) => evidence is null ? null : new(
        Version: evidence.Version,
        Kind: evidence.Kind,
        Channel: evidence.Channel,
        PrincipalId: evidence.PrincipalId,
        TenantId: evidence.TenantId,
        PlanHash: evidence.PlanHash,
        PolicyRevision: evidence.PolicyRevision,
        AcceptedAt: evidence.AcceptedAt);
}

internal sealed record OperationProposalEvidenceV1(
    string ToolName,
    string OperationId,
    string CandidateId,
    string TenantId,
    string TargetId,
    string DescriptorRevision,
    string PolicyRevision,
    string AuthorizationDecision,
    string RequestDigest,
    string CanonicalRequest,
    string PayloadDigest,
    string CanonicalPayload,
    string TranscriptDigest,
    string TranscriptKeyId,
    string CanonicalTranscript,
    string TranscriptSignature,
    string ReleaseId,
    string ActionId,
    string RunNonce,
    string McpSessionId,
    string McpCallId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string VerifierDecision)
{
    public static OperationProposalEvidenceV1? From(OperationProposalEvidence? evidence) => evidence is null ? null : new(
        ToolName: evidence.ToolName,
        OperationId: evidence.OperationId,
        CandidateId: evidence.CandidateId,
        TenantId: evidence.TenantId,
        TargetId: evidence.TargetId,
        DescriptorRevision: evidence.DescriptorRevision,
        PolicyRevision: evidence.PolicyRevision,
        AuthorizationDecision: evidence.AuthorizationDecision,
        RequestDigest: evidence.RequestDigest,
        CanonicalRequest: evidence.CanonicalRequest,
        PayloadDigest: evidence.PayloadDigest,
        CanonicalPayload: evidence.CanonicalPayload,
        TranscriptDigest: evidence.TranscriptDigest,
        TranscriptKeyId: evidence.TranscriptKeyId,
        CanonicalTranscript: evidence.CanonicalTranscript,
        TranscriptSignature: evidence.TranscriptSignature,
        ReleaseId: evidence.ReleaseId,
        ActionId: evidence.ActionId,
        RunNonce: evidence.RunNonce,
        McpSessionId: evidence.McpSessionId,
        McpCallId: evidence.McpCallId,
        IssuedAt: evidence.IssuedAt,
        ExpiresAt: evidence.ExpiresAt,
        VerifierDecision: evidence.VerifierDecision);
}

