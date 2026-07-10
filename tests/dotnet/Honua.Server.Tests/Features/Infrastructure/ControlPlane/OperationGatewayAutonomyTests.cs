// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.ControlPlane;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class OperationGatewayAutonomyTests
{
    private const string Rule = "alert-dispatch-backlog";
    private const string FindingId = "alert-dispatch-backlog-test";
    private const string RedriveAction = "alerts.redrive_dead_letters";

    [Fact]
    public async Task RouteAsync_AutonomyApprovesApprovalTierRequest_ExecutesDirectWithoutProposal()
    {
        var store = new MultiProposalStore();
        var executor = new RecordingExecutor();
        var auditLog = new RecordingAuditLog();
        var outbox = new RecordingAlertOutbox();
        var evaluator = new RecordingAutonomyEvaluator(
            new OpsAutonomyRouteDecision
            {
                ShouldAutoApply = true,
                Decision = DirectExecuteDecision(),
                ReservationId = "auto-1",
                Reason = "auto-apply-reserved",
            });
        var convergence = RecordingConvergence.Converged();
        var sut = BuildGateway(
            store,
            evaluator,
            executor,
            convergence,
            auditLog,
            CreateOpsNotifier(outbox));

        var result = await sut.RouteAsync(Request());

        result.Outcome.Should().Be(OperationGatewayOutcome.Executed);
        result.ExecutionOperationId.Should().Be(RecordingExecutor.OperationId);
        result.Message.Should().Contain("Auto-applied");
        store.Count.Should().Be(0, "auto-apply must not also create an approval proposal");
        executor.ExecuteCount.Should().Be(1);
        convergence.VerifyCount.Should().Be(1);
        convergence.CompensateCount.Should().Be(0);
        evaluator.OutcomeCount.Should().Be(1);
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.Succeeded);
        evaluator.ProposalRaisedCount.Should().Be(0);
        auditLog.Events.Select(item => item.Action).Should().ContainInOrder(
            "operation.auto_executed",
            "operation.auto_verified",
            "operation.auto_applied");
        outbox.Events.Should().ContainSingle();
        outbox.Events[0].Source.Should().Be(AlertEventSources.Ops);
        outbox.Events[0].ServiceId.Should().Be("ops-autonomy");
        Assert.Contains(FindingId, outbox.Events[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteAsync_AutonomyDeniesApprovalTierRequest_CreatesProposalAndRecordsTrack()
    {
        var store = new MultiProposalStore();
        var evaluator = new RecordingAutonomyEvaluator(
            new OpsAutonomyRouteDecision
            {
                ShouldAutoApply = false,
                Reason = "policy-propose-only",
            });
        var sut = BuildGateway(store, evaluator, new RecordingExecutor(), RecordingConvergence.Converged());

        var result = await sut.RouteAsync(Request());

        result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        result.ProposalId.Should().NotBeNullOrWhiteSpace();
        store.Count.Should().Be(1);
        evaluator.OutcomeCount.Should().Be(0);
        evaluator.ProposalRaisedCount.Should().Be(1);
    }

    [Fact]
    public async Task RouteAsync_AutonomyDirectExecutionFails_RecordsFailedOutcome()
    {
        var store = new MultiProposalStore();
        var evaluator = new RecordingAutonomyEvaluator(
            new OpsAutonomyRouteDecision
            {
                ShouldAutoApply = true,
                Decision = DirectExecuteDecision(),
                ReservationId = "auto-1",
            });
        var sut = BuildGateway(store, evaluator, new ThrowingExecutor(), RecordingConvergence.Converged());

        var act = () => sut.RouteAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Count.Should().Be(0);
        evaluator.OutcomeCount.Should().Be(1);
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.Failed);
    }

    [Fact]
    public async Task RouteAsync_AutonomousExecutorIsNotRegistered_PreservesNotSupportedOutcome()
    {
        var evaluator = AutoApplyingEvaluator();
        var auditLog = new RecordingAuditLog();
        var sut = BuildGateway(
            new MultiProposalStore(),
            evaluator,
            new WrongKindExecutor(),
            RecordingConvergence.Converged(),
            auditLog);

        var result = await sut.RouteAsync(Request());

        result.Outcome.Should().Be(OperationGatewayOutcome.NotSupported);
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.Failed);
        auditLog.Events.Should().ContainSingle(item => item.Action == "operation.auto_failed");
    }

    [Fact]
    public async Task RouteAsync_AutoSafeActionHasNoConvergenceVerifier_FailsBeforeActuation()
    {
        var evaluator = AutoApplyingEvaluator();
        var executor = new RecordingExecutor();
        var sut = BuildGateway(
            new MultiProposalStore(),
            evaluator,
            executor,
            RecordingConvergence.Unavailable());

        var result = await sut.RouteAsync(Request());

        result.Outcome.Should().Be(OperationGatewayOutcome.Failed);
        result.Message.Should().Contain("No post-action convergence verifier");
        executor.ExecuteCount.Should().Be(0, "autonomy must fail closed before invoking an unverifiable action");
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.Failed);
    }

    [Fact]
    public async Task RouteAsync_VerificationFailsWithoutCompensation_RequiresManualIntervention()
    {
        var evaluator = AutoApplyingEvaluator();
        var auditLog = new RecordingAuditLog();
        var outbox = new RecordingAlertOutbox();
        var convergence = RecordingConvergence.VerificationFailed(
            "dead-letter backlog still exceeds the threshold",
            supportsCompensation: false);
        var sut = BuildGateway(
            new MultiProposalStore(),
            evaluator,
            new RecordingExecutor(),
            convergence,
            auditLog,
            CreateOpsNotifier(outbox));

        var result = await sut.RouteAsync(Request());

        result.Outcome.Should().Be(OperationGatewayOutcome.Failed);
        result.ExecutionOperationId.Should().Be(RecordingExecutor.OperationId);
        result.Message.Should().Contain("dead-letter backlog still exceeds the threshold");
        result.Message.Should().Contain("compensation is not supported");
        result.Message.Should().Contain("manual intervention required");
        convergence.VerifyCount.Should().Be(1);
        convergence.CompensateCount.Should().Be(0);
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.Failed);
        evaluator.LastMessage.Should().Be(result.Message);
        auditLog.Events.Select(item => item.Action).Should().ContainInOrder(
            "operation.auto_executed",
            "operation.auto_verified",
            "operation.auto_failed");
        auditLog.Events.Last().Outcome.Should().Be(AuditOutcome.Failure);
        outbox.Events.Should().ContainSingle();
        outbox.Events[0].PayloadJson.Should().Contain("manual intervention required");
    }

    [Fact]
    public async Task RouteAsync_VerificationFailsAndCompensationSucceeds_RecordsRolledBack()
    {
        var evaluator = AutoApplyingEvaluator();
        var auditLog = new RecordingAuditLog();
        var convergence = RecordingConvergence.VerificationFailed(
            "post-action observation regressed",
            supportsCompensation: true,
            new AutonomousCompensationResult(
                AutonomousCompensationState.RolledBack,
                "restored the pre-action state"));
        var sut = BuildGateway(
            new MultiProposalStore(),
            evaluator,
            new RecordingExecutor(),
            convergence,
            auditLog);

        var result = await sut.RouteAsync(Request());

        result.Outcome.Should().Be(OperationGatewayOutcome.RolledBack);
        result.Message.Should().Contain("post-action observation regressed");
        result.Message.Should().Contain("restored the pre-action state");
        convergence.CompensateCount.Should().Be(1);
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.RolledBack);
        auditLog.Events.Select(item => item.Action).Should().ContainInOrder(
            "operation.auto_executed",
            "operation.auto_verified",
            "operation.auto_compensated",
            "operation.auto_rolled_back");
    }

    [Fact]
    public async Task RouteAsync_CompensationFails_PreservesFailureOfFailureAsIndeterminate()
    {
        var evaluator = AutoApplyingEvaluator();
        var auditLog = new RecordingAuditLog();
        var outbox = new RecordingAlertOutbox();
        var convergence = RecordingConvergence.VerificationFailed(
            "verification evidence: finding persisted",
            supportsCompensation: true,
            new AutonomousCompensationResult(
                AutonomousCompensationState.Failed,
                "compensation evidence: rollback actuator timed out"));
        var sut = BuildGateway(
            new MultiProposalStore(),
            evaluator,
            new RecordingExecutor(),
            convergence,
            auditLog,
            CreateOpsNotifier(outbox));

        var result = await sut.RouteAsync(Request());

        result.Outcome.Should().Be(OperationGatewayOutcome.Indeterminate);
        result.Message.Should().Contain("verification evidence: finding persisted");
        result.Message.Should().Contain("compensation evidence: rollback actuator timed out");
        result.Message.Should().Contain("manual intervention required");
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.Indeterminate);
        evaluator.LastMessage.Should().Be(result.Message);
        auditLog.Events.Select(item => item.Action).Should().ContainInOrder(
            "operation.auto_executed",
            "operation.auto_verified",
            "operation.auto_compensated",
            "operation.auto_indeterminate");
        outbox.Events.Should().ContainSingle();
        outbox.Events[0].PayloadJson.Should().Contain("rollback actuator timed out");
    }

    [Fact]
    public async Task RouteAsync_VerificationIsCancelledAfterExecution_RecordsCanceledNotFailedOrIndeterminate()
    {
        var evaluator = AutoApplyingEvaluator();
        var convergence = RecordingConvergence.Cancelled();
        var auditLog = new RecordingAuditLog();
        var outbox = new RecordingAlertOutbox();
        var sut = BuildGateway(
            new MultiProposalStore(),
            evaluator,
            new RecordingExecutor(),
            convergence,
            auditLog,
            CreateOpsNotifier(outbox));

        var act = () => sut.RouteAsync(Request());

        await act.Should().ThrowAsync<OperationCanceledException>();
        evaluator.LastOutcome.Should().Be(OpsAutonomyActionOutcome.Canceled);
        evaluator.LastMessage.Should().Contain("verification was canceled after execution");
        convergence.CompensateCount.Should().Be(0);
        auditLog.Events.Select(item => item.Action).Should().ContainInOrder(
            "operation.auto_executed",
            "operation.auto_canceled");
        outbox.Events.Should().ContainSingle();
        outbox.Events[0].PayloadJson.Should().Contain("\"outcome\":\"Canceled\"");
    }

    private static OperationGateway BuildGateway(
        MultiProposalStore store,
        IOpsAutonomyEvaluator evaluator,
        IOperationExecutor executor,
        IAutonomousOperationConvergence convergence,
        IAuditLog? auditLog = null,
        OpsNotificationService? opsNotificationService = null)
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.AdminConfigChange, RedriveAction).Returns(RequiresApprovalDecision());

        var services = new ServiceCollection();
        services.AddScoped<IAuditLog>(_ => auditLog ?? NullAuditLog.Instance);
        services.AddScoped(_ => evaluator);
        services.AddScoped(_ => convergence);
        if (opsNotificationService is not null)
        {
            services.AddScoped(_ => opsNotificationService);
        }

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new OperationGateway(
            ladder,
            store,
            [executor],
            scopeFactory,
            Substitute.For<IProposalNotifier>(),
            NullLogger<OperationGateway>.Instance);
    }

    private static RecordingAutonomyEvaluator AutoApplyingEvaluator()
        => new(
            new OpsAutonomyRouteDecision
            {
                ShouldAutoApply = true,
                Decision = DirectExecuteDecision(),
                ReservationId = "auto-1",
                Reason = "auto-apply-reserved",
            });

    private static OpsNotificationService CreateOpsNotifier(RecordingAlertOutbox outbox)
    {
        var options = new AlertOptions
        {
            Ops = new AlertOpsOptions
            {
                Enabled = true,
                Channels = ["webhook"],
                MinSeverity = AlertSeverity.Info,
            },
            Dispatch = new AlertDispatchOptions
            {
                CircuitBreakerThreshold = 0,
            },
        };

        return new OpsNotificationService(
            new AlertDispatchWriter(outbox, NullLogger<AlertDispatchWriter>.Instance),
            new AllowAllAlertEditionPolicy(),
            new AlertChannelCircuitBreaker(Options.Create(options)),
            Options.Create(options),
            NullLogger<OpsNotificationService>.Instance);
    }

    private static OperationGatewayRequest Request()
        => new()
        {
            Kind = OperationClass.AdminConfigChange,
            ActionDiscriminator = RedriveAction,
            RequestedByAgent = "ops-findings-autonomy",
            Reason = "Redrive dead-lettered alert dispatches.",
            IdempotencyKey = FindingId,
            ExecutionPayload = "{\"action\":\"alerts.redrive_dead_letters\"}",
            AutonomyContext = new OperationGatewayAutonomyContext
            {
                FindingId = FindingId,
                Rule = Rule,
                ActionMarkedAutoSafe = true,
                BlastRadius = 1,
                EvidenceRefs = ["test"],
            },
        };

    private static GuardrailDecision RequiresApprovalDecision()
        => new(
            GuardrailTier.RequiresApproval,
            OperationClass.AdminConfigChange,
            HonuaEdition.Enterprise,
            "test");

    private static GuardrailDecision DirectExecuteDecision()
        => new(
            GuardrailTier.DirectExecute,
            OperationClass.AdminConfigChange,
            HonuaEdition.Enterprise,
            $"autonomy-policy:{Rule}");

    private sealed class RecordingAutonomyEvaluator(OpsAutonomyRouteDecision routeDecision) : IOpsAutonomyEvaluator
    {
        public int OutcomeCount { get; private set; }

        public int ProposalRaisedCount { get; private set; }

        public OpsAutonomyActionOutcome? LastOutcome { get; private set; }

        public string? LastMessage { get; private set; }

        public Task<OpsAutonomyFindingDecision> EvaluateFindingAsync(
            OpsFinding finding,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OpsAutonomyFindingDecision { CanAutoApply = false, Reason = "not-used" });

        public Task<OpsAutonomyRouteDecision> EvaluateRouteAsync(
            OperationGatewayRequest request,
            GuardrailDecision currentDecision,
            string? actionDiscriminator,
            CancellationToken cancellationToken = default)
            => Task.FromResult(routeDecision);

        public Task RecordAutoActionOutcomeAsync(
            OpsAutonomyRouteDecision decision,
            OpsAutonomyActionOutcome outcome,
            string? operationId = null,
            string? message = null,
            CancellationToken cancellationToken = default)
        {
            OutcomeCount++;
            LastOutcome = outcome;
            LastMessage = message;
            return Task.CompletedTask;
        }

        public Task RecordProposalRaisedAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            ProposalRaisedCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConvergence(
        AutonomousVerificationResult verification,
        bool supportsCompensation,
        AutonomousCompensationResult compensation,
        bool cancelVerification = false,
        bool canHandle = true) : IAutonomousOperationConvergence
    {
        public int VerifyCount { get; private set; }

        public int CompensateCount { get; private set; }

        public bool CanHandle(OperationGatewayRequest request) => canHandle;

        public bool SupportsCompensation(OperationGatewayRequest request) => supportsCompensation;

        public Task<AutonomousVerificationResult> VerifyAsync(
            OperationGatewayRequest request,
            string? executionOperationId,
            CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            return cancelVerification
                ? Task.FromCanceled<AutonomousVerificationResult>(new CancellationToken(canceled: true))
                : Task.FromResult(verification);
        }

        public Task<AutonomousCompensationResult> CompensateAsync(
            OperationGatewayRequest request,
            string? executionOperationId,
            CancellationToken cancellationToken = default)
        {
            CompensateCount++;
            return Task.FromResult(compensation);
        }

        public static RecordingConvergence Converged()
            => new(
                new AutonomousVerificationResult(
                    AutonomousVerificationState.Converged,
                    "the finding remained clear"),
                supportsCompensation: false,
                new AutonomousCompensationResult(
                    AutonomousCompensationState.NotSupported,
                    "compensation is not supported"));

        public static RecordingConvergence VerificationFailed(
            string message,
            bool supportsCompensation,
            AutonomousCompensationResult? compensation = null)
            => new(
                new AutonomousVerificationResult(AutonomousVerificationState.Failed, message),
                supportsCompensation,
                compensation ?? new AutonomousCompensationResult(
                    AutonomousCompensationState.NotSupported,
                    "compensation is not supported"));

        public static RecordingConvergence Cancelled()
            => new(
                new AutonomousVerificationResult(AutonomousVerificationState.Indeterminate, "not reached"),
                supportsCompensation: false,
                new AutonomousCompensationResult(
                    AutonomousCompensationState.NotSupported,
                    "compensation is not supported"),
                cancelVerification: true);

        public static RecordingConvergence Unavailable()
            => new(
                new AutonomousVerificationResult(AutonomousVerificationState.Indeterminate, "not reached"),
                supportsCompensation: false,
                new AutonomousCompensationResult(
                    AutonomousCompensationState.NotSupported,
                    "compensation is not supported"),
                canHandle: false);
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAlertOutbox : IAlertOutboxWriter
    {
        public List<AlertEventEnvelope> Events { get; } = [];

        public Task<long?> AppendAndEnqueueAsync(
            AlertEventEnvelope alertEvent,
            System.Collections.Immutable.ImmutableArray<AlertChannelType> channels,
            CancellationToken cancellationToken = default)
        {
            Events.Add(alertEvent);
            return Task.FromResult<long?>(Events.Count);
        }
    }

    private sealed class AllowAllAlertEditionPolicy : IAlertEditionPolicy
    {
        public bool IsRuleAllowed(AlertRuleDefinition rule) => true;

        public bool IsChannelAllowed(AlertChannelType channelType) => true;

        public bool IsChannelConfigured(AlertChannelType channelType) => true;
    }

    private sealed class RecordingExecutor : IOperationExecutor
    {
        public const string OperationId = "ops-action-op";

        public int ExecuteCount { get; private set; }

        public OperationClass OperationClass => OperationClass.AdminConfigChange;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
            {
                Summary = "redrive",
                ExecutionPayload = request.ExecutionPayload,
            });

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult<string?>(OperationId);
        }
    }

    private sealed class ThrowingExecutor : IOperationExecutor
    {
        public OperationClass OperationClass => OperationClass.AdminConfigChange;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
            {
                Summary = "redrive",
                ExecutionPayload = request.ExecutionPayload,
            });

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated failure");
    }

    private sealed class WrongKindExecutor : IOperationExecutor
    {
        public OperationClass OperationClass => OperationClass.Deploy;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(null);

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The wrong-kind executor must never be invoked.");
    }

    private sealed class MultiProposalStore : IOperationProposalStore
    {
        private readonly Dictionary<string, OperationProposal> _proposals = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();

        public int Count
        {
            get { lock (_lock) { return _proposals.Count; } }
        }

        public Task<OperationProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_proposals.TryGetValue(proposalId, out var proposal) ? proposal : null);
            }
        }

        public Task<bool> TryCreateAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_proposals.ContainsKey(proposal.ProposalId))
                {
                    return Task.FromResult(false);
                }

                _proposals[proposal.ProposalId] = proposal with { Version = 1 };
                return Task.FromResult(true);
            }
        }

        public Task<bool> TrySetAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (!_proposals.TryGetValue(proposal.ProposalId, out var current) || current.Version != proposal.Version)
                {
                    return Task.FromResult(false);
                }

                _proposals[proposal.ProposalId] = proposal with { Version = proposal.Version + 1 };
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
            OperationClass? kind = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var active = _proposals.Values
                    .Where(proposal => proposal.Status == OperationProposalStatus.AwaitingApproval)
                    .Where(proposal => kind == null || proposal.Kind == kind)
                    .ToList();
                return Task.FromResult<IReadOnlyList<OperationProposal>>(active);
            }
        }

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(
            string operationId,
            string ownerId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
