// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresOpsNotificationStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task Observe_RestartPreservesEpisodeAndOnlyOneReminderAdvanceWins()
    {
        await using var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        await harness.CreateNotificationStore().ObserveAsync("finding", "subject", OpsFindingSeverity.Critical, now);
        var due = (await harness.CreateNotificationStore().ListDueAsync(now, 100)).Single();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            harness.CreateNotificationStore().TryAdvanceAsync(due, now.AddMinutes(30))));
        outcomes.Count(won => won).Should().Be(1);
        (await harness.CreateNotificationStore().ListDueAsync(now, 100)).Should().BeEmpty();
        var reminder = (await harness.CreateNotificationStore().ListDueAsync(now.AddMinutes(30), 100)).Single();
        reminder.EpisodeId.Should().Be(due.EpisodeId);
        reminder.FirstSeenAt.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));
        reminder.NoticeNumber.Should().Be(1);
    }

    [IntegrationTest]
    public async Task Observe_RepeatedObservationDoesNotResetReminderButReopeningStartsNewEpisode()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateNotificationStore();
        var now = DateTimeOffset.UtcNow;
        await store.ObserveAsync("finding", "subject", OpsFindingSeverity.Warning, now);
        var first = (await store.ListDueAsync(now, 100)).Single();
        (await store.TryAdvanceAsync(first, now.AddHours(24))).Should().BeTrue();
        await store.ObserveAsync("finding", "subject", OpsFindingSeverity.Warning, now.AddMinutes(1));
        (await store.ListDueAsync(now.AddMinutes(1), 100)).Should().BeEmpty();
        await store.CloseMissingAsync("finding", [], now.AddMinutes(2));
        await store.ObserveAsync("finding", "subject", OpsFindingSeverity.Warning, now.AddMinutes(3));
        var reopened = (await store.ListDueAsync(now.AddMinutes(3), 100)).Single();
        reopened.EpisodeId.Should().NotBe(first.EpisodeId);
        reopened.NoticeNumber.Should().Be(0);
    }

    [IntegrationTest]
    public async Task Expire_RecordsTerminalStateAndDueNoticeTogetherButLeavesExecutionAlone()
    {
        await using var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var waiting = Proposal(now, OperationProposalStatus.AwaitingApproval);
        var executing = Proposal(now, OperationProposalStatus.Executing);
        await harness.CreateProposalStore().TryCreateAsync(waiting);
        await harness.CreateProposalStore().TryCreateAsync(executing);
        (await harness.CreateNotificationStore().ExpireApprovalsAsync(100)).Should().Be(1);
        (await harness.CreateNotificationStore().ExpireApprovalsAsync(100)).Should().Be(0);
        var expired = (await harness.CreateProposalStore().GetAsync(waiting.ProposalId))!;
        expired.Status.Should().Be(OperationProposalStatus.Expired);
        expired.Version.Should().Be(1);
        (await harness.CreateProposalStore().GetAsync(executing.ProposalId))!.Status
            .Should().Be(OperationProposalStatus.Executing);
        var notice = (await harness.CreateNotificationStore().ListDueAsync(now.AddMinutes(1), 100)).Single();
        notice.Source.Should().Be("proposal");
        notice.SubjectId.Should().Be(waiting.ProposalId);
        (await harness.CreateProposalStore().TrySetAsync(expired with { Status = OperationProposalStatus.Executing }))
            .Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Expire_AtomicallyCancelsAwaitingInstanceAndRefreshesTerminalRetention()
    {
        await using var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var instanceId = $"opi-{Guid.NewGuid():N}";
        var proposal = Proposal(now, OperationProposalStatus.AwaitingApproval) with
        {
            Audit = new OperationAuditInfo { OperationInstanceId = instanceId }
        };
        await harness.CreateProposalStore().TryCreateAsync(proposal);
        await harness.CreateInstanceStore().TryCreateAsync(new OperationHandle
        {
            OperationInstanceId = instanceId, OperationId = "admin.test.operation", ProposalId = proposal.ProposalId,
            Status = OperationHandleStatus.RequiresApproval, CorrelationId = "correlation", CreatedAt = now, UpdatedAt = now
        });
        await harness.ExecuteAsync($"UPDATE {harness.Table("operation_proposals")} SET expires_at = now() - interval '1 second'");
        await harness.ExecuteAsync($"UPDATE {harness.Table("operation_instances")} SET expires_at = now() - interval '1 second'");
        (await harness.CreateNotificationStore().ExpireApprovalsAsync(100)).Should().Be(1);
        (await harness.CreateProposalStore().DeleteExpiredAsync(100, CancellationToken.None)).Should().Be(0);
        (await harness.CreateInstanceStore().DeleteExpiredAsync(100, CancellationToken.None)).Should().Be(0);
        var instance = (await harness.CreateInstanceStore().GetAsync(instanceId))!;
        instance.Status.Should().Be(OperationHandleStatus.Cancelled);
        instance.Reason.Should().Be("Human approval expired without execution.");
        instance.Version.Should().Be(1);
        (await harness.CreateInstanceStore().ListActiveAsync()).Should().BeEmpty();
        (await harness.CreateNotificationStore().ListDueAsync(now.AddMinutes(1), 100)).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task ListDue_BoundsBatchAndDoesNotReturnClosedOrFutureRows()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateNotificationStore();
        var now = DateTimeOffset.UtcNow;
        foreach (var subject in new[] { "a", "b", "c" })
        {
            await store.ObserveAsync("finding", subject, OpsFindingSeverity.Warning, now);
        }
        (await store.ListDueAsync(now, 2)).Should().HaveCount(2);
        await store.CloseMissingAsync("finding", ["a"], now);
        var remaining = (await store.ListDueAsync(now, 100)).Single();
        remaining.SubjectId.Should().Be("a");
        (await store.TryAdvanceAsync(remaining, now.AddDays(1))).Should().BeTrue();
        (await store.ListDueAsync(now, 100)).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task ObserveAwaiting_BoundsNewRowsAndPreservesClosedAndAdvancedSchedules()
    {
        await using var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        var first = Proposal(now, OperationProposalStatus.AwaitingApproval) with
        {
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-10)
        };
        var second = first with { ProposalId = Guid.NewGuid().ToString("N") };
        await harness.CreateProposalStore().TryCreateAsync(first);
        await harness.CreateProposalStore().TryCreateAsync(second);
        var store = harness.CreateNotificationStore();
        (await store.ObserveAwaitingAsync(1)).Should().Be(1);
        var observed = (await store.ListDueAsync(now.AddMinutes(1), 100)).Single();
        observed.FirstSeenAt.Should().BeCloseTo(first.CreatedAt, TimeSpan.FromMilliseconds(1));
        (await store.TryAdvanceAsync(observed, now.AddDays(1))).Should().BeTrue();
        (await harness.CreateNotificationStore().ObserveAwaitingAsync(100)).Should().Be(1);
        var other = (await store.ListDueAsync(now.AddMinutes(1), 100)).Single();
        other.SubjectId.Should().NotBe(observed.SubjectId);
        (await store.TryCloseAsync(other, now)).Should().BeTrue();
        (await store.TryCloseAsync(other, now)).Should().BeFalse();
        (await harness.CreateNotificationStore().ObserveAwaitingAsync(100)).Should().Be(0);
        (await store.ListDueAsync(now.AddMinutes(1), 100)).Should().BeEmpty();
        var reminder = (await store.ListDueAsync(now.AddDays(1), 100)).Single();
        reminder.EpisodeId.Should().Be(observed.EpisodeId);
        reminder.NoticeNumber.Should().Be(1);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresOpsNotificationStoreTests));

    private static OperationProposal Proposal(DateTimeOffset now, OperationProposalStatus status)
        => new()
        {
            ProposalId = Guid.NewGuid().ToString("N"),
            TenantId = "public",
            Kind = OperationClass.AdminConfigChange,
            Status = status,
            RequestedBy = "requester",
            Plan = new OperationProposalPlan { Summary = "Change a setting", RiskLevel = ProposalRiskLevel.Medium },
            CreatedAt = now.AddHours(-25),
            UpdatedAt = now.AddHours(-25)
        };
}
