// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Observability.Services;
using Honua.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class OpsAutonomyEvaluatorTests
{
    private const string Rule = "alert-dispatch-backlog";
    private const string FindingId = "alert-dispatch-backlog-test";
    private const string RedriveAction = "alerts.redrive_dead_letters";

    [Fact]
    public async Task EvaluateRoute_AutoApplyPolicyForAutoSafeAction_ReservesAndReturnsDirectExecute()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(AutoApplyPolicy(), changedBy: "test");
        var sut = CreateEvaluator(store);

        var decision = await sut.EvaluateRouteAsync(Request(), RequiresApproval(), RedriveAction);

        decision.ShouldAutoApply.Should().BeTrue();
        decision.Decision.Should().NotBeNull();
        decision.Decision!.Tier.Should().Be(GuardrailTier.DirectExecute);
        decision.ReservationId.Should().NotBeNullOrWhiteSpace();

        await sut.RecordAutoActionOutcomeAsync(
            decision,
            OpsAutonomyActionOutcome.Succeeded,
            operationId: "op-1");
        var snapshot = await store.GetPolicyAsync(Rule);
        snapshot.Should().NotBeNull();
        var listed = await store.ListPoliciesAsync();
        listed.Single().TrackRecord.AutoApplied.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateRoute_KillSwitchEnabled_ReturnsProposeOnly()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(AutoApplyPolicy(), changedBy: "test");
        await store.SetSettingsAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, changedBy: "test");
        var sut = CreateEvaluator(store);

        var decision = await sut.EvaluateRouteAsync(Request(), RequiresApproval(), RedriveAction);

        decision.ShouldAutoApply.Should().BeFalse();
        decision.Reason.Should().Be("store-kill-switch");
    }

    [Fact]
    public async Task EvaluateRoute_NoDurablePolicyStore_ReturnsProposeOnly()
    {
        var sut = new OpsAutonomyEvaluator(
            new StaticOptionsMonitor<OpsAutonomyOptions>(new OpsAutonomyOptions
            {
                Rules = new Dictionary<string, OpsAutonomyRuleOptions>(StringComparer.Ordinal)
                {
                    [Rule] = new() { Mode = nameof(OpsAutonomyMode.AutoApply) },
                },
            }),
            store: null,
            new TestActionSafetyCatalog(new HashSet<string>(StringComparer.Ordinal) { RedriveAction }));

        var decision = await sut.EvaluateRouteAsync(Request(), RequiresApproval(), RedriveAction);

        decision.ShouldAutoApply.Should().BeFalse();
        decision.Reason.Should().Be("policy-store-unavailable");
    }

    [Fact]
    public async Task EvaluateRoute_FreshStore_UsesSameConfigBackedEffectivePolicyAsOperatorProjection()
    {
        var options = new OpsAutonomyOptions
        {
            DefaultMaxAutoActionsPerWindow = 8,
            DefaultWindowSeconds = 1200,
            DefaultMaxBlastRadius = 9,
            Rules = new Dictionary<string, OpsAutonomyRuleOptions>(StringComparer.Ordinal)
            {
                [Rule] = new()
                {
                    Mode = nameof(OpsAutonomyMode.AutoApply),
                    MaxAutoActionsPerWindow = 3,
                    WindowSeconds = 900,
                    MaxBlastRadius = 2,
                },
            },
        };
        var store = new InMemoryOpsAutonomyPolicyStore();
        var sut = new OpsAutonomyEvaluator(
            new StaticOptionsMonitor<OpsAutonomyOptions>(options),
            store,
            new TestActionSafetyCatalog(new HashSet<string>(StringComparer.Ordinal) { RedriveAction }));

        var decision = await sut.EvaluateRouteAsync(Request(), RequiresApproval(), RedriveAction);
        var projected = OpsAutonomyPolicyDefaults.Resolve(Rule, options, persisted: null);

        decision.ShouldAutoApply.Should().BeTrue();
        decision.Policy.Should().Be(projected);
        projected.Mode.Should().Be(OpsAutonomyMode.AutoApply);
        projected.MaxAutoActionsPerWindow.Should().Be(3);
        projected.Window.Should().Be(TimeSpan.FromSeconds(900));
        projected.MaxBlastRadius.Should().Be(2);
    }

    [Fact]
    public async Task EvaluateRoute_ConfigChangesBeforePersistence_UsesLatestEffectivePolicy()
    {
        var options = new StaticOptionsMonitor<OpsAutonomyOptions>(new OpsAutonomyOptions());
        var store = new InMemoryOpsAutonomyPolicyStore();
        var sut = new OpsAutonomyEvaluator(
            options,
            store,
            new TestActionSafetyCatalog(new HashSet<string>(StringComparer.Ordinal) { RedriveAction }));

        var before = await sut.EvaluateRouteAsync(Request(), RequiresApproval(), RedriveAction);
        options.Set(new OpsAutonomyOptions
        {
            Rules = new Dictionary<string, OpsAutonomyRuleOptions>(StringComparer.Ordinal)
            {
                [Rule] = new() { Mode = nameof(OpsAutonomyMode.AutoApply) },
            },
        });
        var after = await sut.EvaluateRouteAsync(Request(), RequiresApproval(), RedriveAction);

        before.ShouldAutoApply.Should().BeFalse();
        before.Policy!.Mode.Should().Be(OpsAutonomyMode.ProposeOnly);
        after.ShouldAutoApply.Should().BeTrue();
        after.Policy!.Mode.Should().Be(OpsAutonomyMode.AutoApply);
        (await store.GetPolicyAsync(Rule)).Should().BeNull("configuration-backed policy remains non-persisted");
    }

    [Fact]
    public async Task EvaluateRoute_RateCapExceeded_ReturnsProposeOnly()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(AutoApplyPolicy(maxActions: 1), changedBy: "test");
        var sut = CreateEvaluator(store);

        var first = await sut.EvaluateRouteAsync(Request(findingId: "finding-a"), RequiresApproval(), RedriveAction);
        var second = await sut.EvaluateRouteAsync(Request(findingId: "finding-b"), RequiresApproval(), RedriveAction);

        first.ShouldAutoApply.Should().BeTrue();
        second.ShouldAutoApply.Should().BeFalse();
        second.Reason.Should().Be("rate-limit-exceeded");
    }

    [Fact]
    public async Task EvaluateRoute_BlastRadiusExceeded_ReturnsProposeOnly()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(AutoApplyPolicy(maxBlastRadius: 1), changedBy: "test");
        var sut = CreateEvaluator(store);

        var decision = await sut.EvaluateRouteAsync(
            Request(blastRadius: 2),
            RequiresApproval(),
            RedriveAction);

        decision.ShouldAutoApply.Should().BeFalse();
        decision.Reason.Should().Be("blast-radius-exceeded");
    }

    [Fact]
    public async Task EvaluateRoute_ActionNotMarkedAutoSafe_ReturnsProposeOnly()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(AutoApplyPolicy(), changedBy: "test");
        var sut = CreateEvaluator(store);

        var decision = await sut.EvaluateRouteAsync(
            Request(actionMarkedAutoSafe: false),
            RequiresApproval(),
            RedriveAction);

        decision.ShouldAutoApply.Should().BeFalse();
        decision.Reason.Should().Be("finding-action-not-auto-safe");
    }

    [Fact]
    public async Task EvaluateRoute_ActionCatalogDoesNotMarkActionSafe_ReturnsProposeOnly()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(AutoApplyPolicy(), changedBy: "test");
        var sut = CreateEvaluator(store, safeActions: new HashSet<string>(StringComparer.Ordinal));

        var decision = await sut.EvaluateRouteAsync(Request(), RequiresApproval(), RedriveAction);

        decision.ShouldAutoApply.Should().BeFalse();
        decision.Reason.Should().Be("action-catalog-not-auto-safe");
    }

    [Fact]
    public async Task RecordProposalRaised_IncrementsTrackRecord()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(AutoApplyPolicy(), changedBy: "test");
        var sut = CreateEvaluator(store);

        await sut.RecordProposalRaisedAsync(Request());

        var listed = await store.ListPoliciesAsync();
        listed.Single().TrackRecord.ProposalsRaised.Should().Be(1);
    }

    [Fact]
    public async Task RecordProposalResolution_MalformedLegacyMetadata_IsIgnored()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        var sut = CreateEvaluator(store);
        var now = DateTimeOffset.UtcNow;
        var proposal = new OperationProposal
        {
            ProposalId = "proposal-legacy",
            Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.Rejected,
            AutonomyMetadata = new OperationProposalAutonomyMetadata
            {
                FindingId = "finding-legacy",
                Rule = " ",
                ActionMarkedAutoSafe = true,
            },
            ResolvedBy = "human-reviewer",
            ResolvedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await sut.RecordProposalResolutionAsync(proposal, OpsAutonomyProposalResolution.Rejected);

        (await store.ListPoliciesAsync()).Should().BeEmpty();
    }

    private static OpsAutonomyEvaluator CreateEvaluator(
        InMemoryOpsAutonomyPolicyStore store,
        IReadOnlySet<string>? safeActions = null)
        => new(
            new StaticOptionsMonitor<OpsAutonomyOptions>(new OpsAutonomyOptions()),
            store,
            new TestActionSafetyCatalog(safeActions ?? new HashSet<string>(StringComparer.Ordinal) { RedriveAction }));

    private static OpsAutonomyPolicy AutoApplyPolicy(
        int maxActions = 2,
        int maxBlastRadius = 5)
        => new()
        {
            Rule = Rule,
            Mode = OpsAutonomyMode.AutoApply,
            MaxAutoActionsPerWindow = maxActions,
            Window = TimeSpan.FromHours(1),
            MaxBlastRadius = maxBlastRadius,
        };

    private static OperationGatewayRequest Request(
        string findingId = FindingId,
        int blastRadius = 1,
        bool actionMarkedAutoSafe = true)
        => new()
        {
            Kind = OperationClass.AdminConfigChange,
            ActionDiscriminator = RedriveAction,
            RequestedByAgent = "ops-findings-autonomy",
            IdempotencyKey = findingId,
            ExecutionPayload = "{\"action\":\"alerts.redrive_dead_letters\"}",
            AutonomyContext = new OperationGatewayAutonomyContext
            {
                FindingId = findingId,
                Rule = Rule,
                ActionDiscriminator = RedriveAction,
                ActionMarkedAutoSafe = actionMarkedAutoSafe,
                BlastRadius = blastRadius,
                EvidenceRefs = ["test"],
            },
        };

    private static GuardrailDecision RequiresApproval()
        => new(
            GuardrailTier.RequiresApproval,
            OperationClass.AdminConfigChange,
            HonuaEdition.Enterprise,
            "test");

    private sealed class TestActionSafetyCatalog(IReadOnlySet<string> safeActions) : IOpsActionSafetyCatalog
    {
        public bool IsAutoSafe(OperationClass operationClass, string? actionDiscriminator)
            => operationClass == OperationClass.AdminConfigChange &&
               actionDiscriminator is not null &&
               safeActions.Contains(actionDiscriminator);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; private set; } = value;

        public void Set(T next) => CurrentValue = next;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
