using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.ControlPlane;

public sealed class OperationAuthorityContractTests
{
    [UnitTest]
    public void ScopeCeiling_CannotExceedAuthenticatedScopes()
    {
        var authority = Authority(["service:read"], ["service:write"]);

        authority.TryValidate(out var error).Should().BeFalse();
        error.Should().Contain("scope ceiling");
    }

    [UnitTest]
    public void ValidAuthority_IsBoundedAndReplayable()
    {
        var authority = Authority(["service:read", "service:write"], ["service:read"]);

        authority.TryValidate(out var error).Should().BeTrue();
        error.Should().BeNull();
        (authority with { }).Should().Be(authority);
    }

    [UnitTest]
    public void ApprovalRecord_DoesNotDefaultToAuthorityRetention()
    {
        var approval = new OperationApprovalRecord
        {
            Approver = "approver-1",
            Approved = true,
            DecidedAt = DateTimeOffset.UtcNow,
        };

        approval.ProposerAuthorityRetained.Should().BeFalse();
    }

    [UnitTest]
    public void Capture_AuthenticatedPrincipal_PinsIssuerActorTenantAndScopes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "operator-1"),
                new Claim("iss", "https://issuer.example"),
                new Claim(OperatorScopeCatalog.ScopeClaimType, "honua.mcp.read honua.mcp.publish"),
                new Claim(OperatorScopeCatalog.ScpClaimType, "honua.mcp.read"),
            ],
            "Bearer"));

        var authority = OperationAuthorityContext.Capture(principal, "tenant-1");

        authority.Issuer.Should().Be("https://issuer.example");
        authority.Actor.Should().Be("operator-1");
        authority.Scheme.Should().Be("Bearer");
        authority.EffectiveTenant.Should().Be("tenant-1");
        authority.OAuthScopes.Should().Equal("honua.mcp.publish", "honua.mcp.read");
        authority.ScopeCeiling.Should().Equal(authority.OAuthScopes);
    }

    [UnitTest]
    public void Capture_UnauthenticatedPrincipal_FailsClosed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "operator-1")]));

        var act = () => OperationAuthorityContext.Capture(principal, "tenant-1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*authenticated principal is required*");
    }

    [UnitTest]
    public void Capture_ApiKeyPrincipal_PrefersUniqueKeyIdOverSharedDisplayName()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "admin"),
                new Claim("api_key_id", "api-key-42"),
                new Claim("api_key_name", "release automation"),
                new Claim("permission", "admin:deploy"),
                new Claim("permission", "read:services/world"),
                new Claim("permission", "admin:deploy"),
            ],
            "ApiKey"));

        var authority = OperationAuthorityContext.Capture(principal, "tenant-1");

        authority.Actor.Should().Be("api-key-42");
        authority.Scheme.Should().Be("ApiKey");
        authority.OAuthScopes.Should().BeEmpty();
        authority.Permissions.Should().Equal("admin:deploy", "read:services/world");
        authority.PermissionCeiling.Should().Equal(authority.Permissions);
    }

    [UnitTest]
    public void PermissionCeiling_CannotExceedAuthenticatedPermissions()
    {
        var authority = Authority([], []) with
        {
            Permissions = ["read:services/world"],
            PermissionCeiling = ["admin:deploy"],
        };

        authority.TryValidate(out var error).Should().BeFalse();
        error.Should().Contain("permission ceiling");
    }

    [Fact]
    public void PreMarkerBearerAuthority_IsScopeGovernedForReplay()
    {
        var authority = Authority([], []);

        authority.ScopeGoverned.Should().BeNull();
        authority.IsScopeGovernedForReplay().Should().BeTrue();
        authority.PermitsBoundOperation().Should().BeFalse();
    }

    [Fact]
    public void OperatorBearerWithoutOAuthScopes_RemainsGrantGoverned()
    {
        var authority = Authority([], []) with
        {
            Scheme = "OperatorBearer",
            ScopeGoverned = false,
        };

        authority.IsScopeGovernedForReplay().Should().BeFalse();
        authority.PermitsBoundOperation().Should().BeTrue();
    }

    private static OperationAuthorityContext Authority(
        IReadOnlyList<string> scopes,
        IReadOnlyList<string> ceiling)
        => new()
        {
            Issuer = "https://issuer.example",
            Actor = "proposer-1",
            Scheme = "Bearer",
            EffectiveTenant = "tenant-1",
            OAuthScopes = scopes,
            ScopeCeiling = ceiling,
        };
}
