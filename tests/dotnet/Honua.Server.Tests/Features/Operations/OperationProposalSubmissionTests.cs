// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class OperationProposalSubmissionTests
{
    [UnitTheory]
    [InlineData(PolicyDecisionKind.Deny, OperationHandleStatus.Denied)]
    [InlineData(PolicyDecisionKind.DryRunFirst, OperationHandleStatus.DryRunRequired)]
    public async Task ProposeAsync_PolicyRefusal_IsNotUpgraded(PolicyDecisionKind verdict, OperationHandleStatus expected)
    {
        var executor = Executor(); var bridge = Substitute.For<IOperationApprovalBridge>();
        var dispatcher = Dispatcher(executor, verdict, bridge);
        var result = await dispatcher.ProposeAsync(Request(), Context());
        result.Status.Should().Be(expected); result.ProposalId.Should().BeNull();
        await executor.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
        await bridge.DidNotReceiveWithAnyArgs().CreateProposalAsync(default!, default!, default!, default!, default);
    }

    [UnitTheory]
    [InlineData("dry-run")]
    [InlineData("proposal-proof")]
    [InlineData("plan-proof")]
    [InlineData("anonymous")]
    [InlineData("instance")]
    [InlineData("audit")]
    [InlineData("proposal")]
    public async Task ProposeAsync_UntrustedModeInputs_RefusesBeforeAcceptance(string invalid)
    {
        var executor = Executor(); var bridge = Substitute.For<IOperationApprovalBridge>();
        var instances = Substitute.For<IOperationInstanceStore>();
        var dispatcher = Dispatcher(executor, PolicyDecisionKind.Allow, bridge, instances);
        var request = Request() with { DryRun = invalid == "dry-run" };
        var context = Context() with
        {
            ApprovedProposalId = invalid == "proposal-proof" ? "forged" : null,
            ApprovedPlanHash = invalid == "plan-proof" ? "forged" : null,
            PrincipalId = invalid == "anonymous" ? null : "original-6662",
            OperationInstanceId = invalid == "instance" ? "forged" : null,
            AuditId = invalid == "audit" ? "forged" : null,
            ProposalId = invalid == "proposal" ? "forged" : null,
        };
        var action = () => dispatcher.ProposeAsync(request, context);
        await action.Should().ThrowAsync<ArgumentException>();
        await instances.DidNotReceiveWithAnyArgs().TryCreateAsync(default!, default);
        await executor.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default);
        await executor.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProposeAsync_MissingOrFailedBridge_NeverExecutes(bool failed)
    {
        var executor = Executor();
        var bridge = failed ? Substitute.For<IOperationApprovalBridge>() : null;
        if (bridge is not null)
            bridge.CreateProposalAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(
                new OperationApprovalBridgeResult { IsDurable = false, Reason = "storage unavailable" });
        var result = await Dispatcher(executor, PolicyDecisionKind.Allow, bridge).ProposeAsync(Request(), Context());
        result.Status.Should().Be(OperationHandleStatus.Failed); result.ProposalId.Should().BeNull();
        await executor.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTest]
    public async Task ProposeAsync_SecretAwareExecutorMissing_RefusesBeforeProposal()
    {
        var bridge = Substitute.For<IOperationApprovalBridge>();
        var dispatcher = new OperationDispatcher(Catalog(), [], new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var action = () => dispatcher.ProposeAsync(Request(), Context());
        await action.Should().ThrowAsync<OperationUnavailableException>();
        await bridge.DidNotReceiveWithAnyArgs().CreateProposalAsync(default!, default!, default!, default!, default);
    }

    [UnitTest]
    public async Task ProposeAsync_NarrowedScope_RefusesBeforeDescriptorOrAcceptance()
    {
        var executor = Executor(); var instances = Substitute.For<IOperationInstanceStore>();
        var catalog = Substitute.For<IOperationCatalog>();
        var dispatcher = new OperationDispatcher(catalog, [executor], new AllowAllPolicyDecisionPoint(), TimeProvider.System, instanceStore: instances);
        var result = await dispatcher.ProposeAsync(Request(), Context() with { ScopeGoverned = true, RecognizedScopes = ["honua.mcp.read"] });
        result.Status.Should().Be(OperationHandleStatus.Denied);
        await catalog.DidNotReceiveWithAnyArgs().GetDescriptorAsync(default!, default);
        await instances.DidNotReceiveWithAnyArgs().TryCreateAsync(default!, default);
        await executor.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [UnitTest]
    public async Task ProposeAsync_NongatedDescriptor_RefusesBeforeAcceptance()
    {
        var executor = Executor(); var instances = Substitute.For<IOperationInstanceStore>();
        var catalog = Substitute.For<IOperationCatalog>();
        catalog.GetDescriptorAsync(default!, default).ReturnsForAnyArgs(
            AdminAccessOperationCatalog.Descriptors.Single(d => d.OperationId == "admin.api-key.create") with { ApprovalModel = OperationApprovalModel.None });
        var dispatcher = new OperationDispatcher(catalog, [executor], new AllowAllPolicyDecisionPoint(), TimeProvider.System, instanceStore: instances);
        var action = () => dispatcher.ProposeAsync(Request(), Context());
        await action.Should().ThrowAsync<ArgumentException>();
        await instances.DidNotReceiveWithAnyArgs().TryCreateAsync(default!, default);
        await executor.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default);
    }

    [UnitTest]
    public async Task ProposeAsync_LostAcceptancePersistence_CannotCreateProposalOrExecute()
    {
        var executor = Executor(); var bridge = Substitute.For<IOperationApprovalBridge>();
        var instances = Substitute.For<IOperationInstanceStore>();
        instances.TryCreateAsync(default!, default).ReturnsForAnyArgs(_ => Task.FromException<bool>(new IOException("synthetic lost persistence")));
        var result = await Dispatcher(executor, PolicyDecisionKind.Allow, bridge, instances).ProposeAsync(Request(), Context());
        result.Status.Should().Be(OperationHandleStatus.Failed); result.ProposalId.Should().BeNull();
        await executor.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
        await bridge.DidNotReceiveWithAnyArgs().CreateProposalAsync(default!, default!, default!, default!, default);
    }

    [UnitTest]
    public async Task ProposeAsync_SameRawIdempotencyAsCompletedDirectMode_CreatesNewPendingProposal()
    {
        var executor = Executor();
        executor.SubmitAsync(default!, default!, default).ReturnsForAnyArgs(new OperationHandle
        {
            OperationId = "admin.api-key.create",
            OperationInstanceId = "placeholder",
            CorrelationId = "placeholder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = OperationHandleStatus.Completed,
        });
        var bridge = Substitute.For<IOperationApprovalBridge>();
        bridge.CreateProposalAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(
            new OperationApprovalBridgeResult { IsDurable = true, ProposalId = "proposal-new", AuditId = "audit-new" });
        var dispatcher = Dispatcher(executor, PolicyDecisionKind.Allow, bridge);
        var context = Context() with { IdempotencyKey = "same-raw-key" };
        var direct = await dispatcher.SubmitAsync(Request(), context);
        direct.Status.Should().Be(OperationHandleStatus.Completed);
        var proposed = await dispatcher.ProposeAsync(Request(), context);
        proposed.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        proposed.OperationInstanceId.Should().NotBe(direct.OperationInstanceId);
        proposed.ProposalId.Should().Be("proposal-new");
        await executor.Received(1).SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
        var changed = await dispatcher.ProposeAsync(Request() with { Parameters = new Dictionary<string, string?> { ["name"] = "changed" } }, context);
        changed.Status.Should().Be(OperationHandleStatus.Failed);
        await bridge.ReceivedWithAnyArgs(1).CreateProposalAsync(default!, default!, default!, default!, default);
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitAsync_BlankUnverifiedReplayId_DoesNotReplaceActuatorJoin(string unverified)
    {
        var executor = Executor();
        executor.SubmitAsync(default!, default!, default).ReturnsForAnyArgs(new OperationHandle
        {
            OperationId = "admin.api-key.create",
            OperationInstanceId = "placeholder",
            CorrelationId = "placeholder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = OperationHandleStatus.Completed,
            ProposalId = "actuator-owned-proposal",
        });
        var result = await Dispatcher(executor, PolicyDecisionKind.Allow, null).SubmitAsync(Request(),
            Context() with { ApprovedProposalId = unverified, ApprovedPlanHash = "not-verified" });
        result.Status.Should().Be(OperationHandleStatus.Completed);
        result.ProposalId.Should().Be("actuator-owned-proposal");
    }

    private static OperationRequest Request() => new() { OperationId = "admin.api-key.create", Parameters = new Dictionary<string, string?> { ["name"] = "synthetic" } };
    private static OperationPolicyContext Context() => new() { PrincipalId = "original-6662" };
    private static OperationCatalog Catalog() => new([new AdminAccessOperationDescriptorProvider()], TimeProvider.System);
    private static IOperationExecutor Executor()
    {
        var executor = Substitute.For<IOperationExecutor>(); executor.OperationId.Returns("admin.api-key.create");
        executor.ValidateAsync(default!, default).ReturnsForAnyArgs(new OperationValidation { IsValid = true, Status = "valid" });
        return executor;
    }
    private static OperationDispatcher Dispatcher(IOperationExecutor executor, PolicyDecisionKind decision, IOperationApprovalBridge? bridge, IOperationInstanceStore? instances = null)
    {
        var policy = Substitute.For<IOperationPolicyDecisionPoint>();
        policy.EvaluateAsync(default!, default!, default!, default).ReturnsForAnyArgs(new PolicyDecision { Kind = decision });
        return new(Catalog(), [executor], policy, TimeProvider.System, bridge, instances);
    }
}
