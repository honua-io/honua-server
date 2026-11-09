// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

public sealed class StudioHumanDraftPolicyTests
{
    [UnitTheory]
    [InlineData(StudioDraftOperations.Create, true)]
    [InlineData(StudioDraftOperations.Update, true)]
    [InlineData(StudioDraftOperations.Delete, true)]
    [InlineData(StudioDraftOperations.Validate, true)]
    [InlineData(StudioDraftOperations.PreviewPlan, true)]
    [InlineData(StudioDraftOperations.SaveVersion, true)]
    [InlineData(StudioDraftOperations.ReopenVersion, true)]
    [InlineData(StudioDraftOperations.CreatePublicationRequest, false)]
    [InlineData(StudioDraftOperations.Rollback, false)]
    public async Task EvaluateAsync_VerifiedHuman_OnlyPrivateDraftEditsExecuteDirectly(string operationId, bool direct)
    {
        var decision = await EvaluateAsync(operationId);

        decision.Kind.Should().Be(direct ? PolicyDecisionKind.Allow : PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData(GuardrailTier.RequiresApproval, "operator-override", null)]
    [InlineData(GuardrailTier.Blocked, "operator-override", null)]
    [InlineData(GuardrailTier.RequiresApproval, "default-policy", "studio.publication_proposal")]
    public async Task EvaluateAsync_VerifiedHuman_StricterGuardrailsRemainEnforced(
        GuardrailTier tier, string source, string? action)
    {
        var decision = await EvaluateAsync(StudioDraftOperations.Create, tier, source, action);

        decision.Kind.Should().Be(tier == GuardrailTier.Blocked ? PolicyDecisionKind.Deny : PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData("/mcp", false, false, OperationInitiationKind.Agent)]
    [InlineData("/api/v1/studio/package-drafts", true, false, OperationInitiationKind.Agent)]
    [InlineData("/api/v1/studio/package-drafts", false, true, OperationInitiationKind.Agent)]
    [InlineData("/api/v1/studio/package-drafts", false, false, OperationInitiationKind.Human)]
    public async Task ResolveAsync_InteractiveSession_RequiresHumanTransport(
        string path, bool explicitCredential, bool missingSession, OperationInitiationKind expected)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        host.ReviewRequest(human);
        host.Context.Request.Path = path;
        if (explicitCredential) host.Context.Request.Headers.Authorization = "Bearer agent-token";
        if (missingSession) host.Context.Request.Headers.Remove("Cookie");
        var resolver = new OperationInitiationResolver(new HttpContextAccessor { HttpContext = host.Context });

        var initiation = await resolver.ResolveAsync(CancellationToken.None);

        initiation.Kind.Should().Be(expected);
    }

    private static Task<PolicyDecision> EvaluateAsync(
        string operationId, GuardrailTier tier = GuardrailTier.RequiresApproval,
        string source = "default-policy", string? action = null)
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        var guardrail = new GuardrailDecision(tier, OperationClass.StudioDraftMutation, HonuaEdition.Enterprise, source);
        ladder.Resolve(OperationClass.StudioDraftMutation).Returns(guardrail);
        ladder.Resolve(OperationClass.StudioDraftMutation, Arg.Any<string?>()).Returns(guardrail);
        var resolver = Substitute.For<IOperationInitiationResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            (OperationInitiationKind.Human, OperationInitiationChannel.InteractiveOidc)));
        var policy = new CanonicalOperationPolicyDecisionPoint(Options.Create(new OperationPolicyOptions()),
            ladder, initiationResolver: resolver);
        return policy.EvaluateAsync(StudioDraftOperations.BuildDescriptors().Single(item => item.OperationId == operationId),
            new OperationRequest
            {
                OperationId = operationId,
                GatewayRequest = new OperationGatewayRequest
                {
                    OperationId = operationId, Kind = OperationClass.StudioDraftMutation, ActionDiscriminator = action
                }
            }, new OperationPolicyContext());
    }
}
