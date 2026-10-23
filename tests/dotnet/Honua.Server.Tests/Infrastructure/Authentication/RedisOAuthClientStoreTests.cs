// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>Exercises the durable OAuth registry against real Redis.</summary>
/// <remarks>Store-level integration tests issue no HTTP requests and claim no endpoint coverage.</remarks>
[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class RedisOAuthClientStoreTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_RoundTripsAcrossInstances_AndDeletesClient(bool publicClient)
    {
        // The production reflection-disabled probe supplies an isolated Redis
        // externally because Docker.DotNet itself uses reflection-based JSON.
        var externalRedis = Environment.GetEnvironmentVariable("HONUA_TEST_OAUTH_REDIS_CONNECTION");
        externalRedis = string.IsNullOrWhiteSpace(externalRedis) ? null : externalRedis;
        await using var container = externalRedis is null
            ? new RedisBuilder("redis:7.2-alpine").Build()
            : null;
        if (container is not null)
        {
            await container.StartAsync();
        }
        var configuration = ConfigurationOptions.Parse(externalRedis ?? container!.GetConnectionString());
        configuration.DefaultDatabase = publicClient ? 1 : 0;
        using var redis = await ConnectionMultiplexer.ConnectAsync(configuration);
        var writer = new RedisOAuthClientStore(redis);
        var reader = new RedisOAuthClientStore(redis);
        var registration = new OAuthClientRegistration(
            "redis-regression", publicClient ? OAuthClientType.Public : OAuthClientType.Confidential,
            ["client_credentials"], ["https://example.org/callback"], ["features:read"],
            DateTimeOffset.UtcNow.AddHours(1), "test");

        var created = await writer.CreateAsync(registration, CancellationToken.None);
        var loaded = await reader.GetAsync(created.Record.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(created.Record.ClientId, loaded.ClientId);
        Assert.Equal(registration.ClientType, loaded.ClientType);
        Assert.Equal(registration.AllowedScopes, loaded.AllowedScopes);
        Assert.Equal(registration.AllowedGrantTypes, loaded.AllowedGrantTypes);
        Assert.Equal(registration.RedirectUris, loaded.RedirectUris);
        Assert.Equal(created.Record.ExpiresAt, loaded.ExpiresAt);
        Assert.Equal(created.Record.Id, Assert.Single(await reader.ListAsync(CancellationToken.None)).Id);

        var database = redis.GetDatabase();
        var persisted = (string)(await database.StringGetAsync($"honua:auth:oauth-client:{created.Record.Id:D}"))!;
        using var document = JsonDocument.Parse(persisted);
        Assert.Equal(created.Record.Id, document.RootElement.GetProperty("Id").GetGuid());
        Assert.Equal((int)registration.ClientType, document.RootElement.GetProperty("ClientType").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("LastUsedAt").ValueKind);

        Assert.Null(await reader.ValidateSecretAsync(loaded.ClientId, "wrong-secret", CancellationToken.None));
        if (publicClient)
        {
            Assert.Null(created.Secret);
            Assert.Null(created.Record.SecretHash);
            Assert.Null(loaded.SecretHash);
        }
        else
        {
            Assert.NotNull(created.Secret);
            Assert.False(persisted.Contains(created.Secret, StringComparison.Ordinal));
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(created.Secret)), loaded.SecretHash);
            var validated = await reader.ValidateSecretAsync(loaded.ClientId, created.Secret, CancellationToken.None);
            Assert.NotNull(validated);
            Assert.NotNull(validated.LastUsedAt);
            var updated = await writer.GetAsync(loaded.Id, CancellationToken.None);
            Assert.NotNull(updated);
            Assert.Equal(validated.LastUsedAt, updated.LastUsedAt);
        }

        Assert.NotNull(await writer.DeleteAsync(loaded.Id, CancellationToken.None));
        Assert.Null(await reader.GetAsync(loaded.Id, CancellationToken.None));
        Assert.Empty(await reader.ListAsync(CancellationToken.None));
        Assert.Null(await reader.ValidateSecretAsync(loaded.ClientId, created.Secret ?? "wrong-secret", CancellationToken.None));
    }
}
