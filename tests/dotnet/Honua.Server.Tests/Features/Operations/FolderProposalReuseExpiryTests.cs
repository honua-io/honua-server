// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderProposalReuseExpiryTests
{
    private const string Requester = "requester";
    private const string Payload = "{\"operationId\":\"folder.create\"}";
    private static readonly DateTimeOffset ExpiresAt = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [UnitTheory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void MatchesExisting_ComparesExpiryWithTheSuppliedTime(int secondsFromExpiry, bool expected)
    {
        var existing = Proposal(Authorization());

        var matches = FolderApprovalAuthorization.MatchesExisting(
            existing, Candidate(Requester, Plan(Authorization()), ExpiresAt.AddSeconds(secondsFromExpiry)));

        matches.Should().Be(expected);
    }

    [UnitTest]
    public void MatchesExisting_BeforeExpiry_StillRequiresTheSameRequester()
    {
        var existing = Proposal(Authorization());

        FolderApprovalAuthorization.MatchesExisting(existing, Candidate("other-requester", Plan(Authorization()), ExpiresAt.AddSeconds(-1)))
            .Should().BeFalse();
    }

    [UnitTest]
    public void MatchesExisting_BeforeExpiry_StillRequiresTheSamePolicyRevision()
    {
        var existing = Proposal(Authorization());
        var changedPolicy = Plan(Authorization() with { PolicyRevision = "policy-2" });

        FolderApprovalAuthorization.MatchesExisting(existing, Candidate(Requester, changedPolicy, ExpiresAt.AddSeconds(-1)))
            .Should().BeFalse();
    }

    [UnitTest]
    public void MatchesExisting_BeforeExpiry_StillRequiresTheSameProposalPayload()
    {
        var existing = Proposal(Authorization());
        var changedPayload = Plan(Authorization()) with { ExecutionPayload = "{\"operationId\":\"folder.delete\"}" };

        FolderApprovalAuthorization.MatchesExisting(existing, Candidate(Requester, changedPayload, ExpiresAt.AddSeconds(-1)))
            .Should().BeFalse();
    }

    private static FolderProposalReuseContext Candidate(string requester, OperationProposalPlan plan, DateTimeOffset now)
        => new(new OperationGatewayRequest { Kind = OperationClass.AdminConfigChange, RequestedBy = requester },
            plan, new FixedTimeProvider(now));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static FolderRequesterAuthorization Authorization() => new()
    {
        RequesterId = Requester,
        SubjectId = "subject",
        SubjectIssuer = "issuer",
        OperationId = "folder.create",
        SnapshotEtag = "etag-1",
        PolicyRevision = "policy-1",
        ServiceIds = ["service-a"],
        ExpiresAt = ExpiresAt,
    };

    private static OperationProposalPlan Plan(FolderRequesterAuthorization authorization) => new()
    {
        ExecutionPayload = Payload,
        FolderAuthorization = authorization,
    };

    private static OperationProposal Proposal(FolderRequesterAuthorization authorization) => new()
    {
        ProposalId = "proposal",
        TenantId = "public",
        RequestedBy = Requester,
        Kind = OperationClass.AdminConfigChange,
        Status = OperationProposalStatus.AwaitingApproval,
        Plan = Plan(authorization),
        CreatedAt = ExpiresAt.AddHours(-1),
        UpdatedAt = ExpiresAt.AddHours(-1),
    };
}
