// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

// A timeout or database outage in the API-key store must not throw and must not lock out the
// bootstrap admin password.
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ApiKeyStoreOutageTests
{
    private const string BreakGlassPassword = "break-glass-password";

    [UnitTest]
    public async Task StoreTimeout_BreakGlassPasswordStillAuthenticates_UnknownKeyFailsClosed()
    {
        var store = UnavailableStore();
        var dependencies = Dependencies(store);

        var bootstrap = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            new ApiKeyPresentation(BreakGlassPassword, "ApiKey", null),
            dependencies,
            NullLogger.Instance,
            CancellationToken.None);
        var unknown = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            new ApiKeyPresentation("not-the-admin-password", "ApiKey", null),
            dependencies,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(ApiKeyRejection.None, bootstrap.Rejection);
        Assert.NotNull(bootstrap.Success);
        Assert.True(bootstrap.Success!.Succeeded);
        Assert.Equal(ApiKeyRejection.CredentialStoreUnavailable, unknown.Rejection);
        Assert.Null(unknown.Success);
    }

    [UnitTest]
    public async Task StoreTimeout_AdminPasswordStillVerifies_OtherUsernamesDoNotThrow()
    {
        var store = UnavailableStore();
        var verifier = new AdminPortalCredentialVerifier(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                AdminPassword = BreakGlassPassword,
                EnvironmentName = "Test",
            }),
            adminApiKeyStore: store,
            logger: NullLogger<AdminPortalCredentialVerifier>.Instance);

        var admin = await verifier.VerifyAsync("admin", BreakGlassPassword, CancellationToken.None);
        var other = await verifier.VerifyAsync("someone-else", "not-the-admin-password", CancellationToken.None);

        Assert.NotNull(admin);
        Assert.Equal("admin", admin!.PrincipalId);
        Assert.Null(other);
    }

    [UnitTest]
    public async Task DatabaseOutage_BreakGlassPasswordStillAuthenticates_OnHeaderAndPortalPaths()
    {
        // The PostgreSQL registry fails with a DbException, which must degrade exactly like a
        // store timeout.
        var store = UnavailableStore(new Npgsql.NpgsqlException("database unavailable"));

        var bootstrap = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            new ApiKeyPresentation(BreakGlassPassword, "ApiKey", null),
            Dependencies(store), NullLogger.Instance, CancellationToken.None);
        var verifier = new AdminPortalCredentialVerifier(
            Options.Create(new ApiKeyAuthenticationOptions { AdminPassword = BreakGlassPassword, EnvironmentName = "Test" }),
            adminApiKeyStore: store,
            logger: NullLogger<AdminPortalCredentialVerifier>.Instance);
        var admin = await verifier.VerifyAsync("admin", BreakGlassPassword, CancellationToken.None);

        Assert.Equal(ApiKeyRejection.None, bootstrap.Rejection);
        Assert.NotNull(admin);
    }

    [UnitTest]
    public async Task InvalidOperation_IsNotTreatedAsStoreOutage()
    {
        var store = UnavailableStore(new InvalidOperationException("changed on every attempt"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
                new ApiKeyPresentation("managed-key", "ApiKey", null),
                Dependencies(store), NullLogger.Instance, CancellationToken.None));

        Assert.Equal("changed on every attempt", thrown.Message);
    }

    [UnitTest]
    public void InvalidOperation_MapsTo500_IdentityModelConfigurationTo401()
    {
        var other = ExceptionMapper.Map(new InvalidOperationException("changed on every attempt"));
        var identityModel = ExceptionMapper.Map(new InvalidOperationException("IDX20803: unable to obtain configuration"));

        Assert.Equal(StatusCodes.Status500InternalServerError, other.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, identityModel.StatusCode);
    }

    private static IAdminApiKeyStore UnavailableStore(Exception? failure = null)
    {
        var store = Substitute.For<IAdminApiKeyStore>();
        store.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure ?? new TimeoutException("store timeout"));
        return store;
    }

    private static ApiKeyAuthenticationDependencies Dependencies(IAdminApiKeyStore store) => new(
        Options.Create(new ApiKeyAuthenticationOptions
        {
            AdminPassword = BreakGlassPassword,
            EnvironmentName = "Test",
        }),
        adminApiKeyStore: store);
}
