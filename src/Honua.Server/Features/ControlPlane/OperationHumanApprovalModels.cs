// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane;

internal sealed class OperationHumanApprovalReview
{
    public required string ProposalId { get; init; }
    public required long ProposalVersion { get; init; }
    public required string PlanHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string Capability { get; init; }
}

internal sealed class OperationHumanApprovalCapability
{
    public required int Version { get; init; }
    public required string Decision { get; init; }
    public required string Audience { get; init; }
    public required string ProposalId { get; init; }
    public required long ProposalVersion { get; init; }
    public required string BindingDigest { get; init; }
    public required string ActorId { get; init; }
    public required string? TenantId { get; init; }
    public required string SessionReference { get; init; }
    public required string ClaimsDigest { get; init; }
    public required string Nonce { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class OperationHumanApprovalBinding
{
    public required string ProposalId { get; init; }
    public required string? TenantId { get; init; }
    public required string? OperationId { get; init; }
    public required string? OperationInstanceId { get; init; }
    public required OperationClass Kind { get; init; }
    public required string? ActionDiscriminator { get; init; }
    public required string? RequestedBy { get; init; }
    public required string? RequestedByAgent { get; init; }
    public required string? PlanHash { get; init; }
    public required bool ScopeGoverned { get; init; }
    public required IReadOnlyList<string> RecognizedScopes { get; init; }
    public required OperationInitiationEvidence? Initiation { get; init; }
    public required OperationProposalEvidence? Evidence { get; init; }

    public static OperationHumanApprovalBinding From(OperationProposal proposal) => new()
    {
        ProposalId = proposal.ProposalId,
        TenantId = proposal.TenantId,
        OperationId = proposal.OperationId,
        OperationInstanceId = proposal.Audit.OperationInstanceId,
        Kind = proposal.Kind,
        ActionDiscriminator = proposal.ActionDiscriminator,
        RequestedBy = proposal.RequestedBy,
        RequestedByAgent = proposal.RequestedByAgent,
        PlanHash = proposal.SealedPlanHash,
        ScopeGoverned = proposal.ScopeGoverned,
        RecognizedScopes = proposal.RecognizedScopes,
        Initiation = proposal.Initiation,
        Evidence = proposal.Evidence,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OperationHumanApprovalReview))]
[JsonSerializable(typeof(OperationHumanApprovalCapability))]
[JsonSerializable(typeof(OperationHumanApprovalBinding))]
internal sealed partial class OperationHumanApprovalJsonContext : JsonSerializerContext;
