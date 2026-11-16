// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Observability.Services;
using Honua.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using IAgentAuthorityStore = Honua.Core.Features.Operations.Abstractions.IAgentAuthorityStore;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class OperationGatewayAutonomyTests
{
    [Fact]
    public async Task OrdinaryRoute_ServiceNameAndAutonomyContext_DoNotGrantRemediationAuthority()
    {
        var evaluator = AutoApplyingEvaluator();
        var executor = new RecordingExecutor();
        var sut = BuildGateway(new MultiProposalStore(), evaluator, executor, RecordingConvergence.Converged());

        var result = await sut.RouteAsync(Request() with { RequestedBy = "service:ops-findings", RequestedByAgent = null });

        result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        evaluator.RouteCount.Should().Be(0);
        executor.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task Remediation_RealReservation_ConcurrentRetriesActuateOnce()
    {
        var policyStore = await PolicyStoreForGuardAsync();
        var evaluator = RealEvaluator(policyStore);
        var executor = new RecordingExecutor { YieldOnExecute = true };
        var audit = new RecordingAuditLog();
        var sut = BuildGateway(new MultiProposalStore(), evaluator, executor, RecordingConvergence.Converged(), audit);

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => sut.RouteRemediationAsync(Request() with { OperationInstanceId = null })));

        results.Should().ContainSingle(result => result.Outcome == OperationGatewayOutcome.Executed);
        executor.ExecuteCount.Should().Be(1);
        audit.Events.Where(item => item.Action == "operation.auto_applied").Should().ContainSingle();
        audit.Events.Should().OnlyContain(item => item.Actor == "service:ops-findings" && item.ActorAgent == null);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("config-kill")]
    [InlineData("store-kill")]
    [InlineData("unsafe-action")]
    [InlineData("unsafe-catalog")]
    [InlineData("blast-radius")]
    [InlineData("budget")]
    public async Task Remediation_RealPolicyGuardDenies_NeverActuates(string guard)
    {
        var policyStore = await PolicyStoreForGuardAsync(guard);

        var evaluator = RealEvaluator(policyStore, new OpsAutonomyOptions
        {
            Enabled = guard != "disabled",
            KillSwitchEnabled = guard == "config-kill",
        }, eligibility: guard == "unsafe-catalog" ? CatalogEligibility.Unsafe : CatalogEligibility.AutoSafe);
        var executor = new RecordingExecutor();
        var sut = BuildGateway(new MultiProposalStore(), evaluator, executor, RecordingConvergence.Converged());
        var request = Request();
        request = request with
        {
            AutonomyContext = request.AutonomyContext! with
            {
                ActionMarkedAutoSafe = guard != "unsafe-action",
                BlastRadius = guard == "blast-radius" ? int.MaxValue : 1,
            },
        };

        var result = await sut.RouteRemediationAsync(request);

        result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        executor.ExecuteCount.Should().Be(0);
    }

    private static OpsAutonomyEvaluator RealEvaluator(
        InMemoryOpsAutonomyPolicyStore store,
        OpsAutonomyOptions? options = null,
        CatalogEligibility eligibility = CatalogEligibility.AutoSafe)
    {
        // Synthetic eligibility only: the shipped allowlist remains empty.
        var catalog = Substitute.For<IOpsActionSafetyCatalog>();
        catalog.IsAutoSafe(Request().Kind, RedriveAction).Returns(eligibility == CatalogEligibility.AutoSafe);
        var monitor = Substitute.For<IOptionsMonitor<OpsAutonomyOptions>>();
        monitor.CurrentValue.Returns(options ?? new OpsAutonomyOptions());
        return new OpsAutonomyEvaluator(monitor, store, catalog);
    }

    private enum CatalogEligibility
    {
        Unsafe,
        AutoSafe,
    }

    private static async Task<InMemoryOpsAutonomyPolicyStore> PolicyStoreForGuardAsync(string? guard = null)
    {
        var policyStore = new InMemoryOpsAutonomyPolicyStore();
        await policyStore.SetPolicyAsync(new OpsAutonomyPolicy
        {
            Rule = Rule,
            Mode = OpsAutonomyMode.AutoApply,
            MaxAutoActionsPerWindow = 1,
            Window = TimeSpan.FromHours(1),
            MaxBlastRadius = 10,
        }, "test");
        if (guard == "store-kill")
        {
            await policyStore.SetSettingsAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, "test");
        }

        if (guard == "budget")
        {
            await policyStore.TryReserveAutoActionAsync(new OpsAutonomyReservationRequest
            {
                Rule = Rule,
                FindingId = "other-finding",
                OperationClass = Request().Kind,
                ActionDiscriminator = RedriveAction,
                BlastRadius = 1,
                MaxAutoActionsPerWindow = 1,
                Window = TimeSpan.FromHours(1),
            });
        }

        return policyStore;
    }

    private static async Task AssertReplayRetainsServiceAuthorityAsync(
        OperationGateway gateway,
        OperationProposal proposal,
        RecordingExecutor executor)
    {
        executor.LastRequest!.RequestedBy.Should().Be("service:ops-findings");
        executor.LastRequest.RequestedByAgent.Should().BeNull();
        executor.LastRequest.IdempotencyKey.Should().Be(proposal.Audit.IdempotencyKey);
        proposal.Audit.IdempotencyKey.Should().StartWith($"{FindingId}:batch:");
        executor.LastRequest.TenantId.Should().Be(proposal.TenantId);
        var repeatedApproval = () => gateway.ApplyApprovedProposalAsync(proposal.ProposalId,
            new OperationProposalApprovalContext { ApprovedBy = "human-approver", TenantId = string.Empty });
        await repeatedApproval.Should().ThrowAsync<InvalidOperationException>();
        executor.ExecuteCount.Should().Be(1);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("ceiling-remediation")]
    public async Task RouteAsync_AuthenticatedAgentClaimsServiceIdentity_RequiresApproval(string invocation)
    {
        using var requestServices = new ServiceCollection().BuildServiceProvider();
        var fixture = AuthenticatedAgentGateway(invocation, requestServices);
        var forged = Request() with { RequestedBy = "service:ops-findings", RequestedByAgent = null };

        // Accidental HTTP reuse of the internal entry point cannot acquire service authority.
        var result = invocation == "ceiling-remediation"
            ? await fixture.Gateway.RouteRemediationAsync(forged)
            : await fixture.Gateway.RouteAsync(forged);

        result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        fixture.Executor.ExecuteCount.Should().Be(0);
        fixture.Evaluator.RouteCount.Should().Be(0);
        var proposal = await fixture.Store.GetAsync(result.ProposalId!);
        proposal!.RequestedByAgent.Should().Be("test:subject:-:service%3Aops-findings");
        proposal.RequestedBy.Should().Be(proposal.RequestedByAgent);
    }

    private sealed record AgentGatewayFixture
    {
        public required OperationGateway Gateway { get; init; }
        public required MultiProposalStore Store { get; init; }
        public required RecordingExecutor Executor { get; init; }
        public required RecordingAutonomyEvaluator Evaluator { get; init; }
    }

    private static AgentGatewayFixture AuthenticatedAgentGateway(string invocation, IServiceProvider requestServices)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "service:ops-findings"),
                new Claim(ClaimTypes.Role, "agent")], "test")),
            RequestServices = requestServices,
        };
        var ceiling = AgentCeiling(invocation);
        var store = new MultiProposalStore();
        var executor = new RecordingExecutor();
        var evaluator = AutoApplyingEvaluator();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.AdminConfigChange).Returns(RequiresApprovalDecision());
        ladder.Resolve(OperationClass.AdminConfigChange, RedriveAction).Returns(RequiresApprovalDecision());
        var gateway = CanonicalOperationGatewayTestComposition.Build(store, ladder, [executor], services =>
            services.AddSingleton<Honua.Core.Features.AuditLog.Abstractions.IAuditLog>(new RecordingAuditLog())
                .AddSingleton<IOpsAutonomyEvaluator>(evaluator)
                .AddSingleton<IAutonomousOperationConvergence>(RecordingConvergence.Converged())
                .AddSingleton(ceiling)
                .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context }));
        return new AgentGatewayFixture { Gateway = gateway, Store = store, Executor = executor, Evaluator = evaluator };
    }

    private static IAgentAuthorityStore AgentCeiling(string invocation)
    {
        var ceiling = Substitute.For<IAgentAuthorityStore>();
        if (invocation == "ceiling-remediation")
        {
            ceiling.GetAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => new AgentAuthorityCeiling
                {
                    PrincipalId = call.ArgAt<string>(1),
                    AllowedOperationIds = ["control-plane.admin-config-change"],
                    MaximumBlastRadius = OperationBlastRadiusClass.DeploymentScope,
                    MaximumSideEffect = OperationSideEffectClass.DestroysState,
                });
        }

        return ceiling;
    }
}
