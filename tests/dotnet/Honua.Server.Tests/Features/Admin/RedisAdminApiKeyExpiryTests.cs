// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Checks real Redis eviction for internal approval credentials.</summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
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
        }
    }
}
