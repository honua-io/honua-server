// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class OperatorBearerWithoutOidcPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnabledOperatorBearer_IsAcceptedWithoutExternalOidc_AndRetainsRequirements(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "false",
            ["Authentication:OperatorBearer:Enabled"] = enabled.ToString()
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddApiKeyAuthentication(configuration);
        services.AddOidcAuthentication(configuration);
        // Exercise a named policy that relies on the implicit default scheme.
        services.Configure<AuthorizationOptions>(options =>
            options.AddPolicy(AuthenticationExtensions.StudioAiProxyPolicy, policy => policy.RequireAuthenticatedUser()));
        services.AddOidcAuthorization(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        options.DefaultPolicy.AuthenticationSchemes.Contains(OidcAuthenticationExtensions.OperatorBearerScheme).Should().Be(enabled);
        if (enabled)
        {
            options.GetPolicy(AuthenticationExtensions.StudioAiProxyPolicy)!.AuthenticationSchemes
                .Should().Contain(AuthenticationExtensions.ApiKeyScheme);
        }
        options.DefaultPolicy.Requirements.Should().Contain(requirement => requirement is DenyAnonymousAuthorizationRequirement);
        foreach (var name in new[] { AuthenticationExtensions.AdminPolicy, AuthenticationExtensions.AdminApprovePolicy })
        {
            var policy = options.GetPolicy(name)!;
            policy.AuthenticationSchemes.Should().Contain(AuthenticationExtensions.ApiKeyScheme);
            policy.AuthenticationSchemes.Contains(OidcAuthenticationExtensions.OperatorBearerScheme).Should().Be(enabled);
            policy.Requirements.Should().Contain(requirement => requirement is RolesAuthorizationRequirement);
        }
        options.GetPolicy(AuthenticationExtensions.AdminPolicy)!.Requirements.Should().Contain(requirement => requirement is AdminPermissionRequirement);
        options.GetPolicy(AuthenticationExtensions.AdminApprovePolicy)!.Requirements.Should().Contain(requirement => requirement is AdminApproveRequirement);
    }
}
