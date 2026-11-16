// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Observability.Services;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed partial class OpsFindingsServiceTests
{
    [UnitTest]
    public async Task BackgroundEvaluation_EligibleFinding_UsesServerBoundaryWhileProposeUsesOrdinaryRoute()
    {
        var gateway = Substitute.For<IOperationGateway, IOpsRemediationGateway>();
        var remediation = (IOpsRemediationGateway)gateway;
        remediation.RouteRemediationAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationGatewayResult { Outcome = OperationGatewayOutcome.Executed, Decision = TestRemediationDecision() });
        gateway.RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationGatewayResult { Outcome = OperationGatewayOutcome.ProposalCreated, Decision = TestRemediationDecision() });
        var service = CreateService(alertHealth: FreshBacklog(), gateway: gateway);
        var finding = (await service.EvaluateAsync()).Single(item => item.Rule == OpsFindingsService.RuleAlertDispatchBacklog);
        var evaluator = Substitute.For<IOpsAutonomyEvaluator>();
        // Synthetic eligibility tests the boundary without adding a shipped auto-safe action.
        evaluator.EvaluateFindingAsync(Arg.Any<OpsFinding>(), Arg.Any<CancellationToken>())
            .Returns(call => new OpsAutonomyFindingDecision { CanAutoApply = call.Arg<OpsFinding>().Id == finding.Id });
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        using var provider = new ServiceCollection().AddSingleton(service).AddSingleton(evaluator).BuildServiceProvider();
        using var loop = new OpsFindingsAutonomyEvaluationService(provider.GetRequiredService<IServiceScopeFactory>(), jobs,
            new StaticOptionsMonitor<OpsAutonomyOptions>(new OpsAutonomyOptions()), NullLogger<OpsFindingsAutonomyEvaluationService>.Instance);

        await loop.EvaluateOnceAsync();
        await service.ProposeAsync(finding.Id);

        await remediation.Received(1).RouteRemediationAsync(Arg.Is<OperationGatewayRequest>(request =>
            request.IdempotencyKey == finding.Id && request.AutonomyContext != null), Arg.Any<CancellationToken>());
        await gateway.Received(1).RouteAsync(Arg.Is<OperationGatewayRequest>(request =>
            request.RequestedByAgent == OpsFindingsService.RequestedByAgent), Arg.Any<CancellationToken>());
        await jobs.Received(1).ReleaseLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task BackgroundEvaluation_EligibleFinding_ActuatesThroughCanonicalGatewayWithServiceAudit()
    {
        var (gateway, evaluator, executor, audit) = CanonicalRemediationGateway();
        var service = CreateService(alertHealth: FreshBacklog(), gateway: gateway);
        var finding = (await service.EvaluateAsync()).Single(item => item.Rule == OpsFindingsService.RuleAlertDispatchBacklog);
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        using var provider = new ServiceCollection().AddSingleton(service).AddSingleton(evaluator).BuildServiceProvider();
        using var loop = new OpsFindingsAutonomyEvaluationService(provider.GetRequiredService<IServiceScopeFactory>(), jobs,
            new StaticOptionsMonitor<OpsAutonomyOptions>(new OpsAutonomyOptions()), NullLogger<OpsFindingsAutonomyEvaluationService>.Instance);

        await loop.EvaluateOnceAsync();

        await executor.Received(1).ExecuteAsync(Arg.Is<OperationGatewayRequest>(request =>
            request.RequestedBy == "service:ops-findings" && request.RequestedByAgent == null
            && request.IdempotencyKey == finding.Id), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await audit.Received().RecordAsync(Arg.Is<AuditEvent>(item => item.Action == "operation.auto_applied"
            && item.Actor == "service:ops-findings" && item.ActorAgent == null), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("disabled")]
    [InlineData("kill-switch")]
    [InlineData("lease-denied")]
    [InlineData("unsafe-action")]
    public async Task BackgroundEvaluation_GuardDenies_NeverRoutesRemediation(string guard)
    {
        var gateway = Substitute.For<IOperationGateway, IOpsRemediationGateway>();
        var service = CreateService(alertHealth: FreshBacklog(), gateway: gateway);
        var options = new StaticOptionsMonitor<OpsAutonomyOptions>(new OpsAutonomyOptions
        {
            Enabled = guard != "disabled",
            KillSwitchEnabled = guard == "kill-switch",
        });
        var evaluator = new OpsAutonomyEvaluator(options, new InMemoryOpsAutonomyPolicyStore());
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(guard != "lease-denied");
        using var provider = new ServiceCollection().AddSingleton(service).AddSingleton<IOpsAutonomyEvaluator>(evaluator).BuildServiceProvider();
        using var loop = new OpsFindingsAutonomyEvaluationService(provider.GetRequiredService<IServiceScopeFactory>(), jobs,
            options, NullLogger<OpsFindingsAutonomyEvaluationService>.Instance);

        await loop.EvaluateOnceAsync();

        await ((IOpsRemediationGateway)gateway).DidNotReceive().RouteRemediationAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>());
        await gateway.DidNotReceive().RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("stale")]
    [InlineData("unavailable")]
    public async Task Remediate_StaleOrIncompleteEvidence_NeverRoutes(string evidenceState)
    {
        var health = FreshBacklog();
        health.LastPollAt = DateTimeOffset.UtcNow.AddHours(-1);
        health.IsStoragePollFailing = evidenceState == "unavailable";
        var gateway = Substitute.For<IOperationGateway, IOpsRemediationGateway>();
        var service = CreateService(alertHealth: health, gateway: gateway);
        var finding = (await service.EvaluateAsync()).Single(item => item.Rule == OpsFindingsService.RuleAlertDispatchBacklog);

        var result = await service.RemediateAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.Blocked, result.Status);
        await ((IOpsRemediationGateway)gateway).DidNotReceive().RouteRemediationAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>());
        await gateway.DidNotReceive().RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>());
    }


    private static (OperationGateway Gateway, IOpsAutonomyEvaluator Evaluator, IOperationExecutor Executor, IAuditLog Audit)
        CanonicalRemediationGateway()
    {
        var evaluator = Substitute.For<IOpsAutonomyEvaluator>();
        // Only eligibility is synthetic; the background loop, findings and canonical gateway are real.
        evaluator.EvaluateFindingAsync(Arg.Any<OpsFinding>(), Arg.Any<CancellationToken>())
            .Returns(call => new OpsAutonomyFindingDecision
            {
                CanAutoApply = call.Arg<OpsFinding>().Rule == OpsFindingsService.RuleAlertDispatchBacklog,
            });
        evaluator.EvaluateRouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<GuardrailDecision>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OpsAutonomyRouteDecision
            {
                ShouldAutoApply = true,
                ReservationId = "synthetic-reservation",
                Decision = new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.AdminConfigChange,
                    HonuaEdition.Community, "synthetic-eligibility"),
            });
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.AdminConfigChange, Arg.Any<string?>()).Returns(TestRemediationDecision());
        var executor = Substitute.For<IOperationExecutor>();
        executor.OperationClass.Returns(OperationClass.AdminConfigChange);
        executor.ExecuteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("remediation-operation");
        var convergence = Substitute.For<IAutonomousOperationConvergence>();
        convergence.CanHandle(Arg.Any<OperationGatewayRequest>()).Returns(true);
        convergence.VerifyAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AutonomousVerificationResult(AutonomousVerificationState.Converged, "test finding cleared"));
        var audit = Substitute.For<IAuditLog>();
        audit.RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>()).Returns("remediation-audit");
        var gateway = CanonicalOperationGatewayTestComposition.Build(Substitute.For<IOperationProposalStore>(), ladder,
            [executor], services => services.AddSingleton(evaluator).AddSingleton(convergence).AddSingleton(audit));
        return (gateway, evaluator, executor, audit);
    }

    private static GuardrailDecision TestRemediationDecision()
        => new(GuardrailTier.RequiresApproval, OperationClass.AdminConfigChange, HonuaEdition.Community, "test");

    private static FakeAlertDispatchHealth FreshBacklog() => new()
    {
        IsDispatcherEnabled = true,
        IsDispatcherRunning = true,
        LastPollAt = DateTimeOffset.UtcNow,
        LastBacklog = new AlertDispatchBacklog { PendingCount = 3, DeadLetteredCount = 2 },
    };
}
