// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Models;
using Honua.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// A Redis timeout talking to the API-key store must not throw and must not lock out
/// the bootstrap admin password.
/// </summary>
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
            BreakGlassPassword,
            "ApiKey",
            dependencies,
            NullLogger.Instance,
            CancellationToken.None);
        var unknown = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            "not-the-admin-password",
            "ApiKey",
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
        // The PostgreSQL registry used on multi-instance Redis-off hosts (#6487) fails with a
        // DbException, which must degrade exactly like a Redis outage.
        var store = UnavailableStore(new Npgsql.NpgsqlException("database unavailable"));

        var bootstrap = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            BreakGlassPassword, "ApiKey", Dependencies(store), NullLogger.Instance, CancellationToken.None);
        var verifier = new AdminPortalCredentialVerifier(
            Options.Create(new ApiKeyAuthenticationOptions { AdminPassword = BreakGlassPassword, EnvironmentName = "Test" }),
            adminApiKeyStore: store,
            logger: NullLogger<AdminPortalCredentialVerifier>.Instance);
        var admin = await verifier.VerifyAsync("admin", BreakGlassPassword, CancellationToken.None);

        Assert.Equal(ApiKeyRejection.None, bootstrap.Rejection);
        Assert.NotNull(admin);
    }

    [UnitTest]
    public async Task CompletedReaderOnce_ManagedKeyAuthenticates_OnHeaderAndPortalPaths()
    {
        var store = new ScriptedStore();
        var record = ManagedKey();
        store.Enqueue(new InvalidOperationException(RedisClientFault.CompletedReaderMessage));
        store.Enqueue(record);
        store.Enqueue(new InvalidOperationException(RedisClientFault.CompletedReaderMessage));
        store.Enqueue(record);

        var validation = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            "managed-key", "ApiKey", Dependencies(store), NullLogger.Instance, CancellationToken.None);
        var verifier = new AdminPortalCredentialVerifier(
            Options.Create(new ApiKeyAuthenticationOptions { AdminPassword = BreakGlassPassword, EnvironmentName = "Test" }),
            adminApiKeyStore: store,
            logger: NullLogger<AdminPortalCredentialVerifier>.Instance);
        var principal = await verifier.VerifyAsync("journey", "managed-key", CancellationToken.None);

        Assert.Equal(ApiKeyRejection.None, validation.Rejection);
        Assert.NotNull(validation.Success);
        Assert.True(validation.Success!.Succeeded);
        Assert.NotNull(principal);
        Assert.Equal(4, store.Calls);
    }

    [UnitTest]
    public async Task CompletedReaderPersists_UnknownKeyFailsClosed_BreakGlassStillAuthenticates()
    {
        var fault = new InvalidOperationException(RedisClientFault.CompletedReaderMessage);
        var store = UnavailableStore(fault);

        var bootstrap = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            BreakGlassPassword, "ApiKey", Dependencies(store), NullLogger.Instance, CancellationToken.None);
        var unknown = await ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
            "not-the-admin-password", "ApiKey", Dependencies(store), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(ApiKeyRejection.None, bootstrap.Rejection);
        Assert.Equal(ApiKeyRejection.CredentialStoreUnavailable, unknown.Rejection);
        Assert.Null(unknown.Success);
    }

    [UnitTest]
    public async Task OtherInvalidOperation_IsNotTreatedAsRedis()
    {
        var store = UnavailableStore(new InvalidOperationException("changed on every attempt"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiKeyAuthenticationHandler.ValidateApiKeyAsync(
                "managed-key", "ApiKey", Dependencies(store), NullLogger.Instance, CancellationToken.None));

        Assert.Equal("changed on every attempt", thrown.Message);
    }

    [UnitTest]
    public void CompletedReader_MapsToServiceUnavailable_OtherInvalidOperationStays500()
    {
        var completed = ExceptionMapper.Map(
            new InvalidOperationException(RedisClientFault.CompletedReaderMessage));
        var other = ExceptionMapper.Map(new InvalidOperationException("changed on every attempt"));
        var identityModel = ExceptionMapper.Map(new InvalidOperationException("IDX20803: unable to obtain configuration"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, completed.StatusCode);
        Assert.Equal(StatusCodes.Status500InternalServerError, other.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, identityModel.StatusCode);
    }

    private static AdminApiKeyValidationResult ManagedKey()
    {
        var now = DateTimeOffset.UtcNow;
        return new AdminApiKeyValidationResult(new AdminApiKeyRecord(
            Guid.NewGuid(),
            "journey",
            "hnua_testkey",
            new byte[32],
            ["admin:*"],
            now,
            now,
            ExpiresAt: null,
            LastUsedAt: null,
            RotatedAt: null,
            RevokedAt: null,
            CreatedBy: "test"));
    }

    /// <summary>
    /// Returns the scripted result for each <see cref="IAdminApiKeyStore.ValidateAsync"/> call.
    /// An exception factory faults that call. Other store methods are unused.
    /// </summary>
    private sealed class ScriptedStore : IAdminApiKeyStore
    {
        private readonly Queue<Func<AdminApiKeyValidationResult?>> _results = new();

        public int Calls { get; private set; }

        public void Enqueue(Exception failure)
            => _results.Enqueue(() => throw failure);

        public void Enqueue(AdminApiKeyValidationResult? result)
            => _results.Enqueue(() => result);

        public Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_results.Dequeue()());
        }

        public Task<IReadOnlyList<AdminApiKeyRecord>> ListAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AdminApiKeyCreateResult> CreateAsync(
            string name, IReadOnlyList<string> permissions, DateTimeOffset? expiresAt, string? createdBy, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AdminApiKeyCreateResult> CreateAsync(
            string name, IReadOnlyList<string> permissions, DateTimeOffset? expiresAt, string? createdBy, AdminApiKeyRestrictions? restrictions, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AdminApiKeyRecord?> UpdateRestrictionsAsync(Guid id, AdminApiKeyRestrictions restrictions, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static IAdminApiKeyStore UnavailableStore(Exception? failure = null)
    {
        var store = Substitute.For<IAdminApiKeyStore>();
        store.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure ?? new TimeoutException("redis timeout"));
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
