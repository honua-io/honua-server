// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AdminAuthSessionApprovalTests
{
    [UnitTest]
    public async Task GetInteractiveSessionForApprovalAsync_ClaimsAssertInteractiveOrigin_RefusesUnknownSession()
    {
        var store = CreateStore(new InMemorySecurityTokenStore());
        var id = await store.CreateAuthenticatedSessionAsync(
            "oidc", "access-token", "id-token",
            [new() { Type = "sub", Value = "person" },
             new() { Type = "amr", Value = "pwd" },
             new() { Type = "sid", Value = "interactive" },
             new() { Type = "origin", Value = "InteractiveOidc" }],
            DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None);

        (await store.GetAuthenticatedSessionAsync(id, CancellationToken.None))!
            .Origin.Should().Be(AdminAuthSessionOrigin.Unknown);
        (await store.GetInteractiveSessionForApprovalAsync(id, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task GetInteractiveSessionForApprovalAsync_SharedStoreFails_IsAnOutageNotAnAbsentSession()
    {
        var shared = Substitute.For<ISecurityTokenStore>();
        shared.FindAsync(default, default)
            .ReturnsForAnyArgs(Task.FromException<StoredSecurityToken?>(new InvalidOperationException("unavailable")));
        var store = CreateStore(shared);
        var id = await CreateInteractiveSessionAsync(store);

        var read = () => store.GetInteractiveSessionForApprovalAsync(id, CancellationToken.None);

        await read.Should().ThrowAsync<ServiceUnavailableException>();
    }

    [UnitTest]
    public async Task GetInteractiveSessionForApprovalAsync_IndependentStoreAndRevocation_UsesSharedAuthority()
    {
        var shared = new InMemorySecurityTokenStore();
        var first = CreateStore(shared);
        var second = CreateStore(shared);
        var id = await CreateInteractiveSessionAsync(first);

        var session = await second.GetInteractiveSessionForApprovalAsync(id, CancellationToken.None);

        session.Should().NotBeNull();
        session!.Origin.Should().Be(AdminAuthSessionOrigin.InteractiveOidc);
        await first.RemoveAuthenticatedSessionAsync(id, CancellationToken.None);
        (await second.GetInteractiveSessionForApprovalAsync(id, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task GetInteractiveSessionForApprovalAsync_ExpiredSession_ReturnsNull()
    {
        var store = CreateStore(new InMemorySecurityTokenStore());
        var id = await store.CreateAuthenticatedSessionAsync(
            new AdminAuthAuthenticatedSession
            {
                ProviderKey = "oidc",
                AccessToken = "access-token",
                IdToken = "id-token",
                Claims = [new() { Type = "sub", Value = "person" }],
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                Origin = AdminAuthSessionOrigin.InteractiveOidc
            }, CancellationToken.None);

        (await store.GetInteractiveSessionForApprovalAsync(id, CancellationToken.None)).Should().BeNull();
    }

    private static AdminAuthSessionStore CreateStore(ISecurityTokenStore shared) =>
        new(shared, NullLogger<AdminAuthSessionStore>.Instance);

    private static Task<string> CreateInteractiveSessionAsync(AdminAuthSessionStore store) =>
        store.CreateAuthenticatedSessionAsync(
            new AdminAuthAuthenticatedSession
            {
                ProviderKey = "oidc",
                AccessToken = "access-token",
                IdToken = "id-token",
                Claims = [new() { Type = "sub", Value = "person" }],
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                Origin = AdminAuthSessionOrigin.InteractiveOidc
            }, CancellationToken.None);
}
