// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
public sealed class StoredApiKeyCredentialVersionTests
{
    [UnitTest]
    public async Task ActualAuthentication_RotationChangesVersionWithoutChangingActorOrGrants()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("agent", ["admin:read", "agent:workspace"],
            DateTimeOffset.UtcNow.AddMinutes(15), "test", default);
        var original = await AuthenticateAsync(store, created.Key);
        var originalVersion = StoredApiKeyCredentialVersion.GetStamped(original);
        originalVersion.Should().Be(StoredApiKeyCredentialVersion.FromRecord(created.Record));
        originalVersion.Should().NotBe(Convert.ToHexStringLower(created.Record.KeyHash));
        original.Claims.Should().NotContain(claim => claim.Value.Contains(created.Key, StringComparison.Ordinal));

        // Last-used writes must not revoke an otherwise unchanged credential.
        var used = (await store.GetAsync(created.Record.Id, default))!;
        StoredApiKeyCredentialVersion.FromRecord(used with
        {
            UpdatedAt = used.UpdatedAt.AddDays(1), LastUsedAt = used.LastUsedAt?.AddDays(1),
        }).Should().Be(originalVersion);

        var rotated = (await store.RotateAsync(created.Record.Id, default))!;
        rotated.Record.Id.Should().Be(created.Record.Id);
        rotated.Record.Permissions.Should().Equal(created.Record.Permissions);
        var fresh = await AuthenticateAsync(store, rotated.Key);
        CanonicalSecurityActor.Resolve(fresh)!.ActorId.Should().Be(CanonicalSecurityActor.Resolve(original)!.ActorId);
        StoredApiKeyCredentialVersion.GetStamped(fresh).Should().Be(StoredApiKeyCredentialVersion.FromRecord(rotated.Record))
            .And.NotBe(originalVersion);
        StoredApiKeyCredentialVersion.GetStamped(original).Should().Be(originalVersion,
            "a retained principal cannot acquire the new credential's version from a later store read");
        (await store.ValidateAsync(created.Key, default)).Should().BeNull();
        var reconstructed = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(rotated.Record);
        StoredApiKeyCredentialVersion.GetStamped(reconstructed).Should().Be(StoredApiKeyCredentialVersion.GetStamped(fresh));
    }

    [UnitTest]
    public async Task Authentication_StampsTheExactValidatedRecordWithoutReloadingItsReplacement()
    {
        var store = new InMemoryAdminApiKeyStore();
        var original = await store.CreateAsync("agent", ["agent:workspace"], null, "test", default);
        var replacement = (await store.RotateAsync(original.Record.Id, default))!;
        var racingStore = Substitute.For<IAdminApiKeyStore>();
        racingStore.ValidateAsync(original.Key, Arg.Any<CancellationToken>()).Returns(new AdminApiKeyValidationResult(original.Record));
        racingStore.GetAsync(original.Record.Id, Arg.Any<CancellationToken>()).Returns(replacement.Record);

        var principal = await AuthenticateAsync(racingStore, original.Key);
        StoredApiKeyCredentialVersion.GetStamped(principal).Should().Be(StoredApiKeyCredentialVersion.FromRecord(original.Record))
            .And.NotBe(StoredApiKeyCredentialVersion.FromRecord(replacement.Record));
        await racingStore.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task MissingForgedOrAmbiguousVersion_CannotClaimValidatedCredentialProvenance()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("agent", ["agent:workspace"], null, "test", default);
        var legacy = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record.Id, created.Record.Name, created.Record.Permissions);
        StoredApiKeyCredentialVersion.GetStamped(legacy).Should().BeNull();
        var identity = (ClaimsIdentity)legacy.Identity!;
        identity.AddClaim(new Claim(StoredApiKeyCredentialVersion.ClaimType, StoredApiKeyCredentialVersion.FromRecord(created.Record)));
        StoredApiKeyCredentialVersion.GetStamped(legacy).Should().BeNull();
        CanonicalSecurityActor.RemoveUnstampedAuthorityClaims(legacy);
        legacy.HasClaim(claim => claim.Type == StoredApiKeyCredentialVersion.ClaimType).Should().BeFalse();
        OidcClaimsTransformation.IsReservedProvenanceClaimType(StoredApiKeyCredentialVersion.ClaimType).Should().BeTrue();

        var authenticated = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var claim = authenticated.FindFirst(StoredApiKeyCredentialVersion.ClaimType)!;
        ((ClaimsIdentity)authenticated.Identity!).AddClaim(claim.Clone());
        StoredApiKeyCredentialVersion.GetStamped(authenticated).Should().BeNull();
        StoredApiKeyCredentialVersion.GetStamped(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull();
    }

    private static async Task<ClaimsPrincipal> AuthenticateAsync(IAdminApiKeyStore store, string material)
    {
        var validation = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(material, AuthenticationExtensions.ApiKeyScheme,
            new ApiKeyAuthenticationDependencies(Options.Create(new ApiKeyAuthenticationOptions()), adminApiKeyStore: store),
            NullLogger.Instance, default);
        validation.Success.Should().NotBeNull();
        validation.Success!.Succeeded.Should().BeTrue();
        return validation.Success.Principal!;
    }
}
