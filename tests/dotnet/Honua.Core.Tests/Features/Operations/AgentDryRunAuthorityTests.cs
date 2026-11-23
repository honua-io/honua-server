// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Core.Tests.OperationScopes;

// An agent whose changes need approval may still preview them: the final ceiling read before
// actuation treats a dry run like submission does, and a revocation still stops it.
public sealed class AgentDryRunAuthorityTests
{
    private static readonly OperationPolicyContext Agent = new()
    {
        PrincipalId = "agent-a",
        TenantId = "tenant-a",
        Roles = ["agent", "admin"],
    };

    [UnitTest]
    public async Task Dispatcher_ApprovalRequiredAgentDryRun_RunsThePreview()
    {
        var executor = DryRunExecutor();
        var dispatcher = Dispatcher(executor, new CeilingStore(), Allow());

        var result = await dispatcher.SubmitAsync(DryRun(), Agent);

        Assert.Equal(OperationHandleStatus.Completed, result.Status);
        await executor.Received(1).SubmitAsync(Arg.Is<OperationRequest>(request => request.DryRun),
            Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Dispatcher_AgentDryRun_RevokedDuringPolicyEvaluation_IsStillRefused()
    {
        var store = new CeilingStore { Ceiling = AllowingCeiling() };
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                store.Ceiling = AllowingCeiling() with { Enabled = false };
                return PolicyDecision.Allowed;
            });
        var executor = DryRunExecutor();

        var result = await Dispatcher(executor, store, policy).SubmitAsync(DryRun(), Agent);

        Assert.Equal(OperationHandleStatus.Denied, result.Status);
        await executor.DidNotReceive().SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(),
            Arg.Any<CancellationToken>());
    }

    private static OperationRequest DryRun() => new() { OperationId = "service.publish", DryRun = true };

    private static OperationDispatcher Dispatcher(
        IOperationExecutor executor, IAgentAuthorityStore store, IOperationPolicyDecisionPoint policy)
    {
        var catalog = Substitute.For<IOperationCatalog>();
        catalog.GetDescriptorAsync("service.publish", Arg.Any<CancellationToken>()).Returns(Descriptor());
        return new OperationDispatcher(catalog, [executor], policy, TimeProvider.System,
            guards: new OperationActuationGuards { AgentAuthorityStore = store });
    }

    private static IOperationDryRunExecutor DryRunExecutor()
    {
        var executor = Substitute.For<IOperationDryRunExecutor>();
        executor.OperationId.Returns("service.publish");
        executor.ExecutesDryRun.Returns(true);
        executor.ValidateAsync(Arg.Any<OperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationValidation { IsValid = true, Status = "valid" });
        executor.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => new OperationHandle
            {
                OperationInstanceId = "opinst-preview",
                OperationId = "service.publish",
                CorrelationId = "corr-preview",
                Status = OperationHandleStatus.Completed,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
        return executor;
    }

    private static IOperationPolicyDecisionPoint Allow()
    {
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(PolicyDecision.Allowed);
        return policy;
    }

    // An agent with no ceiling may change things only through approval; one with this ceiling may publish.
    private static AgentAuthorityCeiling AllowingCeiling() => new()
    {
        TenantId = Agent.TenantId,
        PrincipalId = Agent.PrincipalId!,
        AllowedOperationIds = ["service.publish"],
        MaximumBlastRadius = OperationBlastRadiusClass.ServiceScope,
        MaximumSideEffect = OperationSideEffectClass.CreatesMetadata,
    };

    private static OperationDescriptor Descriptor()
    {
        var policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.CreatesMetadata,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = true,
        };
        return new OperationDescriptor
        {
            OperationId = "service.publish",
            ProviderId = "test",
            Title = "Publish",
            Description = "Publish service",
            Category = "Publishing",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = OperationApprovalModel.OperatorGate,
            Policy = policy,
        };
    }

    private sealed class CeilingStore : IAgentAuthorityStore
    {
        public AgentAuthorityCeiling? Ceiling { get; set; }

        public Task<AgentAuthorityCeiling?> GetAsync(
            string? tenantId, string principalId, CancellationToken cancellationToken = default)
            => Task.FromResult(Ceiling);

        public Task SetAsync(AgentAuthorityCeiling ceiling, CancellationToken cancellationToken = default)
        {
            Ceiling = ceiling;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string? tenantId, string principalId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
