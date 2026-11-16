// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class PortalTokenSourceValidatorTests
{
    [UnitTest]
    public async Task IsStillValidAsync_AfterRevocation_AllPreviouslyUsedInstancesRefuse()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("worker", [], null, "test", CancellationToken.None);
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(store).BuildServiceProvider();
        var first = new PortalTokenSourceValidator(services);
        var second = new PortalTokenSourceValidator(services);
        var source = SourceFor(created.Record);

        (await first.IsStillValidAsync(source, CancellationToken.None)).Should().BeTrue();
        (await second.IsStillValidAsync(source, CancellationToken.None)).Should().BeTrue();
        await store.RevokeAsync(created.Record.Id, CancellationToken.None);

        (await first.IsStillValidAsync(source, CancellationToken.None)).Should().BeFalse();
        (await second.IsStillValidAsync(source, CancellationToken.None)).Should().BeFalse();
    }

    [UnitTest]
    public Task IsStillValidAsync_AfterSuccessfulRead_MissingAuthorityRefuses() =>
        AssertAuthorityRefusesAsync(() => Task.FromResult<AdminApiKeyRecord?>(null));

    [UnitTest]
    public Task IsStillValidAsync_AfterSuccessfulRead_UnavailableAuthorityRefuses() =>
        AssertAuthorityRefusesAsync(() => Task.FromException<AdminApiKeyRecord?>(new InvalidOperationException("unavailable")));

    [UnitTest]
    public async Task ResolveAdminPasswordAsync_CachedSecretDiffers_UsesFreshAuthority()
    {
        const string reference = "aws:secretsmanager:portal/password";
        const string currentPassword = "Current-Password-673!";
        var resolver = Substitute.For<IConnectionSecretResolver>();
        resolver.CanResolveSecretAsync(reference, Arg.Any<CancellationToken>()).Returns(true);
        resolver.ResolveConnectionStringAsync(reference, Arg.Any<CancellationToken>()).Returns("Previous-Password-829!");
        resolver.ResolveSecretFreshAsync(reference, Arg.Any<CancellationToken>()).Returns(currentPassword);

        var password = await PortalCredentialSourceVersion.ResolveAdminPasswordAsync(
            new ApiKeyAuthenticationOptions { AdminPassword = reference, EnvironmentName = "Production" },
            resolver, CancellationToken.None);

        password.Should().Be(currentPassword);
        await resolver.DidNotReceive().ResolveConnectionStringAsync(reference, Arg.Any<CancellationToken>());
    }

    private static async Task AssertAuthorityRefusesAsync(Func<Task<AdminApiKeyRecord?>> resolveAuthority)
    {
        var seed = await new InMemoryAdminApiKeyStore().CreateAsync("worker", [], null, "test", CancellationToken.None);
        var store = Substitute.For<IAdminApiKeyStore>();
        store.GetAsync(seed.Record.Id, Arg.Any<CancellationToken>()).Returns(seed.Record);
        using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var validator = new PortalTokenSourceValidator(services);
        var source = SourceFor(seed.Record);
        (await validator.IsStillValidAsync(source, CancellationToken.None)).Should().BeTrue();

        store.GetAsync(seed.Record.Id, Arg.Any<CancellationToken>()).Returns(_ => resolveAuthority());

        (await validator.IsStillValidAsync(source, CancellationToken.None)).Should().BeFalse();
    }

    private static PortalTokenSourceRecord SourceFor(AdminApiKeyRecord record) => new()
    {
        Kind = PortalCredentialSourceKind.ManagedApiKey,
        Reference = record.Id.ToString("D"),
        Version = PortalCredentialSourceVersion.ForManagedKey(record),
    };
}
