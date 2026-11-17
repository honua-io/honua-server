// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Startup;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AdminPasswordEnvironmentTests
{
    [Theory]
    [InlineData("Production", "weak-password")]
    [InlineData("Staging", "weak-password")]
    [InlineData("Deployment", "weak-password")]
    [InlineData("", "weak-password")]
    [InlineData("Staging", "long-password-without-required-classes")]
    [Trait("Tier", "Fast")]
    public void AuthenticationOptions_WeakDeploymentPassword_RejectsConfiguration(string environmentName, string password)
    {
        var action = () => BuildOptions(environmentName, password);

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData("Test")]
    [InlineData("test")]
    [Trait("Tier", "Fast")]
    public void AuthenticationOptions_WeakLocalPassword_PreservesCompatibility(string environmentName)
    {
        BuildOptions(environmentName, "weak-password").AdminPassword.Should().Be("weak-password");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Deployment")]
    [InlineData("Development")]
    [InlineData("Test")]
    [Trait("Tier", "Fast")]
    public void AuthenticationOptions_ValidPassword_AcceptsConfiguration(string environmentName)
    {
        const string password = "Deployment-Admin-Password-Aa1!";

        BuildOptions(environmentName, password).AdminPassword.Should().Be(password);
    }

    private static ApiKeyAuthenticationOptions BuildOptions(string environmentName, string password)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["HONUA_ADMIN_PASSWORD"] = password });
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var services = new ServiceCollection();
        services.AddHonuaAuthenticationOptions(configuration, environment);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ApiKeyAuthenticationOptions>>().Value;
    }
}
