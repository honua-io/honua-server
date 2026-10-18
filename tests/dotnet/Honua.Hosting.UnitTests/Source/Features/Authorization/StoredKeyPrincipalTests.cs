// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class StoredKeyPrincipalTests
{
    [UnitTheory]
    [InlineData("admin:read")]
    [InlineData("admin:write")]
    [InlineData("admin")]
    [InlineData("agent:workspace")]
    public async Task MetadataFactory_MatchesActualStoredKeyAuthentication_WithoutSecretInput(string permission)
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("parity", [permission], null, "fixture", CancellationToken.None);
        var live = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(created.Key,
            AuthenticationExtensions.ApiKeyScheme,
            new ApiKeyAuthenticationDependencies(Options.Create(new ApiKeyAuthenticationOptions()), adminApiKeyStore: store),
            NullLogger.Instance, CancellationToken.None);
        var replay = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        replay.Claims.Select(claim => (claim.Type, claim.Value, claim.Issuer))
            .Should().Equal(live.Success!.Principal!.Claims.Select(claim => (claim.Type, claim.Value, claim.Issuer)));
        replay.Identity!.AuthenticationType.Should().Be(live.Success.Principal.Identity!.AuthenticationType);
        CanonicalSecurityActor.Resolve(replay).Should().Be(CanonicalSecurityActor.Resolve(live.Success.Principal));
    }

    [UnitTest]
    public void MissingMetadata_CannotSelectBootstrapSemantics()
    {
        var noId = () => ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(Guid.Empty, "key", []);
        noId.Should().Throw<ArgumentException>();
        var noName = () => ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(Guid.NewGuid(), " ", []);
        noName.Should().Throw<ArgumentException>();
        var noGrants = () => ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(Guid.NewGuid(), "key", null!);
        noGrants.Should().Throw<ArgumentNullException>();
    }
}
