// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Alerts;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class OpsNotificationReminderRunnerTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task DisabledDelivery_PreservesPendingIdentityUntilEnabledAndSurvivesRunnerRestart()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow);
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(new[] { Finding(clock.GetUtcNow(), OpsFindingSeverity.Critical) });
        var options = new AlertOptions { Enabled = true, Ops = new AlertOpsOptions { Enabled = false, Channels = ["webhook"] } };
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = CreateNotifications(outbox, options);
        OpsNotificationReminderRunner Runner() => new(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, Substitute.For<IAlertDispatchHealth>());
        await Runner().RunAsync(CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(1);
        var pending = (await harness.CreateNotificationStore().ListDueAsync(clock.GetUtcNow(), 100)).Single();
        await outbox.DidNotReceive().CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>());
        options.Ops.Enabled = true;
        await Runner().RunAsync(CancellationToken.None);
        await Runner().RunAsync(CancellationToken.None);
        await outbox.Received(1).CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 1 && entries[0].AlertEvent.DedupeKey ==
                $"ops:ops-reminders:finding:finding-1:{pending.EpisodeId:N}:Critical:0"), Arg.Any<CancellationToken>());
        clock.Now = clock.Now.AddMinutes(30);
        await Runner().RunAsync(CancellationToken.None);
        await outbox.Received(2).CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>());
        (await harness.CreateNotificationStore().ListDueAsync(clock.GetUtcNow(), 100)).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task ExpiredApproval_EmitsOneTerminalNoticeWithoutClosingFindingOrExecuting()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(new[] { Finding(clock.GetUtcNow(), OpsFindingSeverity.Warning) });
        var proposal = new OperationProposal
        {
            ProposalId = Guid.NewGuid().ToString("N"), TenantId = "public", Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.AwaitingApproval, RequestedBy = "requester",
            Plan = new OperationProposalPlan { Summary = "Approval required", RiskLevel = ProposalRiskLevel.Medium },
            CreatedAt = clock.GetUtcNow().AddHours(-25), UpdatedAt = clock.GetUtcNow().AddHours(-25)
        };
        await harness.CreateProposalStore().TryCreateAsync(proposal);
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = CreateNotifications(outbox, new AlertOptions
        {
            Enabled = true, Ops = new AlertOpsOptions { Enabled = true, Channels = ["webhook"] }
        });
        var runner = new OpsNotificationReminderRunner(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, Substitute.For<IAlertDispatchHealth>());
        await runner.RunAsync(CancellationToken.None);
        await runner.RunAsync(CancellationToken.None);
        (await harness.CreateProposalStore().GetAsync(proposal.ProposalId))!.Status.Should().Be(OperationProposalStatus.Expired);
        await outbox.Received(1).CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 1 &&
                entries[0].AlertEvent.ProposalId == proposal.ProposalId), Arg.Any<CancellationToken>());
        var tomorrow = await harness.CreateNotificationStore().ListDueAsync(clock.GetUtcNow().AddDays(1), 100);
        tomorrow.Should().ContainSingle(schedule => schedule.Source == "finding" && schedule.SubjectId == "finding-1");
        tomorrow.Should().NotContain(schedule => schedule.Source == "proposal");
    }

    [IntegrationTest]
    public async Task ExpiredApproval_DeferredNoticeSurvivesRetentionSweepAndRunnerRestart()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<OpsFinding>());
        var proposal = new OperationProposal
        {
            ProposalId = Guid.NewGuid().ToString("N"), TenantId = "public", Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.AwaitingApproval, RequestedBy = "requester",
            Plan = new OperationProposalPlan { Summary = "Approval required", RiskLevel = ProposalRiskLevel.Medium },
            CreatedAt = clock.Now.AddHours(-25), UpdatedAt = clock.Now.AddHours(-25)
        };
        await harness.CreateProposalStore().TryCreateAsync(proposal);
        await harness.ExecuteAsync($"UPDATE {harness.Table("operation_proposals")} SET expires_at = now() - interval '1 second'");
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var options = new AlertOptions { Enabled = true, Ops = new AlertOpsOptions { Enabled = false, Channels = ["webhook"] } };
        var notifications = CreateNotifications(outbox, options);
        OpsNotificationReminderRunner Runner() => new(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, Substitute.For<IAlertDispatchHealth>());
        await Runner().RunAsync(CancellationToken.None);
        // Delivery may remain disabled longer than the refreshed terminal retention window.
        await harness.ExecuteAsync($"UPDATE {harness.Table("operation_proposals")} SET expires_at = now() - interval '1 second'");
        (await harness.CreateProposalStore().DeleteExpiredAsync(100, CancellationToken.None)).Should().Be(0);
        clock.Now = clock.Now.AddMinutes(1);
        options.Ops.Enabled = true;
        await Runner().RunAsync(CancellationToken.None);
        await Runner().RunAsync(CancellationToken.None);
        await outbox.Received(1).CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Single().AlertEvent.ProposalId == proposal.ProposalId),
            Arg.Any<CancellationToken>());
        (await harness.CreateProposalStore().DeleteExpiredAsync(100, CancellationToken.None)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task InformationalFinding_DoesNotCreateImmediateNotification()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow);
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(new[] { Finding(clock.GetUtcNow(), OpsFindingSeverity.Info) });
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = CreateNotifications(outbox, new AlertOptions
        {
            Enabled = true, Ops = new AlertOpsOptions { Enabled = true, Channels = ["webhook"] }
        });
        await new OpsNotificationReminderRunner(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, Substitute.For<IAlertDispatchHealth>()).RunAsync(CancellationToken.None);
        (await harness.CreateNotificationStore().ListDueAsync(clock.GetUtcNow().AddDays(1), 100)).Should().BeEmpty();
        await outbox.DidNotReceive().CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    public async Task Escalation_AfterOutboxCommitBeforeAdvance_UsesDistinctIdentityOnRestart()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow);
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(new[] { Finding(clock.Now, OpsFindingSeverity.Warning) });
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = CreateNotifications(outbox, new AlertOptions
        {
            Enabled = true, Ops = new AlertOpsOptions { Enabled = true, Channels = ["webhook"] }
        });
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var crash = true;
        outbox.CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var accepted = call.ArgAt<IReadOnlyList<AlertOutboxEntry>>(1)
                    .Select(entry => committed.Add(entry.AlertEvent.DedupeKey)).ToImmutableArray();
                if (crash)
                {
                    throw new InvalidOperationException("Simulated crash after commit");
                }
                return accepted;
            });
        OpsNotificationReminderRunner Runner() => new(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, Substitute.For<IAlertDispatchHealth>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(CancellationToken.None));
        crash = false;
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(new[] { Finding(clock.Now, OpsFindingSeverity.Critical) });
        await Runner().RunAsync(CancellationToken.None);
        await Runner().RunAsync(CancellationToken.None);
        committed.Should().HaveCount(2);
        committed.Should().Contain(key => key.EndsWith(":Warning:0", StringComparison.Ordinal));
        committed.Should().Contain(key => key.EndsWith(":Critical:0", StringComparison.Ordinal));
    }

    [IntegrationTest]
    public async Task FilteredWarningBatch_DoesNotStarveLaterCriticalFindingOrConsumePendingOrdinal()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow);
        var values = Enumerable.Range(0, 100).Select(index => Finding(clock.Now, OpsFindingSeverity.Warning)
            with { Id = $"warning-{index:D3}" }).ToList();
        var store = harness.CreateNotificationStore();
        foreach (var finding in values)
        {
            await store.ObserveAsync(new("finding", finding.Id, finding.Severity), clock.Now.AddMinutes(-1));
        }
        values.Add(Finding(clock.Now, OpsFindingSeverity.Critical));
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(values);
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = CreateNotifications(outbox, new AlertOptions
        {
            Enabled = true, Ops = new AlertOpsOptions
            { Enabled = true, Channels = ["webhook"], MinSeverity = AlertSeverity.Critical }
        });
        OpsNotificationReminderRunner Runner() => new(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, Substitute.For<IAlertDispatchHealth>());
        await Runner().RunAsync(CancellationToken.None);
        await Runner().RunAsync(CancellationToken.None);
        await outbox.Received(1).CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Single().AlertEvent.Severity == AlertSeverity.Critical),
            Arg.Any<CancellationToken>());
        var pending = await store.ListDueAsync(clock.Now.AddMinutes(1), 100);
        pending.Should().HaveCount(100).And.OnlyContain(schedule => schedule.NoticeNumber == 0);
    }

    [IntegrationTest]
    public async Task CriticalApproval_RemindsDailyInsteadOfUsingCriticalFindingCadence()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<OpsFinding>());
        await harness.CreateProposalStore().TryCreateAsync(new OperationProposal
        {
            ProposalId = Guid.NewGuid().ToString("N"), TenantId = "public", Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.AwaitingApproval, RequestedBy = "requester",
            Plan = new OperationProposalPlan { Summary = "Critical approval", RiskLevel = ProposalRiskLevel.High },
            CreatedAt = clock.Now.AddMinutes(-2), UpdatedAt = clock.Now.AddMinutes(-2)
        });
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = CreateNotifications(outbox, new AlertOptions
        {
            Enabled = true, Ops = new AlertOpsOptions { Enabled = true, Channels = ["webhook"] }
        });
        await new OpsNotificationReminderRunner(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, Substitute.For<IAlertDispatchHealth>()).RunAsync(CancellationToken.None);
        (await harness.CreateNotificationStore().ListDueAsync(clock.Now.AddHours(12), 100)).Should().BeEmpty();
        (await harness.CreateNotificationStore().ListDueAsync(clock.Now.AddDays(1), 100)).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task ColdTick_RefreshesDurableAlertObservationBeforeEvaluatingFindings()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow);
        var health = Substitute.For<IAlertDispatchHealth>();
        var refreshed = false;
        health.RefreshBacklogAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            refreshed = true;
            return new AlertDispatchBacklog { PendingCount = 1, DeadLetteredCount = 0 };
        });
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(_ => refreshed
            ? new[] { Finding(clock.Now, OpsFindingSeverity.Critical) } : Array.Empty<OpsFinding>());
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var notifications = CreateNotifications(outbox, new AlertOptions
        {
            Enabled = true, Ops = new AlertOpsOptions { Enabled = true, Channels = ["webhook"] }
        });
        await new OpsNotificationReminderRunner(harness.CreateNotificationStore(), findings,
            harness.CreateProposalStore(), notifications, clock, health).RunAsync(CancellationToken.None);
        await outbox.Received(1).CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Single().AlertEvent.Severity == AlertSeverity.Critical),
            Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    public async Task UnavailableAlertObservation_PreservesExistingUnresolvedReminder()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(OpsNotificationReminderRunnerTests));
        var clock = new ReminderClock(DateTimeOffset.UtcNow);
        var store = harness.CreateNotificationStore();
        await store.ObserveAsync(new("finding", "finding-1", OpsFindingSeverity.Critical), clock.Now);
        var pending = (await store.ListDueAsync(clock.Now, 100)).Single();
        var health = Substitute.For<IAlertDispatchHealth>();
        health.RefreshBacklogAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<AlertDispatchBacklog>(new InvalidOperationException("Storage unavailable")));
        var findings = Substitute.For<IOpsFindingsService>();
        findings.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<OpsFinding>());
        var notifications = CreateNotifications(Substitute.For<IAlertOutboxWriter>(), new AlertOptions());
        var runner = new OpsNotificationReminderRunner(store, findings, harness.CreateProposalStore(), notifications, clock, health);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(CancellationToken.None));
        await findings.DidNotReceive().EvaluateAsync(Arg.Any<CancellationToken>());
        (await store.ListDueAsync(clock.Now, 100)).Should().ContainSingle().Which.Should().BeEquivalentTo(pending);
    }

    private static OpsFinding Finding(DateTimeOffset now, OpsFindingSeverity severity) => new()
    {
        Id = "finding-1", Rule = "test-rule", Severity = severity, Title = "Finding",
        Explanation = "Needs operator attention", DetectedAt = now, Subject = new OpsFindingSubject(), EvidenceRefs = []
    };

    private static OpsNotificationService CreateNotifications(IAlertOutboxWriter outbox, AlertOptions options)
    {
        outbox.CommitEvaluationAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Any<IReadOnlyList<AlertOutboxEntry>>(), Arg.Any<CancellationToken>())
            .Returns(call => ImmutableArray.CreateRange(Enumerable.Repeat(true, call.ArgAt<IReadOnlyList<AlertOutboxEntry>>(1).Count)));
        var policy = Substitute.For<IAlertEditionPolicy>();
        policy.IsChannelAllowed(Arg.Any<AlertChannelType>()).Returns(true);
        var configured = Options.Create(options);
        var metrics = TestTelemetry.CreateAlertPipelineMetrics();
        return new OpsNotificationService(new AlertDispatchWriter(outbox, metrics, NullLogger<AlertDispatchWriter>.Instance),
            policy, new AlertChannelCircuitBreaker(configured), configured, metrics, NullLogger<OpsNotificationService>.Instance);
    }

    private sealed class ReminderClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
