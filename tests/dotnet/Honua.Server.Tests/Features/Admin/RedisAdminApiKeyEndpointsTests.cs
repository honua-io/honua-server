// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Exercises the durable API-key registry through real HTTP authentication.</summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApiKeyManagement)]
public sealed class RedisAdminApiKeyEndpointsTests(RedisFixture redis) : IAsyncLifetime
{
    private const string BootstrapKey = "redis-api-key-http-bootstrap";
    private const string RegistryPrefix = "honua:auth:admin-api-key:";
    private readonly List<Guid> _createdIds = [];
    private ConnectionMultiplexer _connection = null!;
    private WebAppFixture _fixture = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", BootstrapKey);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAdminApiKeyStore>();
                services.AddSingleton<IAdminApiKeyStore>(new RedisAdminApiKeyStore(_connection));
            });
        await _fixture.InitializeAsync();
        _admin = CreateClient(BootstrapKey);
    }

    public async Task DisposeAsync()
    {
        _admin?.Dispose();
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }

        if (_connection is not null)
        {
            var database = _connection.GetDatabase();
            foreach (var id in _createdIds)
            {
                await database.KeyDeleteAsync($"{RegistryPrefix}{id:D}");
                await database.SetRemoveAsync(RegistryPrefix + "ids", id.ToString("D"));
            }
            await _connection.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task Lifecycle_WithRedisRegistry_RotatesAndRevokesAuthentication()
    {
        using var body = new StringContent("{\"name\":\"redis-lifecycle\",\"permissions\":[\"admin:*\"]}", Encoding.UTF8, "application/json");
        using var create = await _admin.PostAsync("/api/v1/admin/api-keys", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var data = created.RootElement.GetProperty("data");
        var id = data.GetProperty("apiKey").GetProperty("id").GetGuid();
        _createdIds.Add(id);
        var originalKey = data.GetProperty("key").GetString()!;
        using var originalClient = CreateClient(originalKey);
        using var list = await originalClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(originalKey, await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var store = _fixture.GetService<IAdminApiKeyStore>();
        Assert.NotNull((await store.GetAsync(id, CancellationToken.None))!.LastUsedAt);

        using var rotate = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/rotate", null);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        using var rotated = JsonDocument.Parse(await rotate.Content.ReadAsStringAsync());
        var rotatedKey = rotated.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        Assert.NotEqual(originalKey, rotatedKey);
        using var rotatedClient = CreateClient(rotatedKey);
        using var oldResponse = await originalClient.GetAsync("/api/v1/admin/api-keys");
        using var newResponse = await rotatedClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);

        using var revoke = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using var revokedResponse = await rotatedClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task LegacyRedisRecord_PreservesCompareAndSetAndExpiry()
    {
        var legacyStore = new InMemoryAdminApiKeyStore();
        var issued = await legacyStore.CreateAsync("legacy-wire-format", ["admin:*"], null, null, CancellationToken.None);
        var id = issued.Record.Id;
        _createdIds.Add(id);
        var database = _connection.GetDatabase();
        // Deliberately use the pre-fix serializer to seed an existing deployment's bytes.
        await database.StringSetAsync($"{RegistryPrefix}{id:D}", JsonSerializer.Serialize(issued.Record));
        await database.SetAddAsync(RegistryPrefix + "ids", id.ToString("D"));
        using var client = CreateClient(issued.Key);
        using var valid = await client.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var store = _fixture.GetService<IAdminApiKeyStore>();
        Assert.NotNull((await store.GetAsync(id, CancellationToken.None))!.LastUsedAt);

        var expired = issued.Record with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await database.StringSetAsync($"{RegistryPrefix}{id:D}", JsonSerializer.Serialize(expired));
        using var denied = await client.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    private HttpClient CreateClient(string key) =>
        _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
}
