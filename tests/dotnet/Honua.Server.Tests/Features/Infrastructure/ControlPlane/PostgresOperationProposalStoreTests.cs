// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
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
    public async Task TrySetAsync_ApprovalWindowElapsed_RefusesExecutionWithoutChangingProposal()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal() with
        {
            Status = OperationProposalStatus.AwaitingApproval,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-25)
        };
        (await store.TryCreateAsync(proposal)).Should().BeTrue();

        (await store.TrySetAsync(proposal with { Status = OperationProposalStatus.Executing }))
            .Should().BeFalse();

        var stored = await store.GetAsync(proposal.ProposalId);
        stored!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        stored.Version.Should().Be(0);
    }

    [IntegrationTest]
    public async Task TrySetAsync_StoredDeadlineExpired_CallerCannotExtendIt()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal() with
        {
            Status = OperationProposalStatus.AwaitingApproval,
            ApprovalExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await store.TryCreateAsync(proposal);
        (await store.TrySetAsync(proposal with
        {
            ApprovalExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        })).Should().BeTrue();
        var stored = (await store.GetAsync(proposal.ProposalId))!;
        stored.ApprovalExpiresAt.Should().BeCloseTo(proposal.ApprovalExpiresAt!.Value, TimeSpan.FromMilliseconds(1));
        (await store.TrySetAsync(stored with
        {
            Status = OperationProposalStatus.Executing,
            ApprovalExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        })).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task TrySetAsync_CriticalWindowStillOpen_AllowsExecution()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal() with
        {
            Status = OperationProposalStatus.AwaitingApproval,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-25),
            Plan = new OperationProposalPlan { RiskLevel = ProposalRiskLevel.High }
        };
        await store.TryCreateAsync(proposal);
        (await store.TrySetAsync(proposal with { Status = OperationProposalStatus.Executing })).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TryCreateAsync_FindingSeverityControlsApprovalWindow()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        foreach (var (severity, risk, hours) in new[]
        {
            (Honua.Core.Features.Observability.Domain.OpsFindingSeverity.Critical, ProposalRiskLevel.Low, 72),
            (Honua.Core.Features.Observability.Domain.OpsFindingSeverity.Warning, ProposalRiskLevel.High, 24)
        })
        {
            var proposal = Proposal() with
            {
                Plan = new OperationProposalPlan { RiskLevel = risk },
                AutonomyMetadata = new OperationProposalAutonomyMetadata
                {
                    FindingId = "finding",
                    Rule = "rule",
                    Severity = severity
                }
            };
            await store.TryCreateAsync(proposal);
            var stored = (await store.GetAsync(proposal.ProposalId))!;
            stored.ApprovalExpiresAt.Should().BeCloseTo(proposal.CreatedAt.AddHours(hours), TimeSpan.FromMilliseconds(1));
        }
    }

    [IntegrationTest]
    public async Task TrySetAsync_AlreadyExecutingAfterApprovalDeadline_AllowsProgress()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal() with
        {
            Status = OperationProposalStatus.Executing,
            ApprovalExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        await store.TryCreateAsync(proposal);

        (await store.TrySetAsync(proposal with { ResolvedBy = "original-approver" }))
            .Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TrySetAsync_ExpiredProposal_CannotBeRevived()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateProposalStore();
        var proposal = Proposal() with { Status = OperationProposalStatus.Expired };
        await store.TryCreateAsync(proposal);

        (await store.TrySetAsync(proposal with { Status = OperationProposalStatus.AwaitingApproval }))
            .Should().BeFalse();
        (await store.TrySetAsync(proposal with { Status = OperationProposalStatus.Executing }))
            .Should().BeFalse();
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
