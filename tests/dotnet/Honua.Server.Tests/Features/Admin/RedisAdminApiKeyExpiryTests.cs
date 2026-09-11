// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Checks real Redis eviction and authentication-candidate indexing.</summary>
/// <remarks>These store-level integration tests issue no HTTP requests and claim no endpoint coverage.</remarks>
[Collection("Redis")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.ApiKeyManagement)]
public sealed class RedisAdminApiKeyExpiryTests(RedisFixture redis)
{
    [IntegrationTheory]
    [InlineData("created")]
    [InlineData("validated")]
    [InlineData("revoked")]
    public async Task ApprovedCredential_AfterExpiry_RemovesMetadata(string operation)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = new RedisAdminApiKeyStore(connection);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
        var created = await store.CreateAsync("approval-expiry-" + operation,
            AdminApiKeyPermission.CreateApprovedOperationGrants("PUT", "/api/v1/admin/metadata/layers/1/filter", "tenant-a"),
            expiresAt, "requester", CancellationToken.None);
        try
        {
            Assert.NotNull(await store.GetAsync(created.Record.Id, CancellationToken.None));
            if (operation == "validated")
            {
                Assert.NotNull(await store.ValidateAsync(created.Key, CancellationToken.None));
            }
            else if (operation == "revoked")
            {
                Assert.NotNull(await store.RevokeAsync(created.Record.Id, CancellationToken.None));
            }

            var delay = expiresAt.AddMilliseconds(250) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }
            Assert.Null(await store.ValidateAsync(created.Key, CancellationToken.None));
            Assert.Null(await store.GetAsync(created.Record.Id, CancellationToken.None));
        }
        finally
        {
            var database = connection.GetDatabase();
            await database.KeyDeleteAsync($"honua:auth:admin-api-key:{created.Record.Id:D}");
            await database.SetRemoveAsync("honua:auth:admin-api-key:ids", created.Record.Id.ToString("D"));
            await database.SetRemoveAsync("honua:auth:admin-api-key:active-ids", created.Record.Id.ToString("D"));
            await database.SetRemoveAsync("honua:auth:admin-api-key:seen-ids", created.Record.Id.ToString("D"));
        }
    }

    [IntegrationTest]
    public async Task LegacyRegistry_WithoutCandidateIndex_StillAuthenticatesAndSeedsIt()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        var store = new RedisAdminApiKeyStore(connection);
        var created = await store.CreateAsync("legacy-index-upgrade", ["admin:*"],
            DateTimeOffset.UtcNow.AddHours(1), "operator", CancellationToken.None);
        try
        {
            // A registry written before the candidate index existed has records and ids but
            // neither the active index nor the reconciliation ledger. Authentication must not
            // fail closed while both are absent.
            await database.KeyDeleteAsync("honua:auth:admin-api-key:active-ids");
            await database.KeyDeleteAsync("honua:auth:admin-api-key:seen-ids");

            var validated = await store.ValidateAsync(created.Key, CancellationToken.None);

            Assert.NotNull(validated);
            Assert.Equal(created.Record.Id, validated.Record.Id);
            Assert.True(await database.SetContainsAsync("honua:auth:admin-api-key:active-ids", created.Record.Id.ToString("D")),
                "reconciliation must adopt the pre-existing valid record as an authentication candidate");
            Assert.NotNull(await store.ValidateAsync(created.Key, CancellationToken.None));
        }
        finally
        {
            await database.KeyDeleteAsync($"honua:auth:admin-api-key:{created.Record.Id:D}");
            await database.SetRemoveAsync("honua:auth:admin-api-key:ids", created.Record.Id.ToString("D"));
            await database.SetRemoveAsync("honua:auth:admin-api-key:active-ids", created.Record.Id.ToString("D"));
            await database.SetRemoveAsync("honua:auth:admin-api-key:seen-ids", created.Record.Id.ToString("D"));
        }
    }

    [IntegrationTest]
    public async Task Rotate_AfterExpiry_ReturnsNullRatherThanUnusableMaterial()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        var store = new RedisAdminApiKeyStore(connection);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
        var created = await store.CreateAsync("expired-rotate-store", ["admin:*"], expiresAt, "operator", CancellationToken.None);
        try
        {
            var delay = expiresAt.AddMilliseconds(250) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            Assert.Null(await store.RotateAsync(created.Record.Id, CancellationToken.None));

            // Rejecting the rotation must not disturb the retained metadata (#4603).
            var retained = await store.GetAsync(created.Record.Id, CancellationToken.None);
            Assert.NotNull(retained);
            Assert.Null(retained.RotatedAt);
            Assert.Equal(created.Record.KeyHash, retained.KeyHash);
        }
        finally
        {
            await database.KeyDeleteAsync($"honua:auth:admin-api-key:{created.Record.Id:D}");
            await database.SetRemoveAsync("honua:auth:admin-api-key:ids", created.Record.Id.ToString("D"));
            await database.SetRemoveAsync("honua:auth:admin-api-key:active-ids", created.Record.Id.ToString("D"));
            await database.SetRemoveAsync("honua:auth:admin-api-key:seen-ids", created.Record.Id.ToString("D"));
        }
    }
}
