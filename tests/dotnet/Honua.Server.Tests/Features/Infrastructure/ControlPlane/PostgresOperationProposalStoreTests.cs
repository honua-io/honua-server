// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using NSubstitute;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresOperationProposalStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task TryCreateAsync_SameIdOnTwoStores_SecondIsRefused()
    {
        await using var harness = await CreateHarnessAsync();
        var proposal = Proposal();

        (await harness.CreateProposalStore().TryCreateAsync(proposal)).Should().BeTrue();
        (await harness.CreateProposalStore().TryCreateAsync(proposal with { RequestedBy = "someone-else" })).Should().BeFalse();

        var stored = await harness.CreateProposalStore().GetAsync(proposal.ProposalId);
        stored!.RequestedBy.Should().Be(proposal.RequestedBy);
        stored.Version.Should().Be(0);
    }

    [IntegrationTest]
    public async Task TrySetAsync_TwoWritersOfOneVersion_ExactlyOneWins()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal();
        await store.TryCreateAsync(proposal);
        var read = await store.GetAsync(proposal.ProposalId);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(writer =>
            harness.CreateProposalStore().TrySetAsync(read! with
            {
                Status = OperationProposalStatus.Executing,
                ResolvedBy = "approver-" + writer
            })));

        outcomes.Count(won => won).Should().Be(1);
        var stored = await store.GetAsync(proposal.ProposalId);
        stored!.Version.Should().Be(1);
        stored.ResolvedBy.Should().Be("approver-" + Array.IndexOf(outcomes, true));
    }

    [IntegrationTest]
    public async Task TrySetAsync_StampsUpdatedAtFromTheDatabaseClock()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var writtenByASkewedNode = DateTimeOffset.UtcNow.AddHours(-6);
        var proposal = Proposal() with { UpdatedAt = writtenByASkewedNode };
        await store.TryCreateAsync(proposal);

        (await store.TrySetAsync(proposal with { Version = 0, Status = OperationProposalStatus.AwaitingApproval })).Should().BeTrue();

        var databaseNow = await harness.ScalarAsync<DateTime>("SELECT now()");
        var stored = await store.GetAsync(proposal.ProposalId);
        stored!.UpdatedAt.UtcDateTime.Should().BeCloseTo(databaseNow, TimeSpan.FromSeconds(30));
    }

    [IntegrationTest]
    public async Task ListActiveAsync_ReturnsOnlyActiveProposalsOfTheKindNewestFirst()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var older = Proposal() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };
        var newer = Proposal() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var otherKind = Proposal() with { Kind = OperationClass.Deploy };
        var finished = Proposal();
        foreach (var proposal in new[] { older, newer, otherKind, finished })
        {
            await store.TryCreateAsync(proposal);
        }

        await store.TrySetAsync(finished with { Version = 0, Status = OperationProposalStatus.Rejected });

        (await store.ListActiveAsync(OperationClass.AdminConfigChange)).Select(proposal => proposal.ProposalId)
            .Should().Equal(newer.ProposalId, older.ProposalId);
        (await store.ListActiveAsync()).Should().HaveCount(3);
    }

    [IntegrationTest]
    public async Task DeleteExpiredAsync_KeepsActiveProposalsAndDeletesTerminalOnesPastRetention()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var waiting = Proposal();
        var rejected = Proposal();
        var recentlyRejected = Proposal();
        foreach (var proposal in new[] { waiting, rejected, recentlyRejected })
        {
            await store.TryCreateAsync(proposal);
        }

        await store.TrySetAsync(rejected with { Version = 0, Status = OperationProposalStatus.Rejected });
        await store.TrySetAsync(recentlyRejected with { Version = 0, Status = OperationProposalStatus.Rejected });
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("operation_proposals")} SET expires_at = now() - interval '1 second' WHERE proposal_id = ANY (@ids)",
            new NpgsqlParameter("ids", new[] { waiting.ProposalId, rejected.ProposalId }));

        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);

        (await store.GetAsync(waiting.ProposalId)).Should().NotBeNull("an active proposal never expires");
        (await store.GetAsync(rejected.ProposalId)).Should().BeNull();
        (await store.GetAsync(recentlyRejected.ProposalId)).Should().NotBeNull();
    }

    [IntegrationTest]
    public async Task Leases_AreExclusivePerProposalUntilReleased()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var leaseId = "janitor-" + Guid.NewGuid().ToString("N");
        var leaseDuration = TimeSpan.FromSeconds(30);

        (await store.TryAcquireLeaseAsync(leaseId, "node-a", leaseDuration)).Should().BeTrue();
        (await harness.CreateProposalStore().TryAcquireLeaseAsync(leaseId, "node-b", leaseDuration)).Should().BeFalse();
        (await store.RenewLeaseAsync(leaseId, "node-a", leaseDuration)).Should().BeTrue();
        await store.ReleaseLeaseAsync(leaseId, "node-a");
        (await harness.CreateProposalStore().TryAcquireLeaseAsync(leaseId, "node-b", leaseDuration)).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task ReserveBatchAsync_ConcurrentReplicas_ShareOnePendingReservation()
    {
        await using var harness = await CreateHarnessAsync();
        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            harness.CreateProposalStore().ReserveBatchAsync(new("finding", $"key-{index}", $"proposal-{index}"))));

        keys.Distinct().Should().ContainSingle();
        (await harness.CreateProposalStore().ReserveBatchAsync(new("finding", "restart", "restart-id")))
            .Should().Be(keys[0]);
    }

    [IntegrationTest]
    public async Task ReserveBatchAsync_CompletionSurvivesRetention_AdvancesOnlyAfterSuccessfulVersionedWrite()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal();
        await store.ReserveBatchAsync(new("finding", "first", proposal.ProposalId));
        await store.TryCreateAsync(proposal);
        (await store.TrySetAsync(proposal with { Version = 9, Status = OperationProposalStatus.Succeeded })).Should().BeFalse();
        (await store.ReserveBatchAsync(new("finding", "stale", "stale-id"))).Should().Be("first");
        (await store.TrySetAsync(proposal with { Status = OperationProposalStatus.Succeeded })).Should().BeTrue();
        await harness.ExecuteAsync($"UPDATE {harness.Table("operation_proposals")} SET expires_at = now() - interval '1 second'");
        (await store.DeleteExpiredAsync(100, CancellationToken.None)).Should().Be(1);

        (await harness.CreateProposalStore().ReserveBatchAsync(new("finding", "next", "next-id"))).Should().Be("next");
    }

    [IntegrationTest]
    public async Task ReserveBatchAsync_RejectedProposal_DoesNotOpenAnotherBatch()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal();
        await store.ReserveBatchAsync(new("finding", "first", proposal.ProposalId));
        await store.TryCreateAsync(proposal);
        await store.TrySetAsync(proposal with { Status = OperationProposalStatus.Rejected });

        (await harness.CreateProposalStore().ReserveBatchAsync(new("finding", "next", "next-id"))).Should().Be("first");
    }

    [IntegrationTest]
    public async Task CreateApprovalProposalAsync_RedriveBacklogGrows_PendingBatchStaysSealedThenNextBatchIsFresh()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.AdminConfigChange).Returns(new GuardrailDecision(
            GuardrailTier.RequiresApproval, OperationClass.AdminConfigChange, HonuaEdition.Pro, "test"));
        var gateway = CanonicalOperationGatewayTestComposition.Build(store, ladder, []);
        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.AdminConfigChange, RequestedByAgent = "ops-findings",
            ActionDiscriminator = OpsActionNames.RedriveDeadLetters, IdempotencyKey = "stable-finding",
            Plan = new OperationProposalPlan { Summary = "Redrive one", ExecutionPayload = OpsActionExecutionPayloads.RedriveDeadLetters(1) },
            AutonomyContext = new OperationGatewayAutonomyContext
            {
                FindingId = "stable-finding", Rule = "alert-dispatch-backlog", ActionMarkedAutoSafe = true
            }
        };
        var first = await gateway.CreateApprovalProposalAsync("instance-first", request);
        first.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, first.Message);
        var grown = request with
        {
            Plan = new OperationProposalPlan { Summary = "Redrive five hundred", ExecutionPayload = OpsActionExecutionPayloads.RedriveDeadLetters(500) }
        };
        var pending = await gateway.CreateApprovalProposalAsync("instance-retry", grown);
        pending.ProposalId.Should().Be(first.ProposalId);
        var stored = await store.GetAsync(first.ProposalId!);
        stored!.Plan.ExecutionPayload.Should().Be(request.Plan.ExecutionPayload);
        await store.TrySetAsync(stored with { Status = OperationProposalStatus.Succeeded });

        var restartedGateway = CanonicalOperationGatewayTestComposition.Build(harness.CreateProposalStore(), ladder, []);
        var next = await restartedGateway.CreateApprovalProposalAsync("instance-next", grown);
        next.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, next.Message);
        next.ProposalId.Should().NotBe(first.ProposalId);
        (await store.GetAsync(next.ProposalId!))!.Plan.ExecutionPayload.Should().Be(grown.Plan.ExecutionPayload);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresOperationProposalStoreTests));

    private static OperationProposal Proposal()
    {
        var now = DateTimeOffset.UtcNow;
        return new OperationProposal
        {
            ProposalId = $"proposal-{Guid.NewGuid():N}",
            TenantId = "public",
            Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.Planned,
            RequestedBy = "requester",
            Plan = new OperationProposalPlan { Summary = "Change a setting", RiskLevel = ProposalRiskLevel.Medium },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
