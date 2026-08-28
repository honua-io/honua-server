// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.ControlPlane;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LegacyExecutor = Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor;
using TypedExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

internal static class CanonicalOperationGatewayTestComposition
{
    internal static LegacyExecutor PlanningOnly(Honua.Core.Features.Guardrails.Domain.OperationClass operationClass)
        => new PlanningOnlyExecutor(operationClass);

    internal static OperationGateway Build(
        IOperationProposalStore proposalStore,
        IGuardrailLadder ladder,
        IEnumerable<LegacyExecutor> actuators,
        Action<IServiceCollection>? configure = null,
        IProposalNotifier? notifier = null)
    {
        var actuatorArray = actuators.ToArray();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditLog, TestAuditLog>();
        configure?.Invoke(services);

        var bridge = new ForwardingApprovalBridge();
        // Legacy proposal fixtures predate operation-instance identity. This explicit
        // test-only store supplies the migrated acceptance envelope so those tests can
        // continue exercising gateway state transitions; production replay remains
        // fail-closed when the original durable instance is absent.
        var instanceStore = new LegacyProposalTestOperationInstanceStore();
        var typedActuators = actuatorArray
            .Select(actuator => (TypedExecutor)new LegacyGatewayOperationAdapter(actuator))
            .ToArray();
        var catalog = new OperationCatalog(
            [new ServerOperationDescriptorProvider(actuatorArray)],
            TimeProvider.System);
        var policy = new CanonicalOperationPolicyDecisionPoint(
            Options.Create(new OperationPolicyOptions()),
            ladder);
        var invoker = new OperationDispatcher(
            catalog,
            typedActuators,
            policy,
            TimeProvider.System,
            bridge,
            instanceStore,
            new TestAuditLog());
        services.AddSingleton<IOperationInvoker>(invoker);

        var provider = services.BuildServiceProvider();
        var gateway = new OperationGateway(
            ladder,
            proposalStore,
            actuatorArray,
            provider.GetRequiredService<IServiceScopeFactory>(),
            notifier ?? new NullProposalNotifier(),
            NullLogger<OperationGateway>.Instance);
        bridge.Gateway = gateway;
        return gateway;
    }

    private sealed class ForwardingApprovalBridge : IOperationApprovalBridge
    {
        public OperationGateway? Gateway { get; set; }

        public async Task<OperationApprovalBridgeResult> CreateProposalAsync(
            IOperationDescriptor descriptor,
            OperationRequest request,
            OperationPolicyContext context,
            PolicyDecision decision,
            CancellationToken cancellationToken = default)
        {
            if (Gateway is null || request.GatewayRequest is null)
            {
                return new OperationApprovalBridgeResult
                {
                    IsDurable = false,
                    Reason = "Canonical test proposal runtime is unavailable.",
                };
            }

            var result = await Gateway.CreateApprovalProposalAsync(
                context.OperationInstanceId
                    ?? throw new InvalidOperationException("Canonical test identity is unavailable."),
                request.GatewayRequest with
                {
                    OperationInstanceId = context.OperationInstanceId,
                    CorrelationId = context.CorrelationId,
                },
                cancellationToken);
            return new OperationApprovalBridgeResult
            {
                IsDurable = result.Outcome == OperationGatewayOutcome.ProposalCreated,
                ProposalId = result.ProposalId,
                AuditId = result.AuditId,
                Reason = result.Message,
            };
        }
    }

    private sealed class TestAuditLog : IAuditLog
    {
        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>($"audit-test-{Guid.NewGuid():N}");
    }

    private sealed class LegacyProposalTestOperationInstanceStore : IOperationInstanceStore
    {
        private readonly VolatileOperationInstanceStore _inner = new();

        public Task<bool> TryCreateAsync(
            OperationHandle envelope,
            CancellationToken cancellationToken = default)
            => _inner.TryCreateAsync(envelope, cancellationToken);

        public Task SetAsync(
            OperationHandle envelope,
            CancellationToken cancellationToken = default)
            => _inner.SetAsync(envelope, cancellationToken);

        public async Task<OperationHandle?> GetAsync(
            string operationInstanceId,
            CancellationToken cancellationToken = default)
        {
            var existing = await _inner.GetAsync(operationInstanceId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            var now = DateTimeOffset.UtcNow;
            var migrated = new OperationHandle
            {
                OperationInstanceId = operationInstanceId,
                OperationId = "test.legacy-approved-proposal",
                CorrelationId = $"corr-test-{Guid.NewGuid():N}",
                AuditId = $"audit-test-{Guid.NewGuid():N}",
                Status = OperationHandleStatus.Accepted,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _ = await _inner.TryCreateAsync(migrated, cancellationToken);
            return await _inner.GetAsync(operationInstanceId, cancellationToken);
        }
    }

    private sealed class NullProposalNotifier : IProposalNotifier
    {
        public Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class PlanningOnlyExecutor(Honua.Core.Features.Guardrails.Domain.OperationClass operationClass)
        : LegacyExecutor
    {
        public Honua.Core.Features.Guardrails.Domain.OperationClass OperationClass => operationClass;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
            {
                Summary = $"{operationClass} proposal",
            });

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Planning-only actuator must not execute.");
    }
}
