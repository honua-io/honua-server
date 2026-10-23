// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// A host that registers its own <see cref="IPortalCredentialVerifier"/> before
/// <see cref="PortalTokenAuthenticationExtensions.AddPortalTokenAuthentication"/>
/// must still be able to activate the OAuth broker. The broker depends on the
/// concrete OIDC verifier, not on whichever implementation was selected for
/// generateToken.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class CustomPortalVerifierOAuthRegistrationTests
{
    [UnitTest]
    public void CustomVerifier_DoesNotReplaceTheInterface_AndActivatesTheBroker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IPortalCredentialVerifier, RecordingPortalCredentialVerifier>();
        services.AddPortalTokenAuthentication(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPortalCredentialVerifier>()
            .Should().BeOfType<RecordingPortalCredentialVerifier>();
        scope.ServiceProvider.GetRequiredService<OidcPortalCredentialVerifier>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<PortalOAuthBroker>().Should().NotBeNull();
    }

    private sealed class RecordingPortalCredentialVerifier : IPortalCredentialVerifier
    {
        public Task<PortalCredentialPrincipal?> VerifyAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
            => Task.FromResult<PortalCredentialPrincipal?>(null);
    }
}
