using FluentAssertions;
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
