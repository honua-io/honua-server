// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class OperationHumanApprovalBindingV1Tests
{
    [UnitTest]
    public void ComputeBindingDigest_V1WireVector_RetainsItsDeclaredShape()
    {
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));
        var initiation = new OperationInitiationEvidence
        {
            Version = 1,
            Kind = OperationInitiationKind.Agent,
            Channel = OperationInitiationChannel.Mcp,
            PrincipalId = "a",
            TenantId = null,
            PlanHash = "h",
            PolicyRevision = "policy",
            AcceptedAt = instant,
        };
        var proposal = new OperationProposal
        {
            ProposalId = "p",
            OperationId = "op",
            Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.Executing,
            RequestedBy = "a",
            SealedPlanHash = "h",
            Audit = new OperationAuditInfo { OperationInstanceId = "i" },
            CreatedAt = instant,
            UpdatedAt = instant,
            Initiation = initiation,
            Evidence = Evidence(instant),
        };

        Assert.Equal("fb08f39bb9b03aa86a05dbe82c81854d4d989ebdadad0bea4cebab605ace2b4d", OperationHumanApprovalAuthority.ComputeInitiationDigest(initiation));
        Assert.Equal("4af8d1c8e4551acd3f9012e3bde21aacd0723c1c7d5597658b1fe2e637c6f81f", OperationHumanApprovalAuthority.ComputeBindingDigest(proposal));
    }

    private static OperationProposalEvidence Evidence(DateTimeOffset instant) => new()
    {
        ToolName = "v",
        OperationId = "v",
        CandidateId = "v",
        TenantId = "v",
        TargetId = "v",
        DescriptorRevision = "v",
        PolicyRevision = "v",
        AuthorizationDecision = "v",
        RequestDigest = "v",
        CanonicalRequest = "v",
        PayloadDigest = "v",
        CanonicalPayload = "v",
        TranscriptDigest = "v",
        TranscriptKeyId = "v",
        CanonicalTranscript = "v",
        TranscriptSignature = "v",
        ReleaseId = "v",
        ActionId = "v",
        RunNonce = "v",
        McpSessionId = "v",
        McpCallId = "v",
        IssuedAt = instant,
        ExpiresAt = instant,
        VerifierDecision = "v",
    };
}
