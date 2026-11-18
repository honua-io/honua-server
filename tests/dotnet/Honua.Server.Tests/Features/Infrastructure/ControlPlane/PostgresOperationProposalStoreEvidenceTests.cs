// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresOperationProposalStoreEvidenceTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task TryCreateAsync_SignedEvidenceOnSecondProposal_RefusesWithoutPersisting()
    {
        await using var harness = await CreateHarnessAsync();
        var evidence = Evidence("tenant-a", "run-shared");
        var first = Proposal(evidence);
        var second = Proposal(evidence);

        (await harness.CreateProposalStore().TryCreateAsync(first)).Should().BeTrue();
        var reuse = () => harness.CreateProposalStore().TryCreateAsync(second);

        await reuse.Should().ThrowAsync<OperationProposalEvidenceConsumedException>()
            .WithMessage(OperationProposalEvidenceConsumedException.AlreadyUsedMessage);
        (await harness.CreateProposalStore().GetAsync(second.ProposalId)).Should().BeNull();
        (await harness.CreateProposalStore().GetAsync(first.ProposalId)).Should().NotBeNull();
    }

    [IntegrationTest]
    public async Task TryCreateAsync_DifferentRunOrTenant_AcceptsEachEvidence()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();

        (await store.TryCreateAsync(Proposal(Evidence("tenant-a", "run-1")))).Should().BeTrue();
        (await store.TryCreateAsync(Proposal(Evidence("tenant-a", "run-2")))).Should().BeTrue();
        (await store.TryCreateAsync(Proposal(Evidence("tenant-b", "run-1")))).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TryCreateAsync_SameProposalIdRetried_ReportsConflictWithoutRefusal()
    {
        await using var harness = await CreateHarnessAsync();
        var proposal = Proposal(Evidence("tenant-a", "run-retry"));

        (await harness.CreateProposalStore().TryCreateAsync(proposal)).Should().BeTrue();
        (await harness.CreateProposalStore().TryCreateAsync(proposal)).Should().BeFalse();

        (await harness.CreateProposalStore().GetAsync(proposal.ProposalId))!.Evidence!.RunNonce.Should().Be("run-retry");
    }

    [IntegrationTest]
    public async Task TryCreateAsync_EvidenceWithoutRunNonce_RefusesWithoutPersisting()
    {
        await using var harness = await CreateHarnessAsync();
        var proposal = Proposal(Evidence("tenant-a", " "));
        var store = harness.CreateProposalStore();

        var create = () => store.TryCreateAsync(proposal);

        await create.Should().ThrowAsync<OperationProposalEvidenceConsumedException>();
        (await store.GetAsync(proposal.ProposalId)).Should().BeNull();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresOperationProposalStoreEvidenceTests));

    private static OperationProposal Proposal(OperationProposalEvidence evidence)
    {
        var now = DateTimeOffset.UtcNow;
        return new OperationProposal
        {
            ProposalId = $"proposal-{Guid.NewGuid():N}",
            TenantId = evidence.TenantId,
            Kind = OperationClass.Deploy,
            Status = OperationProposalStatus.Planned,
            RequestedBy = "requester",
            Plan = new OperationProposalPlan { Summary = "Deploy a candidate", RiskLevel = ProposalRiskLevel.Medium },
            Evidence = evidence,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static OperationProposalEvidence Evidence(string tenantId, string runNonce)
        => new()
        {
            ToolName = "propose-deploy",
            OperationId = "control-plane.deploy",
            CandidateId = "candidate-a",
            TenantId = tenantId,
            TargetId = "candidate-a",
            DescriptorRevision = "descriptor-1",
            PolicyRevision = "policy-1",
            AuthorizationDecision = "admin-policy-authorized",
            RequestDigest = "request-digest",
            CanonicalRequest = "eA==",
            PayloadDigest = "payload-digest",
            CanonicalPayload = "eA==",
            TranscriptDigest = "transcript-digest",
            TranscriptKeyId = "transcript-key-1",
            CanonicalTranscript = "eA==",
            TranscriptSignature = "signature",
            ReleaseId = "release-1",
            ActionId = "action-1",
            RunNonce = runNonce,
            McpSessionId = "session-1",
            McpCallId = "call-1",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };
}
