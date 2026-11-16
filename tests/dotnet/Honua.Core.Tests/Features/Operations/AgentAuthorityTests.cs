// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Core.Tests.OperationScopes;

public sealed class AgentAuthorityTests
{
    private static readonly OperationPolicyContext Agent = new()
    {
        PrincipalId = "agent-a",
        TenantId = "tenant-a",
        Roles = ["agent", "admin"],
    };

    [UnitTheory]
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
        Assert.Equal(AgentAuthorityVerdict.Denied, await Verdict(Store(ceiling), Descriptor(), Agent));
    }

    [UnitTest]
    public async Task NoCeiling_AgentChangeNeedsApprovalAndReadsFollowPolicy()
    {
        var readOnly = Descriptor() with { Policy = Descriptor().Policy with { SideEffectClass = OperationSideEffectClass.ReadOnly } };
        Assert.Equal(AgentAuthorityVerdict.ApprovalRequired, await Verdict(Store(null), Descriptor(), Agent));
        Assert.Equal(AgentAuthorityVerdict.ApprovalRequired, await Verdict(null, Descriptor(), Agent));
        Assert.Equal(AgentAuthorityVerdict.Unrestricted, await Verdict(Store(null), readOnly, Agent));
        Assert.Equal(AgentAuthorityVerdict.Denied, await Verdict(Store(Ceiling() with { Enabled = false }), readOnly, Agent));
        Assert.Equal(AgentAuthorityVerdict.Denied, await Verdict(Store(null), readOnly, Agent with { PrincipalId = " " }));
        Assert.Equal(AgentAuthorityVerdict.Unrestricted, await Verdict(null, Descriptor(), Agent with { Roles = ["admin"] }));
    }

    [UnitTest]
    public async Task Ceiling_IsReadAgainAfterRevocationAndForApprovedReplay()
    {
        var store = Store(Ceiling());
        var replay = Agent with { ApprovedProposalId = "approved", ApprovedPlanHash = "plan" };
        Assert.Equal(AgentAuthorityVerdict.Unrestricted, await Verdict(store, Descriptor(), replay));
        store.GetAsync(Agent.TenantId, Agent.PrincipalId!, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentAuthorityCeiling?>(Ceiling() with { Enabled = false }));
        Assert.Equal(AgentAuthorityVerdict.Denied, await Verdict(store, Descriptor(), replay));
    }

    [UnitTest]
    public async Task Dispatcher_NoCeiling_AgentChangeBecomesProposal()
    {
        var executor = Executor();
        var dispatcher = new OperationDispatcher(Catalog(), [executor], Policy(PolicyDecision.Allowed), TimeProvider.System,
            guards: new OperationActuationGuards { AgentAuthorityStore = Store(null) });

        var result = await dispatcher.SubmitAsync(new OperationRequest { OperationId = "service.publish" }, Agent);

        Assert.Equal(PolicyDecisionKind.RequireApproval, result.PolicyDecision);
        await executor.DidNotReceive().SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(PolicyDecisionKind.Deny)]
    [InlineData(PolicyDecisionKind.RequireApproval)]
    public async Task Dispatcher_CeilingCannotGrantExecutionOrBypassExistingPolicy(PolicyDecisionKind expected)
    {
        var catalog = Catalog();
        var executor = Executor();
        var policy = Policy(new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval, Reason = "Human approval required" });
        var dispatcher = new OperationDispatcher(catalog, [executor], policy, TimeProvider.System,
            guards: new OperationActuationGuards { AgentAuthorityStore = Store(expected == PolicyDecisionKind.RequireApproval ? Ceiling() : Ceiling() with { Enabled = false }) });

        var result = await dispatcher.SubmitAsync(new OperationRequest { OperationId = "service.publish" }, Agent);

        Assert.Equal(expected, result.PolicyDecision);
        await executor.DidNotReceive().SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Dispatcher_RevocationDuringPolicyEvaluationPreventsActuation()
    {
        var store = Store(Ceiling());
        var catalog = Catalog();
        var executor = Executor();
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                store.GetAsync(Agent.TenantId, Agent.PrincipalId!, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AgentAuthorityCeiling?>(Ceiling() with { Enabled = false }));
                return PolicyDecision.Allowed;
            });
        var dispatcher = new OperationDispatcher(catalog, [executor], policy, TimeProvider.System,
            guards: new OperationActuationGuards { AgentAuthorityStore = store });

        var result = await dispatcher.SubmitAsync(new OperationRequest { OperationId = "service.publish" }, Agent);

        Assert.Equal(OperationHandleStatus.Denied, result.Status);
        Assert.Contains("revoked", result.Reason);
        await executor.DidNotReceive().SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Dispatcher_CancellationDuringFinalCeilingRead_DoesNotReportActuation()
    {
        var store = Store(Ceiling());
        var catalog = Catalog();
        var executor = Executor();
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                store.GetAsync(Agent.TenantId, Agent.PrincipalId!, Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<AgentAuthorityCeiling?>(new OperationCanceledException()));
                return PolicyDecision.Allowed;
            });
        var instances = new VolatileOperationInstanceStore();
        var dispatcher = new OperationDispatcher(catalog, [executor], policy, TimeProvider.System,
            instanceStore: instances, guards: new OperationActuationGuards { AgentAuthorityStore = store });
        var context = Agent with { OperationInstanceId = Guid.NewGuid().ToString("N") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.SubmitAsync(
            new OperationRequest { OperationId = "service.publish" }, context));

        Assert.Equal(OperationHandleStatus.Cancelled, (await instances.GetAsync(context.OperationInstanceId, default))!.Status);
        await executor.DidNotReceive().SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
    }

    private static Task<AgentAuthorityVerdict> Verdict(
        IAgentAuthorityStore? store, IOperationDescriptor descriptor, OperationPolicyContext context)
        => AgentAuthorityPolicy.EvaluateAsync(store, descriptor, context, default);

    private static IOperationCatalog Catalog()
    {
        var catalog = Substitute.For<IOperationCatalog>();
        catalog.GetDescriptorAsync("service.publish", Arg.Any<CancellationToken>()).Returns(Descriptor());
        return catalog;
    }

    private static IOperationExecutor Executor()
    {
        var executor = Substitute.For<IOperationExecutor>();
        executor.OperationId.Returns("service.publish");
        executor.ValidateAsync(Arg.Any<OperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OperationValidation { IsValid = true, Status = "valid" });
        return executor;
    }

    private static IOperationPolicyDecisionPoint Policy(PolicyDecision decision)
    {
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(decision);
        return policy;
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
        TenantId = Agent.TenantId,
        PrincipalId = Agent.PrincipalId!,
        AllowedOperationIds = ["service.publish"],
        MaximumBlastRadius = OperationBlastRadiusClass.ServiceScope,
        MaximumSideEffect = OperationSideEffectClass.CreatesMetadata,
    };

    private static OperationDescriptor Descriptor() => new()
    {
        OperationId = "service.publish",
        ProviderId = "test",
        Title = "Publish",
        Description = "Publish service",
        Category = "Publishing",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.CreatesMetadata,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = false,
        },
    };
}
