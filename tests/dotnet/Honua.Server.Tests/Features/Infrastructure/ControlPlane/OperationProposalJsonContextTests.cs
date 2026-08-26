// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Verifies backward-compatible durable proposal serialization through the source-generated
/// context used by the Redis proposal store.
/// </summary>
public sealed class OperationProposalJsonContextTests
{
    [UnitTest]
    public void OperationProposalJsonContext_QualifiedApproverIdentity_RoundTrips()
    {
        var proposal = CreateProposal(new OperationApproverIdentity
        {
            Actor = "approver-1",
            Issuer = "honua-operator-bearer",
            MembershipIssuer = "https://idp.example.com",
            Scheme = "OperatorBearer",
        });

        var json = JsonSerializer.Serialize(
            proposal,
            OperationProposalJsonContext.Default.OperationProposal);
        var roundTrip = JsonSerializer.Deserialize(
            json,
            OperationProposalJsonContext.Default.OperationProposal);

        var identity = Assert.IsType<OperationApproverIdentity>(roundTrip?.Approval?.ApproverIdentity);
        Assert.Equal("approver-1", identity.Actor);
        Assert.Equal("honua-operator-bearer", identity.Issuer);
        Assert.Equal("https://idp.example.com", identity.MembershipIssuer);
        Assert.Equal("OperatorBearer", identity.Scheme);
    }

    [UnitTest]
    public void OperationProposalJsonContext_LegacyApprovalWithoutApproverIdentity_DeserializesNull()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(
            CreateProposal(new OperationApproverIdentity
            {
                Actor = "approver-1",
                Issuer = "https://idp.example.com",
                Scheme = "oidc",
            }),
            OperationProposalJsonContext.Default.OperationProposal))!;
        Assert.True(document["approval"]!.AsObject().Remove("approverIdentity"));

        var legacy = JsonSerializer.Deserialize(
            document.ToJsonString(),
            OperationProposalJsonContext.Default.OperationProposal);

        var approval = Assert.IsType<OperationApprovalRecord>(legacy?.Approval);
        Assert.Null(approval.ApproverIdentity);
        Assert.Equal("approver-1", approval.Approver);
        Assert.True(approval.Approved);
    }

    private static OperationProposal CreateProposal(OperationApproverIdentity approverIdentity)
    {
        var decidedAt = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        return new OperationProposal
        {
            ProposalId = "proposal-1",
            Kind = OperationClass.Deploy,
            Status = OperationProposalStatus.Submitted,
            Approval = new OperationApprovalRecord
            {
                Approver = approverIdentity.Actor,
                ApproverIdentity = approverIdentity,
                Approved = true,
                DecidedAt = decidedAt,
                ProposerAuthorityRetained = true,
            },
            CreatedAt = decidedAt.AddMinutes(-5),
            UpdatedAt = decidedAt,
            ResolvedAt = decidedAt,
        };
    }
}
