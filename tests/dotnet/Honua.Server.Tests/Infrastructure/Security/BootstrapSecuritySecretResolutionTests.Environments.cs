// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Security;

public sealed partial class BootstrapSecuritySecretResolutionTests
{
    [Theory]
    [InlineData("Development", "weak-password")]
    [InlineData("development", "weak-password")]
    [InlineData("Test", "weak-password")]
    [InlineData("test", "weak-password")]
    [InlineData("Production", "Deployment-Admin-Password-Aa1!")]
    [InlineData("Staging", "Deployment-Admin-Password-Aa1!")]
    [InlineData("Deployment", "Deployment-Admin-Password-Aa1!")]
    [InlineData("Development", "Deployment-Admin-Password-Aa1!")]
    [InlineData("Test", "Deployment-Admin-Password-Aa1!")]
    [Trait("Tier", "Fast")]
    public async Task RefreshedAdminPassword_AcceptsPermittedCredential(string environmentName, string password)
    {
        const string reference = "aws:secretsmanager:fixture/admin";
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = password });
        var dependencies = new ApiKeyAuthenticationDependencies(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                AdminPassword = reference,
                EnvironmentName = environmentName
            }), resolver);
        var attempt = new AdminPasswordAttempt(password, "ApiKey",
            new ClientConnection(IPAddress.Loopback, ClientReach.DirectLoopback));

        var result = await ApiKeyAuthenticationHandler.ValidateAdminPasswordAsync(
            attempt, dependencies, NullLogger.Instance, CancellationToken.None);
        var portalResult = await BuildPortalVerifier(reference, resolver, environmentName)
            .VerifyAsync("admin", password, CancellationToken.None);

        (result.Success?.Succeeded == true).Should().BeTrue();
        (portalResult is not null).Should().BeTrue();
        result.Rejection.Should().Be(ApiKeyRejection.None);
    }

    [Theory]
    [InlineData("Production", "weak-password")]
    [InlineData("Staging", "weak-password")]
    [InlineData("Deployment", "weak-password")]
    [InlineData("", "weak-password")]
    [InlineData("Staging", "long-password-without-required-classes")]
    [Trait("Tier", "Fast")]
    public async Task RefreshedAdminPassword_RejectsInvalidCredential(string environmentName, string password)
    {
        const string reference = "aws:secretsmanager:fixture/admin";
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = password });
        var dependencies = new ApiKeyAuthenticationDependencies(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                AdminPassword = reference,
                EnvironmentName = environmentName
            }), resolver);
        var attempt = new AdminPasswordAttempt(password, "ApiKey",
            new ClientConnection(IPAddress.Loopback, ClientReach.DirectLoopback));

        var result = await ApiKeyAuthenticationHandler.ValidateAdminPasswordAsync(
            attempt, dependencies, NullLogger.Instance, CancellationToken.None);
        var portalResult = await BuildPortalVerifier(reference, resolver, environmentName)
            .VerifyAsync("admin", password, CancellationToken.None);

        (result.Success?.Succeeded == true).Should().BeFalse();
        (portalResult is not null).Should().BeFalse();
        result.Rejection.Should().Be(ApiKeyRejection.AdminPasswordUnavailable);
    }

    [Theory]
    [InlineData("Development", "weak-password")]
    [InlineData("development", "weak-password")]
    [InlineData("Test", "weak-password")]
    [InlineData("test", "weak-password")]
    [InlineData("Staging", "Deployment-Admin-Password-Aa1!")]
    [Trait("Tier", "Fast")]
    public async Task ResolveSecuritySecretReferences_AcceptsPermittedCredential(string environmentName, string password)
    {
        const string reference = "aws:secretsmanager:fixture/admin";
        using var configuration = BuildConfiguration(new Dictionary<string, string?> { [AdminKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = password });

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration, resolver, [AdminKey], environmentName);

        await action.Should().NotThrowAsync();
        configuration[AdminKey].Should().Be(reference);
    }

    [Theory]
    [InlineData("Production", "weak-password")]
    [InlineData("Staging", "weak-password")]
    [InlineData("Deployment", "weak-password")]
    [InlineData("", "weak-password")]
    [InlineData("Staging", "long-password-without-required-classes")]
    [Trait("Tier", "Fast")]
    public async Task ResolveSecuritySecretReferences_RejectsInvalidCredential(string environmentName, string password)
    {
        const string reference = "aws:secretsmanager:fixture/admin";
        using var configuration = BuildConfiguration(new Dictionary<string, string?> { [AdminKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = password });

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration, resolver, [AdminKey], environmentName);

        await action.Should().ThrowAsync<InvalidOperationException>();
        configuration[AdminKey].Should().Be(reference);
    }

}
