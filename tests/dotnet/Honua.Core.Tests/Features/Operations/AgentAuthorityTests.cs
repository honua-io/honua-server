// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using NSubstitute;

namespace Honua.Core.Tests.OperationScopes;

public sealed class AgentAuthorityTests
{
    private static readonly OperationPolicyContext Agent = new()
    {
        PrincipalId = "agent-a", TenantId = "tenant-a", Roles = ["agent", "admin"],
    };

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("tenant")]
    [InlineData("principal")]
    [InlineData("operation")]
    [InlineData("blast")]
    [InlineData("effect")]
    [InlineData("invalid")]
    public async Task Agent_CannotExceedAnyCeilingDimension(string restriction)
    {
        var ceiling = Ceiling();
        ceiling = restriction switch
        {
            "disabled" => ceiling with { Enabled = false },
            "tenant" => ceiling with { TenantId = "other" },
            "principal" => ceiling with { PrincipalId = "other" },
            "operation" => ceiling with { AllowedOperationIds = ["other"] },
            "blast" => ceiling with { MaximumBlastRadius = OperationBlastRadiusClass.ResourceScope },
            "effect" => ceiling with { MaximumSideEffect = OperationSideEffectClass.ReadOnly },
            "invalid" => ceiling with { AllowedOperationIds = ["*"] },
            _ => ceiling,
        };
        var store = Store(restriction == "missing" ? null : ceiling);
        Assert.False(await AgentAuthorityPolicy.AllowsAsync(store, Descriptor(), Agent, default));
    }

    [Fact]
    public async Task MissingStore_DeniesAgentAndPreservesHumanPolicyPath()
    {
        Assert.False(await AgentAuthorityPolicy.AllowsAsync(null, Descriptor(), Agent, default));
        Assert.True(await AgentAuthorityPolicy.AllowsAsync(null, Descriptor(), Agent with { Roles = ["admin"] }, default));
    }

    [Fact]
    public async Task Ceiling_IsReadAgainAfterRevocationAndForApprovedReplay()
    {
        var store = Store(Ceiling());
        var replay = Agent with { ApprovedProposalId = "approved", ApprovedPlanHash = "plan" };
        Assert.True(await AgentAuthorityPolicy.AllowsAsync(store, Descriptor(), replay, default));
        store.GetAsync(Agent.TenantId, Agent.PrincipalId!, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentAuthorityCeiling?>(null));
        Assert.False(await AgentAuthorityPolicy.AllowsAsync(store, Descriptor(), replay, default));
    }

    [Theory]
    [InlineData(PolicyDecisionKind.Deny)]
    [InlineData(PolicyDecisionKind.RequireApproval)]
    public async Task Dispatcher_CeilingCannotGrantExecutionOrBypassExistingPolicy(PolicyDecisionKind expected)
    {
        var catalog = Substitute.For<IOperationCatalog>();
        catalog.GetDescriptorAsync("service.publish", Arg.Any<CancellationToken>()).Returns(Descriptor());
        var executor = Substitute.For<IOperationExecutor>();
        executor.OperationId.Returns("service.publish");
        executor.ValidateAsync(Arg.Any<OperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationValidation { IsValid = true, Status = "valid" });
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval, Reason = "Human approval required" });
        var dispatcher = new OperationDispatcher(catalog, [executor], policy, TimeProvider.System,
            agentAuthorityStore: Store(expected == PolicyDecisionKind.RequireApproval ? Ceiling() : null));

        var result = await dispatcher.SubmitAsync(new OperationRequest { OperationId = "service.publish" }, Agent);

        Assert.Equal(expected, result.PolicyDecision);
        await executor.DidNotReceive().SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_RevocationDuringPolicyEvaluationPreventsActuation()
    {
        var store = Store(Ceiling());
        var catalog = Substitute.For<IOperationCatalog>();
        catalog.GetDescriptorAsync("service.publish", Arg.Any<CancellationToken>()).Returns(Descriptor());
        var executor = Substitute.For<IOperationExecutor>();
        executor.OperationId.Returns("service.publish");
        executor.ValidateAsync(Arg.Any<OperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationValidation { IsValid = true, Status = "valid" });
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                store.GetAsync(Agent.TenantId, Agent.PrincipalId!, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AgentAuthorityCeiling?>(null));
                return PolicyDecision.Allowed;
            });
        var dispatcher = new OperationDispatcher(catalog, [executor], policy, TimeProvider.System,
            agentAuthorityStore: store);

        var result = await dispatcher.SubmitAsync(new OperationRequest { OperationId = "service.publish" }, Agent);

        Assert.Equal(OperationHandleStatus.Denied, result.Status);
        Assert.Contains("revoked", result.Reason);
        await executor.DidNotReceive().SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
    }

    private static IAgentAuthorityStore Store(AgentAuthorityCeiling? ceiling)
    {
        var store = Substitute.For<IAgentAuthorityStore>();
        store.GetAsync(Agent.TenantId, Agent.PrincipalId!, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ceiling));
        return store;
    }

    private static AgentAuthorityCeiling Ceiling() => new()
    {
        TenantId = Agent.TenantId, PrincipalId = Agent.PrincipalId!, AllowedOperationIds = ["service.publish"],
        MaximumBlastRadius = OperationBlastRadiusClass.ServiceScope,
        MaximumSideEffect = OperationSideEffectClass.CreatesMetadata,
    };

    private static OperationDescriptor Descriptor() => new()
    {
        OperationId = "service.publish", ProviderId = "test", Title = "Publish", Description = "Publish service",
        Category = "Publishing", ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.CreatesMetadata,
            Determinism = OperationDeterminism.Deterministic,
        },
    };
}
