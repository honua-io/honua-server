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
    [InlineData("Production", "weak-password", false)]
    [InlineData("Staging", "weak-password", false)]
    [InlineData("Deployment", "weak-password", false)]
    [InlineData("", "weak-password", false)]
    [InlineData("Development", "weak-password", true)]
    [InlineData("development", "weak-password", true)]
    [InlineData("Test", "weak-password", true)]
    [InlineData("test", "weak-password", true)]
    [InlineData("Production", "Deployment-Admin-Password-Aa1!", true)]
    [InlineData("Staging", "Deployment-Admin-Password-Aa1!", true)]
    [InlineData("Deployment", "Deployment-Admin-Password-Aa1!", true)]
    [InlineData("Development", "Deployment-Admin-Password-Aa1!", true)]
    [InlineData("Test", "Deployment-Admin-Password-Aa1!", true)]
    [InlineData("Staging", "long-password-without-required-classes", false)]
    [Trait("Tier", "Fast")]
    public async Task RefreshedAdminPassword_Credential_AppliesEnvironmentPolicy(
        string environmentName, string password, bool accepts)
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

        (result.Success?.Succeeded == true).Should().Be(accepts);
        (portalResult is not null).Should().Be(accepts);
        result.Rejection.Should().Be(accepts ? ApiKeyRejection.None : ApiKeyRejection.AdminPasswordUnavailable);
    }

    [Theory]
    [InlineData("Production", "weak-password", false)]
    [InlineData("Staging", "weak-password", false)]
    [InlineData("Deployment", "weak-password", false)]
    [InlineData("", "weak-password", false)]
    [InlineData("Development", "weak-password", true)]
    [InlineData("development", "weak-password", true)]
    [InlineData("Test", "weak-password", true)]
    [InlineData("test", "weak-password", true)]
    [InlineData("Staging", "Deployment-Admin-Password-Aa1!", true)]
    [InlineData("Staging", "long-password-without-required-classes", false)]
    [Trait("Tier", "Fast")]
    public async Task ResolveSecuritySecretReferences_AdminPassword_AppliesEnvironmentPolicy(
        string environmentName, string password, bool accepts)
    {
        const string reference = "aws:secretsmanager:fixture/admin";
        using var configuration = BuildConfiguration(new Dictionary<string, string?> { [AdminKey] = reference });
        var resolver = new StubSecretResolver(new Dictionary<string, string> { [reference] = password });

        var action = () => StartupConfigurationHelpers.ResolveSecuritySecretReferencesAsync(
            configuration, resolver, [AdminKey], environmentName);

        if (accepts)
        {
            await action.Should().NotThrowAsync();
        }
        else
        {
            await action.Should().ThrowAsync<InvalidOperationException>();
        }

        configuration[AdminKey].Should().Be(reference);
    }

}
