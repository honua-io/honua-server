// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class CurrentOperationAuthorityRevalidatorTests
{
    [Fact]
    public async Task RevalidateAsync_UsesCurrentMembershipInsteadOfCapturedAdminRole()
    {
        var membership = Substitute.For<IPrincipalMembershipSource>();
        membership.ResolveMembershipAsync("proposer", "issuer", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["viewer"]));
        ClaimsPrincipal? evaluatedPrincipal = null;
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(
                Arg.Do<ClaimsPrincipal>(principal => evaluatedPrincipal = principal),
                Arg.Any<OperatorAuthorizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Forbidden("current grant denied"));
        var sut = CreateSut(authorization, membership, Substitute.For<IAdminApiKeyStore>());

        var result = await sut.RevalidateAsync(CreateProposal(CreateAuthority() with
        {
            Roles = ["admin"],
            RoleCeiling = ["admin"],
        }));

        result.IsAllowed.Should().BeFalse();
        evaluatedPrincipal.Should().NotBeNull();
        evaluatedPrincipal!.IsInRole("admin").Should().BeFalse(
            "a captured admin role is only a ceiling and cannot bypass current membership");
    }

    [Fact]
    public async Task RevalidateAsync_InactiveMembershipFailsBeforeGrantEvaluation()
    {
        var membership = Substitute.For<IPrincipalMembershipSource>();
        membership.ResolveMembershipAsync("proposer", "issuer", Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(false, []));
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        var sut = CreateSut(authorization, membership, Substitute.For<IAdminApiKeyStore>());

        var result = await sut.RevalidateAsync(CreateProposal(CreateAuthority()));

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("no longer active");
        await authorization.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default!, default);
    }

    [Fact]
    public async Task RevalidateAsync_RevokedApiKeyFailsBeforeGrantEvaluation()
    {
        var apiKeyId = Guid.NewGuid();
        var apiKeyStore = Substitute.For<IAdminApiKeyStore>();
        apiKeyStore.GetAsync(apiKeyId, Arg.Any<CancellationToken>()).Returns(new AdminApiKeyRecord(
            apiKeyId,
            "deploy-key",
            "hnua_prefix",
            [],
            ["admin:*"],
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow,
            ExpiresAt: null,
            LastUsedAt: null,
            RotatedAt: null,
            RevokedAt: DateTimeOffset.UtcNow,
            CreatedBy: "admin"));
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        var sut = CreateSut(
            authorization,
            Substitute.For<IPrincipalMembershipSource>(),
            apiKeyStore);
        var authority = CreateAuthority() with
        {
            Actor = apiKeyId.ToString("D"),
            Issuer = AuthenticationExtensions.ApiKeyScheme,
            Scheme = AuthenticationExtensions.ApiKeyScheme,
            Permissions = ["admin:*"],
            PermissionCeiling = ["admin:*"],
            Roles = ["admin"],
            RoleCeiling = ["admin"],
        };

        var result = await sut.RevalidateAsync(CreateProposal(authority));

        result.IsAllowed.Should().BeFalse();
        result.Reason.Should().Contain("revoked");
        await authorization.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default!, default);
    }

    [Fact]
    public async Task RevalidateAsync_TrustedOpsFindingsServiceRemainsCredentialless()
    {
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        var membership = Substitute.For<IPrincipalMembershipSource>();
        var apiKeyStore = Substitute.For<IAdminApiKeyStore>();
        var sut = CreateSut(authorization, membership, apiKeyStore);
        var authority = CreateAuthority() with
        {
            Issuer = "honua-server",
            Actor = "ops-findings",
            Scheme = "Service",
            EffectiveTenant = "platform",
        };

        var result = await sut.RevalidateAsync(CreateProposal(authority));

        result.IsAllowed.Should().BeTrue();
        await membership.DidNotReceive().ResolveMembershipAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await apiKeyStore.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        await authorization.DidNotReceiveWithAnyArgs().EvaluateAsync(default!, default!, default);
    }

    private static CurrentOperationAuthorityRevalidator CreateSut(
        IOperatorAuthorizationEvaluator authorization,
        IPrincipalMembershipSource membership,
        IAdminApiKeyStore apiKeyStore)
    {
        var scope = Substitute.For<IOperatorScopeAuthorizer>();
        scope.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorResourceType>(), Arg.Any<OperatorOperation>())
            .Returns(OperatorScopeDecision.Allowed());
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(new GuardrailDecision(
            GuardrailTier.RequiresApproval,
            OperationClass.Deploy,
            HonuaEdition.Enterprise,
            "test"));
        return new CurrentOperationAuthorityRevalidator(
            authorization,
            scope,
            ladder,
            membership,
            apiKeyStore);
    }

    private static OperationProposal CreateProposal(OperationAuthorityContext authority) => new()
    {
        ProposalId = "proposal-1",
        Kind = OperationClass.Deploy,
        Authority = authority,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static OperationAuthorityContext CreateAuthority() => new()
    {
        Issuer = "issuer",
        Actor = "proposer",
        Scheme = "Bearer",
        EffectiveTenant = "tenant-1",
        ScopeGoverned = false,
        ResourceType = OperatorResourceType.Deployment,
        ResourceId = "target-1",
        Operation = OperatorOperation.Publish,
    };
}
