// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.OperationsToolset.Policy;

/// <summary>
/// Owner decision 10 (2026-10-10): in Production the typed operation policy appends two
/// code-declared rules after the operator rules — full administrators and administrative keys
/// execute directly — and everyone else falls through to the fail-closed default.
/// </summary>
public sealed class OperationPolicyBuiltInRulesTests
{
    private const string PublishOperationId = "service.publish";
    private const string ProductionDefaultReason = "No policy rule matched this caller; Production denies by default.";

    [UnitTheory]
    [InlineData("admin", OperationPolicyBuiltInRules.FullAdministratorReason)]
    [InlineData("scoped-admin-key", OperationPolicyBuiltInRules.AdministrativeKeyReason)]
    public async Task Production_AdministrativeRoles_AreAllowedByNamedBuiltIn(string role, string reason)
    {
        var pdp = BuildPdp(ProductionBaseline(), productionEnvironment: true);

        var decision = await Evaluate(pdp, role);

        decision.Kind.Should().Be(PolicyDecisionKind.Allow);
        decision.Reason.Should().Be(reason);
    }

    [UnitTheory]
    [InlineData("layer-write-key")]
    [InlineData("scoped-api-key")]
    [InlineData("approved-operation")]
    [InlineData(null)]
    public async Task Production_NonAdministrativeCallers_AreDeniedWithDefaultReason(string? role)
    {
        var pdp = BuildPdp(ProductionBaseline(), productionEnvironment: true);

        var decision = await Evaluate(pdp, role);

        decision.Kind.Should().Be(PolicyDecisionKind.Deny);
        decision.Reason.Should().Be(ProductionDefaultReason);
    }

    [UnitTest]
    public async Task Production_OperatorRule_BeatsBuiltIn()
    {
        var options = ProductionBaseline();
        options.Rules.Add(new OperationPolicyRule
        {
            OperationId = PublishOperationId,
            Role = "admin",
            Decision = PolicyDecisionKind.RequireApproval,
            Reason = "operator: publishes need a second pair of eyes",
            ApprovalLane = "publish-review",
        });
        options.Rules.Add(new OperationPolicyRule
        {
            OperationId = "*",
            Role = "scoped-admin-key",
            Decision = PolicyDecisionKind.Deny,
            Reason = "operator: no key-driven operations",
        });
        var pdp = BuildPdp(options, productionEnvironment: true);

        var admin = await Evaluate(pdp, "admin");
        admin.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
        admin.ApprovalLane.Should().Be("publish-review");

        var key = await Evaluate(pdp, "scoped-admin-key");
        key.Kind.Should().Be(PolicyDecisionKind.Deny);
        key.Reason.Should().Be("operator: no key-driven operations");

        // An operator rule that does not match still falls through to the built-ins.
        var otherOperation = await pdp.EvaluateAsync(
            BuildDescriptor("admin.server.status"),
            new OperationRequest { OperationId = "admin.server.status" },
            new OperationPolicyContext { Roles = ["admin"] },
            CancellationToken.None);
        otherOperation.Kind.Should().Be(PolicyDecisionKind.Allow);
        otherOperation.Reason.Should().Be(OperationPolicyBuiltInRules.FullAdministratorReason);
    }

    [UnitTest]
    public void Production_Composition_OrdersOperatorRulesBeforeBuiltIns()
    {
        var options = ProductionBaseline();
        options.Rules.Add(new OperationPolicyRule { OperationId = PublishOperationId, Decision = PolicyDecisionKind.Deny });

        var composition = OperationPolicyBuiltInRules.Compose(options, productionEnvironment: true);

        composition.OperatorRuleCount.Should().Be(1);
        composition.BuiltInRuleCount.Should().Be(2);
        composition.BuiltInRules.Should().BeTrue();
        composition.Rules.Select(static rule => rule.Role)
            .Should().Equal(null, "admin", "scoped-admin-key");
        composition.Describe().Should().Be("1 operator rules + 2 built-in rules, default Deny");
    }

    [UnitTest]
    public async Task Production_OptOut_RemovesBuiltIns()
    {
        var options = ProductionBaseline();
        options.BuiltInProductionRules = false;
        var pdp = BuildPdp(options, productionEnvironment: true);

        pdp.Composition.BuiltInRuleCount.Should().Be(0);
        pdp.Composition.Describe().Should().Be("0 operator rules + 0 built-in rules, default Deny");
        var decision = await Evaluate(pdp, "admin");
        decision.Kind.Should().Be(PolicyDecisionKind.Deny);
        decision.Reason.Should().Be(ProductionDefaultReason);
    }

    [UnitTest]
    public async Task NonProduction_HasNoBuiltIns()
    {
        var pdp = BuildPdp(ProductionBaseline(), productionEnvironment: false);

        pdp.Composition.BuiltInRules.Should().BeFalse();
        (await Evaluate(pdp, "admin")).Kind.Should().Be(PolicyDecisionKind.Deny);
    }

    [UnitTest]
    public async Task Production_DisabledPolicy_StaysPassThroughWithoutBuiltIns()
    {
        var pdp = BuildPdp(new OperationPolicyOptions { Enabled = false }, productionEnvironment: true);

        pdp.Composition.BuiltInRuleCount.Should().Be(0);
        pdp.Composition.Describe().Should().Be("disabled (pass-through allow)");
        (await Evaluate(pdp, null)).Kind.Should().Be(PolicyDecisionKind.Allow);
    }

    [UnitTest]
    public async Task CanonicalPdp_TypedOperation_UsesBuiltInsInProduction()
    {
        var pdp = new CanonicalOperationPolicyDecisionPoint(
            Options.Create(ProductionBaseline()),
            new DenyingLadder(),
            productionEnvironment: true);

        pdp.TypedPolicy.BuiltInRules.Should().BeTrue();
        var admin = await pdp.EvaluateAsync(
            BuildDescriptor(PublishOperationId),
            new OperationRequest { OperationId = PublishOperationId },
            new OperationPolicyContext { Roles = ["scoped-admin-key"] },
            CancellationToken.None);
        admin.Kind.Should().Be(PolicyDecisionKind.Allow);
        admin.Reason.Should().Be(OperationPolicyBuiltInRules.AdministrativeKeyReason);
    }

    [UnitTest]
    public void BuiltInRules_ReturnFreshInstances()
    {
        var first = OperationPolicyBuiltInRules.Production;
        first[0].Decision = PolicyDecisionKind.Deny;

        OperationPolicyBuiltInRules.Production[0].Decision.Should().Be(PolicyDecisionKind.Allow);
    }

    private static OperationPolicyOptions ProductionBaseline() => new()
    {
        Enabled = true,
        DefaultDecision = PolicyDecisionKind.Deny,
        DefaultReason = ProductionDefaultReason,
    };

    private static ConfigurableOperationPolicyDecisionPoint BuildPdp(
        OperationPolicyOptions options,
        bool productionEnvironment)
        => new(Options.Create(options), productionEnvironment);

    private static Task<PolicyDecision> Evaluate(ConfigurableOperationPolicyDecisionPoint pdp, string? role)
        => pdp.EvaluateAsync(
            BuildDescriptor(PublishOperationId),
            new OperationRequest { OperationId = PublishOperationId },
            new OperationPolicyContext { Roles = role is null ? [] : [role] },
            CancellationToken.None);

    private static OperationDescriptor BuildDescriptor(string operationId)
        => new()
        {
            OperationId = operationId,
            ProviderId = "test",
            Title = operationId,
            Description = operationId,
            Category = "Publishing",
            ExecutionKind = OperationExecutionKind.Synchronous,
            ApprovalModel = OperationApprovalModel.OperatorGate,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
                SideEffectClass = OperationSideEffectClass.CreatesMetadata,
                Determinism = OperationDeterminism.Deterministic,
                SupportsDryRun = true,
            },
        };

    private sealed class DenyingLadder : IGuardrailLadder
    {
        public GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition) => Throw();

        public GuardrailDecision Resolve(OperationClass operationClass) => Throw();

        public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator) => Throw();

        public GuardrailDecision Resolve(
            OperationClass operationClass,
            string? actionDiscriminator,
            HonuaEdition edition) => Throw();

        private static GuardrailDecision Throw()
            => throw new InvalidOperationException("Typed operations never consult the legacy ladder.");
    }
}
