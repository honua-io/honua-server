// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Studio.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.OperationsToolset.Policy;

public sealed class AgentApprovalRuleTests
{
    [UnitTheory]
    [InlineData("admin.services.access-policy.set")]
    [InlineData("admin.layer.publish")]
    [InlineData("service.publish")]
    public async Task EvaluateAsync_AgentGatedOperation_PolicyDisabled_RequiresApproval(string operationId)
    {
        var pdp = BuildPdp(new OperationPolicyOptions { Enabled = false }, productionEnvironment: false);

        var decision = await EvaluateAsync(pdp, operationId, AgentApprovalRule.AgentRole);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
        decision.ApprovalLane.Should().Be(AgentApprovalRule.ApprovalLane);
        decision.Reason.Should().Contain("Console Approvals");
    }

    [UnitTest]
    public async Task EvaluateAsync_AgentWithAdminRole_Production_AdminBuiltInDoesNotExempt()
    {
        var pdp = BuildPdp(ProductionOptions(), productionEnvironment: true);

        var decision = await EvaluateAsync(
            pdp, "admin.layer.publish", OperationPolicyBuiltInRules.FullAdministratorRole, AgentApprovalRule.AgentRole);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [UnitTest]
    public async Task EvaluateAsync_AgentGatedOperation_OperatorAllowRule_CannotOverride()
    {
        var options = new OperationPolicyOptions
        {
            Enabled = true,
            DefaultDecision = PolicyDecisionKind.Allow,
            Rules =
            [
                new OperationPolicyRule
                {
                    OperationId = "admin.services.access-policy.set",
                    Role = AgentApprovalRule.AgentRole,
                    Decision = PolicyDecisionKind.Allow,
                },
            ],
        };
        var pdp = BuildPdp(options, productionEnvironment: false);

        var decision = await EvaluateAsync(pdp, "admin.services.access-policy.set", AgentApprovalRule.AgentRole);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [UnitTest]
    public async Task EvaluateAsync_AgentUngatedOperation_FollowsConfiguredPolicy()
    {
        var pdp = BuildPdp(new OperationPolicyOptions { Enabled = false }, productionEnvironment: false);

        var decision = await EvaluateAsync(pdp, "admin.connections.create", AgentApprovalRule.AgentRole);

        decision.Kind.Should().Be(PolicyDecisionKind.Allow);
    }

    [UnitTest]
    public async Task EvaluateAsync_HumanAdministrator_GatedOperation_KeepsBuiltInAllow()
    {
        var pdp = BuildPdp(ProductionOptions(), productionEnvironment: true);

        var decision = await EvaluateAsync(pdp, "admin.layer.publish", OperationPolicyBuiltInRules.FullAdministratorRole);

        decision.Kind.Should().Be(PolicyDecisionKind.Allow);
        decision.Reason.Should().Be(OperationPolicyBuiltInRules.FullAdministratorReason);
    }

    [UnitTest]
    public void IsAgent_MatchesRoleCaseInsensitively()
    {
        AgentApprovalRule.IsAgent(["Agent"]).Should().BeTrue();
        AgentApprovalRule.IsAgent(["admin", "scoped-admin-key"]).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("authorized", null, PolicyDecisionKind.RequireApproval)]
    [InlineData("admin-direct-publication", null, PolicyDecisionKind.RequireApproval)]
    [InlineData("autonomy-authorized", null, PolicyDecisionKind.RequireApproval)]
    [InlineData("authorized", "approved-proposal", PolicyDecisionKind.Allow)]
    public async Task CanonicalPdp_AgentStudioPublication_RequiresHumanDecision(
        string authorizationOutcome, string? approvedProposalId, PolicyDecisionKind expected)
    {
        var operationId = StudioDraftMutationContext.PublicationOperationId;
        var pdp = new CanonicalOperationPolicyDecisionPoint(
            Options.Create(new OperationPolicyOptions { Enabled = false }), new DirectLadder());
        var request = new OperationRequest
        {
            OperationId = operationId,
            GatewayRequest = new OperationGatewayRequest
            {
                Kind = OperationClass.StudioDraftMutation,
                ActionDiscriminator = BuiltInGuardrailActions.StudioPublicationRequest,
            },
        };

        var decision = await pdp.EvaluateAsync(BuildDescriptor(operationId), request,
            new OperationPolicyContext
            {
                Roles = ["admin", "agent"],
                AuthorizationOutcome = authorizationOutcome,
                ApprovedProposalId = approvedProposalId,
            });

        decision.Kind.Should().Be(expected);
    }

    private sealed class DirectLadder : IGuardrailLadder
    {
        public GuardrailDecision Resolve(OperationClass kind) => Direct();
        public GuardrailDecision Resolve(OperationClass kind, HonuaEdition edition) => Direct();
        public GuardrailDecision Resolve(OperationClass kind, string? action) => Direct();
        public GuardrailDecision Resolve(OperationClass kind, string? action, HonuaEdition edition) => Direct();
        private static GuardrailDecision Direct() => new(
            GuardrailTier.DirectExecute, OperationClass.StudioDraftMutation, HonuaEdition.Community, "test: direct edition");
    }

    private static OperationPolicyOptions ProductionOptions() => new()
    {
        Enabled = true,
        DefaultDecision = PolicyDecisionKind.Deny,
    };

    private static ConfigurableOperationPolicyDecisionPoint BuildPdp(OperationPolicyOptions options, bool productionEnvironment)
        => new(Options.Create(options), productionEnvironment);

    private static Task<PolicyDecision> EvaluateAsync(
        ConfigurableOperationPolicyDecisionPoint pdp,
        string operationId,
        params string[] roles)
        => pdp.EvaluateAsync(
            BuildDescriptor(operationId),
            new OperationRequest { OperationId = operationId },
            new OperationPolicyContext { Roles = roles },
            CancellationToken.None);

    private static OperationDescriptor BuildDescriptor(string operationId) => new()
    {
        OperationId = operationId,
        ProviderId = "test",
        Title = operationId,
        Description = operationId,
        Category = "Admin",
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
