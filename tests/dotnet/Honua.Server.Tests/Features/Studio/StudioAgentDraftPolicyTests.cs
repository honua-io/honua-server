// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Studio;

// Agents author their own unpublished drafts directly; publication, rollback and deletion stay a human decision.
public sealed class StudioAgentDraftPolicyTests
{
    private const string AgentId = "agent-key-1";

    [UnitTheory]
    [InlineData(StudioDraftOperations.Create)]
    [InlineData(StudioDraftOperations.Update)]
    [InlineData(StudioDraftOperations.RestoreRevision)]
    [InlineData(StudioDraftOperations.Validate)]
    [InlineData(StudioDraftOperations.PreviewPlan)]
    [InlineData(StudioDraftOperations.SaveVersion)]
    public async Task EvaluateAsync_AgentAuthoringOwnDraft_ExecutesDirectly(string operationId)
    {
        var decision = await EvaluateAsync(DefaultPolicy(), Request(operationId));

        decision.Kind.Should().Be(PolicyDecisionKind.Allow);
    }

    [UnitTheory]
    [InlineData(GuardrailTier.RequiresApproval, HonuaEdition.Enterprise, DefaultGuardrailLadder.DefaultPolicySource)]
    [InlineData(GuardrailTier.DirectExecute, HonuaEdition.Pro, DefaultGuardrailLadder.DefaultPolicySource)]
    [InlineData(GuardrailTier.DirectExecute, HonuaEdition.Enterprise, DefaultGuardrailLadder.LicensingDisabledStudioCompositionSource)]
    public async Task EvaluateAsync_AgentAuthoringOwnDraft_ExecutesDirectlyOnEveryBuiltInPolicy(
        GuardrailTier tier, HonuaEdition edition, string source)
    {
        var policy = CreatePolicy(new GuardrailDecision(tier, OperationClass.StudioDraftMutation, edition, source));

        var decision = await EvaluateAsync(policy, Request(StudioDraftOperations.Update));

        decision.Kind.Should().Be(PolicyDecisionKind.Allow);
    }

    [UnitTheory]
    [InlineData(GuardrailTier.DirectExecute, HonuaEdition.Pro)]
    [InlineData(GuardrailTier.RequiresApproval, HonuaEdition.Enterprise)]
    public async Task EvaluateAsync_AgentOutsideItsOwnDraft_RequiresHumanApprovalOnEveryEdition(GuardrailTier tier, HonuaEdition edition)
    {
        var policy = CreatePolicy(new GuardrailDecision(
            tier, OperationClass.StudioDraftMutation, edition, DefaultGuardrailLadder.DefaultPolicySource));
        var request = Request(StudioDraftOperations.Update) with { VerifiedStudioDraftWithinActor = false };

        var decision = await EvaluateAsync(policy, request);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData(StudioDraftOperations.Delete)]
    [InlineData(StudioDraftOperations.ReopenVersion)]
    [InlineData(StudioDraftOperations.CreatePublicationRequest)]
    [InlineData(StudioDraftOperations.Rollback)]
    public async Task EvaluateAsync_AgentDeleteReopenOrPublication_RequiresHumanApproval(string operationId)
    {
        var decision = await EvaluateAsync(DefaultPolicy(), Request(operationId));

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
        decision.Reason.Should().Be(AgentApprovalRule.Reason);
    }

    [UnitTheory]
    [InlineData(StudioDraftOperations.Create)]
    [InlineData(StudioDraftOperations.Update)]
    [InlineData(StudioDraftOperations.RestoreRevision)]
    [InlineData(StudioDraftOperations.Validate)]
    [InlineData(StudioDraftOperations.PreviewPlan)]
    [InlineData(StudioDraftOperations.SaveVersion)]
    public async Task EvaluateAsync_AgentOutsideItsOwnDraftAndItem_RequiresHumanApproval(string operationId)
    {
        var request = Request(operationId) with { VerifiedStudioDraftWithinActor = false };

        var decision = await EvaluateAsync(DefaultPolicy(), request);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData(StudioDraftOperations.Create, null)]
    [InlineData(StudioDraftOperations.Create, " ")]
    [InlineData(StudioDraftOperations.SaveVersion, "")]
    public async Task EvaluateAsync_AgentWithoutPrincipal_RequiresHumanApproval(string operationId, string? principalId)
    {
        var decision = await EvaluateAsync(DefaultPolicy(), Request(operationId), principalId);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData("operator-override", null)]
    [InlineData(DefaultGuardrailLadder.DefaultPolicySource, "studio.publication_proposal")]
    public async Task EvaluateAsync_AgentAuthoring_StricterGuardrailRequiresApproval(string source, string? action)
    {
        var policy = CreatePolicy(new GuardrailDecision(
            GuardrailTier.RequiresApproval, OperationClass.StudioDraftMutation, HonuaEdition.Enterprise, source));

        var decision = await EvaluateAsync(policy, Request(StudioDraftOperations.Update, action: action));

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [UnitTest]
    public async Task EvaluateAsync_AgentAuthoring_BlockedGuardrailDenies()
    {
        var policy = CreatePolicy(new GuardrailDecision(
            GuardrailTier.Blocked, OperationClass.StudioDraftMutation, HonuaEdition.Enterprise, "operator-override"));

        var decision = await EvaluateAsync(policy, Request(StudioDraftOperations.Update));

        decision.Kind.Should().Be(PolicyDecisionKind.Deny);
    }

    [UnitTest]
    public async Task EvaluateAsync_AgentDraftUpdateOutsideTheStudioGateway_RequiresHumanApproval()
    {
        var request = Request(StudioDraftOperations.Update) with { GatewayRequest = null };

        var decision = await EvaluateAsync(DefaultPolicy(), request);

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    private static OperationRequest Request(string operationId, string? action = null) => new()
    {
        OperationId = operationId,
        VerifiedStudioDraftOwnerId = AgentId,
        VerifiedStudioDraftWithinActor = true,
        GatewayRequest = new OperationGatewayRequest
        {
            OperationId = operationId,
            Kind = OperationClass.StudioDraftMutation,
            ActionDiscriminator = action,
        },
    };

    private static Task<PolicyDecision> EvaluateAsync(
        CanonicalOperationPolicyDecisionPoint policy, OperationRequest request, string? principalId = AgentId)
    {
        var descriptor = StudioDraftOperations.BuildDescriptors().Single(item => item.OperationId == request.OperationId);
        var context = new OperationPolicyContext { PrincipalId = principalId, Roles = ["admin", AgentApprovalRule.AgentRole] };
        return policy.EvaluateAsync(descriptor, request, context);
    }

    private static CanonicalOperationPolicyDecisionPoint DefaultPolicy() =>
        CreatePolicy(new GuardrailDecision(
            GuardrailTier.RequiresApproval, OperationClass.StudioDraftMutation, HonuaEdition.Enterprise, DefaultGuardrailLadder.DefaultPolicySource));

    private static CanonicalOperationPolicyDecisionPoint CreatePolicy(GuardrailDecision guardrail)
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.StudioDraftMutation).Returns(guardrail);
        ladder.Resolve(OperationClass.StudioDraftMutation, Arg.Any<string?>()).Returns(guardrail);
        return new CanonicalOperationPolicyDecisionPoint(
            new CanonicalOperationPolicyConfiguration(Options.Create(new OperationPolicyOptions())), ladder);
    }
}
