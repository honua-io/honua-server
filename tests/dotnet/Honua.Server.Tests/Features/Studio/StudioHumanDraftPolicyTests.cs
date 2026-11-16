// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
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

namespace Honua.Server.Tests.Features.Studio;

public sealed class StudioHumanDraftPolicyTests
{
    [UnitTheory]
    [InlineData(StudioDraftOperations.Create, true, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.Create, true, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.Update, true, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.Update, true, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.Delete, true, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.Delete, true, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.Validate, true, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.Validate, true, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.PreviewPlan, true, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.PreviewPlan, true, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.SaveVersion, true, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.SaveVersion, true, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.ReopenVersion, true, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.ReopenVersion, true, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.CreatePublicationRequest, false, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.CreatePublicationRequest, false, OperationInitiationChannel.InteractivePassword)]
    [InlineData(StudioDraftOperations.Rollback, false, OperationInitiationChannel.InteractiveOidc)]
    [InlineData(StudioDraftOperations.Rollback, false, OperationInitiationChannel.InteractivePassword)]
    public async Task EvaluateAsync_VerifiedHuman_OnlyPrivateDraftEditsExecuteDirectly(string operationId, bool direct, OperationInitiationChannel channel)
    {
        var decision = await EvaluateAsync(operationId, channel);

        decision.Kind.Should().Be(direct ? PolicyDecisionKind.Allow : PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData(StudioDraftOperations.Update, null, "draft-owner")]
    [InlineData(StudioDraftOperations.Delete, null, "draft-owner")]
    [InlineData(StudioDraftOperations.Update, "other-owner", "draft-owner")]
    [InlineData(StudioDraftOperations.Delete, "other-owner", "draft-owner")]
    [InlineData(StudioDraftOperations.Update, "draft-owner", null)]
    [InlineData(StudioDraftOperations.Delete, "draft-owner", " ")]
    public async Task EvaluateAsync_UpdateOrDeleteWithoutMatchingStoredOwner_RequiresApproval(
        string operationId, string? storedOwner, string? principalId)
    {
        var policy = CreatePolicy(new GuardrailDecision(GuardrailTier.RequiresApproval,
            OperationClass.StudioDraftMutation, HonuaEdition.Enterprise, "default-policy"));
        var decision = await policy.EvaluateAsync(
            StudioDraftOperations.BuildDescriptors().Single(item => item.OperationId == operationId),
            new OperationRequest
            {
                OperationId = operationId,
                VerifiedStudioDraftOwnerId = storedOwner,
                GatewayRequest = new OperationGatewayRequest
                {
                    OperationId = operationId,
                    Kind = OperationClass.StudioDraftMutation,
                },
            }, new OperationPolicyContext { PrincipalId = principalId });

        decision.Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData(GuardrailTier.RequiresApproval, "operator-override", null)]
    [InlineData(GuardrailTier.Blocked, "operator-override", null)]
    [InlineData(GuardrailTier.RequiresApproval, "default-policy", "studio.publication_proposal")]
    public async Task EvaluateAsync_VerifiedHuman_StricterGuardrailsRemainEnforced(
        GuardrailTier tier, string source, string? action)
    {
        var policy = CreatePolicy(new GuardrailDecision(tier,
            OperationClass.StudioDraftMutation, HonuaEdition.Enterprise, source));
        var decision = await EvaluateAsync(StudioDraftOperations.Create, policy, action);

        decision.Kind.Should().Be(tier == GuardrailTier.Blocked ? PolicyDecisionKind.Deny : PolicyDecisionKind.RequireApproval);
    }

    [UnitTheory]
    [InlineData("mcp", OperationInitiationKind.Agent, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("mcp", OperationInitiationKind.Agent, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("bearer", OperationInitiationKind.Agent, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("bearer", OperationInitiationKind.Agent, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("missing-session", OperationInitiationKind.Agent, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("missing-session", OperationInitiationKind.Agent, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("browser", OperationInitiationKind.Human, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("browser", OperationInitiationKind.Human, (int)AdminAuthSessionOrigin.Builtin)]
    public async Task ResolveAsync_InteractiveSession_RequiresHumanTransport(
        string transport, OperationInitiationKind expected, int originValue)
    {
        var origin = (AdminAuthSessionOrigin)originValue;
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync(origin: origin);
        host.ReviewRequest(human);
        host.Context.Request.Path = transport == "mcp" ? "/mcp" : "/api/v1/studio/package-drafts";
        if (transport == "bearer") host.Context.Request.Headers.Authorization = "Bearer agent-token";
        if (transport == "missing-session") host.Context.Request.Headers.Remove("Cookie");
        var resolver = new OperationInitiationResolver(new HttpContextAccessor { HttpContext = host.Context });

        var initiation = await resolver.ResolveAsync(CancellationToken.None);

        initiation.Kind.Should().Be(expected);
        if (expected == OperationInitiationKind.Human)
            initiation.Channel.Should().Be(origin == AdminAuthSessionOrigin.Builtin
                ? OperationInitiationChannel.InteractivePassword
                : OperationInitiationChannel.InteractiveOidc);
    }

    [UnitTheory]
    [InlineData(null, null, OperationInitiationKind.Human)]
    [InlineData("https://server.example", "same-origin", OperationInitiationKind.Human)]
    [InlineData("https://server.example:443", "same-origin", OperationInitiationKind.Human)]
    [InlineData("https://other.example", "same-site", OperationInitiationKind.Agent)]
    [InlineData("https://server.example:8443", "same-site", OperationInitiationKind.Agent)]
    [InlineData("http://server.example", "same-site", OperationInitiationKind.Agent)]
    [InlineData("null", "same-site", OperationInitiationKind.Agent)]
    [InlineData(null, "same-site", OperationInitiationKind.Agent)]
    [InlineData("https://server.example", "cross-site", OperationInitiationKind.Agent)]
    public async Task ResolveAsync_BrowserOrigin_RequiresTheExactTrustedAuthority(
        string? origin, string? fetchSite, OperationInitiationKind expected)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        host.ReviewRequest(human);
        host.Context.Request.Scheme = "https";
        host.Context.Request.Host = new HostString("server.example");
        host.Context.Request.Path = "/api/v1/studio/package-drafts";
        if (origin is not null) host.Context.Request.Headers.Origin = origin;
        if (fetchSite is not null) host.Context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        var resolver = new OperationInitiationResolver(new HttpContextAccessor { HttpContext = host.Context });

        var initiation = await resolver.ResolveAsync(CancellationToken.None);

        initiation.Kind.Should().Be(expected);
    }

    private static Task<PolicyDecision> EvaluateAsync(string operationId, OperationInitiationChannel channel) =>
        EvaluateAsync(operationId, CreatePolicy(new GuardrailDecision(GuardrailTier.RequiresApproval,
            OperationClass.StudioDraftMutation, HonuaEdition.Enterprise, "default-policy"), channel));

    private static Task<PolicyDecision> EvaluateAsync(
        string operationId, CanonicalOperationPolicyDecisionPoint policy, string? action = null)
    {
        return policy.EvaluateAsync(StudioDraftOperations.BuildDescriptors().Single(item => item.OperationId == operationId),
            new OperationRequest
            {
                OperationId = operationId,
                VerifiedStudioDraftOwnerId = "draft-owner",
                GatewayRequest = new OperationGatewayRequest
                {
                    OperationId = operationId,
                    Kind = OperationClass.StudioDraftMutation,
                    ActionDiscriminator = action
                }
            }, new OperationPolicyContext { PrincipalId = "draft-owner" });
    }

    private static CanonicalOperationPolicyDecisionPoint CreatePolicy(GuardrailDecision guardrail,
        OperationInitiationChannel channel = OperationInitiationChannel.InteractiveOidc)
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.StudioDraftMutation).Returns(guardrail);
        ladder.Resolve(OperationClass.StudioDraftMutation, Arg.Any<string?>()).Returns(guardrail);
        return new CanonicalOperationPolicyDecisionPoint(new CanonicalOperationPolicyConfiguration(Options.Create(new OperationPolicyOptions())),
            ladder, resolveInitiation: _ => Task.FromResult((OperationInitiationKind.Human, channel)));
    }

}
