// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Security.Admission;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Update)]
public sealed class AiAdmissionControlOperationTests
{
    private const string Stop = "admin.ai.admission.stop";
    private static readonly AiAdmissionState State = new("deployment", new AiAdmissionPolicy(), Guid.NewGuid(), 1, 2, false, DateTimeOffset.UtcNow);

    [UnitTest]
    public async Task Prepare_DurableRequester_PinsExpectedStateAndUsesCanonicalAuditActor()
    {
        var store = Store();
        using var services = Services(store, Principal("requester"));
        var executor = new AiAdmissionControlExecutor(Stop, services);
        var context = Context(Principal("requester"));
        var prepared = await executor.PrepareAsync(Request(), context);
        prepared.Parameters[AiAdmissionControlOperation.ActorPin].Should().Be(context.PrincipalId);
        prepared.Parameters["expectedGeneration"].Should().Be(State.Generation.ToString("D"));
        var result = await executor.SubmitAsync(prepared, context);
        result.Status.Should().Be(OperationHandleStatus.Completed);
        await store.Received(1).ApplyControlAsync(Arg.Is<AiAdmissionControlCommand>(command =>
            command.ActorId == context.PrincipalId && command.ExpectedEpoch == State.Epoch && command.InvocationId == context.OperationInstanceId),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PrepareAndSubmit_ConcurrentRequestsOnOneScopedExecutor_KeepCommandsBoundToTheirRequest()
    {
        var store = Store();
        var principal = Principal("requester");
        using var services = Services(store, principal);
        var executor = new AiAdmissionControlExecutor(Stop, services);
        var firstContext = Context(principal) with { OperationInstanceId = "first", CorrelationId = "first-correlation" };
        var secondContext = Context(principal) with { OperationInstanceId = "second", CorrelationId = "second-correlation" };
        var prepared = await Task.WhenAll(executor.PrepareAsync(Request(), firstContext), executor.PrepareAsync(Request(), secondContext));

        var results = await Task.WhenAll(executor.SubmitAsync(prepared[0], firstContext), executor.SubmitAsync(prepared[1], secondContext));

        results.Should().OnlyContain(result => result.Status == OperationHandleStatus.Completed);
        await store.Received(1).ApplyControlAsync(Arg.Is<AiAdmissionControlCommand>(command =>
            command.InvocationId == "first" && command.CorrelationId == "first-correlation"), Arg.Any<CancellationToken>());
        await store.Received(1).ApplyControlAsync(Arg.Is<AiAdmissionControlCommand>(command =>
            command.InvocationId == "second" && command.CorrelationId == "second-correlation"), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Prepare_CallerActorPinOrWrongDeployment_RejectsBeforeReadingLedger()
    {
        var store = Store();
        using var services = Services(store, Principal("requester"));
        var executor = new AiAdmissionControlExecutor(Stop, services);
        var pin = Request(new Dictionary<string, string?> { [AiAdmissionControlOperation.ActorPin] = "claimed-actor" });
        var call = () => executor.PrepareAsync(pin, Context(Principal("requester")));
        await call.Should().ThrowAsync<ArgumentException>();
        var wrong = Request(new Dictionary<string, string?> { ["deploymentId"] = "another-deployment" });
        call = () => executor.PrepareAsync(wrong, Context(Principal("requester")));
        await call.Should().ThrowAsync<ArgumentException>();
        await store.DidNotReceiveWithAnyArgs().GetStateAsync(default!, default);
        await store.DidNotReceiveWithAnyArgs().ApplyControlAsync(default!, default);
    }

    [UnitTest]
    public async Task Prepare_DisplayNameOnlyOrAnonymousActor_DoesNotCreateDurableControl()
    {
        foreach (var principal in new[] { new ClaimsPrincipal(new ClaimsIdentity()),
                     new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "display-only")], "test")) })
        {
            var store = Store();
            using var services = Services(store, principal);
            var executor = new AiAdmissionControlExecutor(Stop, services);
            var call = () => executor.PrepareAsync(Request(), new OperationPolicyContext { PrincipalId = "claimed-name", OperationInstanceId = "instance", CorrelationId = "correlation" });
            await call.Should().ThrowAsync<InvalidOperationException>();
            await store.DidNotReceiveWithAnyArgs().GetStateAsync(default!, default);
        }
    }

    [UnitTest]
    public async Task Submit_ClaimedApprovalAndActorPinWithoutPreparation_Rejects()
    {
        var store = Store();
        using var services = Services(store, Principal("requester"));
        var executor = new AiAdmissionControlExecutor(Stop, services);
        var call = () => executor.SubmitAsync(Request(new Dictionary<string, string?> { [AiAdmissionControlOperation.ActorPin] = "claimed" }),
            Context(Principal("requester")) with { ApprovedProposalId = "claimed-proposal", ApprovedPlanHash = "claimed-hash" });
        await call.Should().ThrowAsync<InvalidOperationException>();
        await store.DidNotReceiveWithAnyArgs().ApplyControlAsync(default!, default);
    }

    [UnitTest]
    public async Task Prepare_ApprovedReplay_RevalidatesSealedOriginalRequesterNotCurrentApprover()
    {
        var store = Store();
        var requester = Principal("requester");
        var context = Context(requester);
        using var initial = Services(store, requester);
        var prepared = await new AiAdmissionControlExecutor(Stop, initial).PrepareAsync(Request(), context);
        var mapper = new AiAdmissionControlApprovalMapper(Stop);
        var mapped = mapper.Map(AiAdmissionControlOperation.Descriptor(Stop), prepared, context,
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
        var now = DateTimeOffset.UtcNow;
        var proposal = new OperationProposal
        {
            ProposalId = "proposal", OperationId = Stop, Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.Executing, RequestedBy = context.PrincipalId, Plan = mapped.Plan!,
            SealedPlanHash = OperationApprovalPlanSeal.Compute(mapped.Plan!),
            Audit = new OperationAuditInfo { OperationInstanceId = context.OperationInstanceId },
            CreatedAt = now, UpdatedAt = now
        };
        var proposals = Substitute.For<IOperationProposalStore>();
        proposals.GetAsync("proposal", Arg.Any<CancellationToken>()).Returns(proposal);
        using var replayServices = Services(store, Principal("approver"), proposals);
        var replayContext = context with { ApprovedProposalId = "proposal", ApprovedPlanHash = proposal.SealedPlanHash };
        var replay = mapper.MapReplay(mapped).Request;
        var executor = new AiAdmissionControlExecutor(Stop, replayServices);
        var accepted = await executor.PrepareAsync(replay, replayContext);
        await executor.SubmitAsync(accepted, replayContext);
        await store.Received(1).ApplyControlAsync(Arg.Is<AiAdmissionControlCommand>(command => command.ActorId == context.PrincipalId), Arg.Any<CancellationToken>());
        var changed = replay with { Parameters = new Dictionary<string, string?>(replay.Parameters) { [AiAdmissionControlOperation.ActorPin] = "forged-actor" } };
        var changedCall = () => new AiAdmissionControlExecutor(Stop, replayServices).PrepareAsync(changed, replayContext);
        await changedCall.Should().ThrowAsync<InvalidOperationException>();
        proposals.GetAsync("proposal", Arg.Any<CancellationToken>()).Returns(proposal with { Status = OperationProposalStatus.AwaitingApproval });
        var notExecuting = () => new AiAdmissionControlExecutor(Stop, replayServices).PrepareAsync(replay, replayContext);
        await notExecuting.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTheory]
    [InlineData("admin.ai.admission.initialize")]
    [InlineData("admin.ai.admission.policy-update")]
    [InlineData("admin.ai.admission.stop")]
    [InlineData("admin.ai.admission.resume")]
    public void ScopeAuthority_ExactlyFourControlIds_RequireUpdate(string id)
    {
        var request = new OperationRequest { OperationId = id };
        OperationScopeMapping.TryResolve(request, out var operation).Should().BeTrue();
        operation.Should().Be(OperatorOperation.Update);
        OperationScopeMapping.ExceedsScopeAuthority(request, new OperationPolicyContext
        { ScopeGoverned = true, RecognizedScopes = [OperatorScopeCatalog.Read] }).Should().BeTrue();
        OperationScopeMapping.ExceedsScopeAuthority(request, new OperationPolicyContext
        { ScopeGoverned = true, RecognizedScopes = [OperatorScopeCatalog.Update] }).Should().BeFalse();
        OperationsServiceCollectionExtensions.RedisOffDirectOperationIds.Should().Contain(id);
    }

    private static OperationRequest Request(IReadOnlyDictionary<string, string?>? parameters = null)
        => new() { OperationId = Stop, Parameters = parameters ?? new Dictionary<string, string?>() };

    private static ClaimsPrincipal Principal(string subject) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Role, "admin")], "test"));

    private static OperationPolicyContext Context(ClaimsPrincipal principal) => new()
    { PrincipalId = JobOwnershipSecurity.ResolveOwner(principal), OperationInstanceId = "instance", CorrelationId = "correlation" };

    private static IAiExecutionAdmissionStore Store()
    {
        var store = Substitute.For<IAiExecutionAdmissionStore>();
        store.GetStateAsync("deployment", Arg.Any<CancellationToken>()).Returns(State);
        store.ApplyControlAsync(Arg.Any<AiAdmissionControlCommand>(), Arg.Any<CancellationToken>())
            .Returns(new AiAdmissionControlResult("stopped", State with { Stopped = true, Epoch = State.Epoch + 1 }, true));
        return store;
    }

    private static ServiceProvider Services(IAiExecutionAdmissionStore store, ClaimsPrincipal principal, IOperationProposalStore? proposals = null)
    {
        var services = new ServiceCollection();
        services.Configure<AiExecutionAdmissionOptions>(options => options.DeploymentId = "deployment");
        services.AddSingleton(store);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });
        if (proposals is not null) services.AddSingleton(proposals);
        return services.BuildServiceProvider();
    }
}
