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
                await database.SetRemoveAsync(RegistryPrefix + "active-ids", id.ToString("D"));
                await database.SetRemoveAsync(RegistryPrefix + "seen-ids", id.ToString("D"));
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

    [IntegrationTheory]
    [InlineData("created", "expired")]
    [InlineData("validated", "expired")]
    [InlineData("rotated", "expired")]
    [InlineData("revoked", "revoked")]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task Expiry_WithRedisRegistry_DeniesCredentialAndRetainsMetadata(string operation, string expectedStatus)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
        using var body = new StringContent(JsonSerializer.Serialize(new
        {
            name = "redis-expiry-" + operation,
            permissions = new[] { "admin:*" },
            expiresAt,
        }), Encoding.UTF8, "application/json");
        using var create = await _admin.PostAsync("/api/v1/admin/api-keys", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var data = created.RootElement.GetProperty("data");
        var id = data.GetProperty("apiKey").GetProperty("id").GetGuid();
        _createdIds.Add(id);
        var credential = data.GetProperty("key").GetString()!;

        if (operation == "validated")
        {
            using var client = CreateClient(credential);
            using var allowed = await client.GetAsync("/api/v1/admin/api-keys");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        else if (operation == "rotated")
        {
            using var rotate = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/rotate", null);
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
            using var rotated = JsonDocument.Parse(await rotate.Content.ReadAsStringAsync());
            credential = rotated.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        }
        else if (operation == "revoked")
        {
            using var revoke = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/revoke", null);
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        // Use real time: advancing only a test clock cannot reproduce Redis TTL deletion.
        var delay = expiresAt.AddMilliseconds(250) - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        using var expiredClient = CreateClient(credential);
        using var denied = await expiredClient.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var effective = await _admin.GetAsync($"/api/v1/admin/api-keys/{id}/effective-permissions");
        Assert.Equal(HttpStatusCode.OK, effective.StatusCode);
        using var metadata = JsonDocument.Parse(await effective.Content.ReadAsStringAsync());
        Assert.Equal(expectedStatus, metadata.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.False(metadata.RootElement.GetProperty("data").GetProperty("canAuthenticate").GetBoolean());
        using var list = await _admin.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var record = Assert.Single(listed.RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        Assert.Equal(expectedStatus, record.GetProperty("status").GetString());
        Assert.Equal(expiresAt, record.GetProperty("expiresAt").GetDateTimeOffset());

        using var finalRevoke = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, finalRevoke.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    public async Task Rotate_AfterExpiry_IsRejectedWithoutIssuingDeadCredential()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
        var (id, _) = await CreateKeyAsync("redis-expired-rotate", expiresAt);

        // Use real time: advancing only a test clock cannot reproduce Redis TTL behaviour.
        await WaitPastAsync(expiresAt);

        using var rotate = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/rotate", null);
        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);

        // Retention (#4603) still holds: the record stays inspectable and stays expired,
        // rather than being reported as a successful rotation back to "active".
        using var effective = await _admin.GetAsync($"/api/v1/admin/api-keys/{id}/effective-permissions");
        Assert.Equal(HttpStatusCode.OK, effective.StatusCode);
        using var metadata = JsonDocument.Parse(await effective.Content.ReadAsStringAsync());
        Assert.Equal("expired", metadata.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.False(metadata.RootElement.GetProperty("data").GetProperty("canAuthenticate").GetBoolean());

        using var list = await _admin.GetAsync("/api/v1/admin/api-keys");
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var record = Assert.Single(listed.RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        Assert.Equal("expired", record.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("rotatedAt").ValueKind);
        Assert.Equal(expiresAt, record.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task RetainedMetadata_AfterExpiryOrRevoke_LeavesTheAuthenticationCandidateSet()
    {
        var database = _connection.GetDatabase();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
        var (expiredId, _) = await CreateKeyAsync("redis-hotpath-expired", expiresAt);
        var (revokedId, _) = await CreateKeyAsync("redis-hotpath-revoked", DateTimeOffset.UtcNow.AddHours(1));
        var (liveId, liveKey) = await CreateKeyAsync("redis-hotpath-live", DateTimeOffset.UtcNow.AddHours(1));

        using (var revoke = await _admin.PostAsync($"/api/v1/admin/api-keys/{revokedId}/revoke", null))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        await WaitPastAsync(expiresAt);

        // Authenticating runs the candidate scan, which prunes what can never authenticate again.
        using var live = CreateClient(liveKey);
        using var authenticated = await live.GetAsync("/api/v1/admin/api-keys");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

        var activeIds = RegistryPrefix + "active-ids";
        Assert.False(await database.SetContainsAsync(activeIds, expiredId.ToString("D")),
            "an expired record must not stay on the per-request authentication path");
        Assert.False(await database.SetContainsAsync(activeIds, revokedId.ToString("D")),
            "a revoked record must not stay on the per-request authentication path");
        Assert.True(await database.SetContainsAsync(activeIds, liveId.ToString("D")),
            "a live record must remain an authentication candidate");

        // Pruning the candidate set must not evict the retained metadata (#4603).
        Assert.True(await database.SetContainsAsync(RegistryPrefix + "ids", expiredId.ToString("D")));
        using var list = await _admin.GetAsync("/api/v1/admin/api-keys");
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var record = Assert.Single(listed.RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == expiredId);
        Assert.Equal("expired", record.GetProperty("status").GetString());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    [Endpoint("GET /api/v1/admin/api-keys/{id}/effective-permissions")]
    public async Task ScopedAndWrongKeys_WithRedisRegistry_AreHeldToTheSharedAdminPolicy()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var (scopedId, scopedKey) = await CreateKeyAsync("redis-scoped-compat", expiresAt, ["read:arcgis_compat_scoped"]);
        var (_, readerKey) = await CreateKeyAsync("redis-scoped-admin-read", expiresAt, ["admin:read"]);

        // The scope round-trips through the generated Redis representation.
        using (var effective = await _admin.GetAsync($"/api/v1/admin/api-keys/{scopedId}/effective-permissions"))
        {
            Assert.Equal(HttpStatusCode.OK, effective.StatusCode);
            using var metadata = JsonDocument.Parse(await effective.Content.ReadAsStringAsync());
            var data = metadata.RootElement.GetProperty("data");
            Assert.Equal("active", data.GetProperty("status").GetString());
            Assert.True(data.GetProperty("canAuthenticate").GetBoolean());
            Assert.Equal("read:arcgis_compat_scoped", Assert.Single(data.GetProperty("permissions").EnumerateArray()).GetString());
        }

        // A non-admin scope authenticates (its usage is written back) but the admin policy refuses it.
        using var scoped = CreateClient(scopedKey);
        using (var refused = await scoped.GetAsync("/api/v1/admin/api-keys"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var store = _fixture.GetService<IAdminApiKeyStore>();
        Assert.NotNull((await store.GetAsync(scopedId, CancellationToken.None))!.LastUsedAt);

        // admin:read reads the registry but cannot mint, rotate or revoke through it.
        using var reader = CreateClient(readerKey);
        using (var read = await reader.GetAsync("/api/v1/admin/api-keys"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using var escalation = new StringContent("{\"name\":\"redis-scoped-escalation\",\"permissions\":[\"admin:*\"]}", Encoding.UTF8, "application/json");
        using (var mint = await reader.PostAsync("/api/v1/admin/api-keys", escalation))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mint.StatusCode);
        }

        using (var rotate = await reader.PostAsync($"/api/v1/admin/api-keys/{scopedId}/rotate", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rotate.StatusCode);
        }

        using (var revoke = await reader.PostAsync($"/api/v1/admin/api-keys/{scopedId}/revoke", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        }

        // Wrong keys: well-formed but never issued, and an issued key missing its last character.
        using var unissued = CreateClient(InMemoryAdminApiKeyStore.GenerateForDurableStore());
        using (var denied = await unissued.GetAsync("/api/v1/admin/api-keys"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        using var truncated = CreateClient(readerKey[..^1]);
        using (var denied = await truncated.GetAsync("/api/v1/admin/api-keys"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        // None of the refused writes touched the scoped record.
        var after = (await store.GetAsync(scopedId, CancellationToken.None))!;
        Assert.Null(after.RotatedAt);
        Assert.Null(after.RevokedAt);
        Assert.Equal(expiresAt, after.ExpiresAt);
        Assert.Equal(["read:arcgis_compat_scoped"], after.Permissions);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task RotateAndRevoke_UnderConcurrentUse_TakeEffectWithoutDenyingValidRequests()
    {
        var (id, originalKey) = await CreateKeyAsync("redis-concurrent-use", DateTimeOffset.UtcNow.AddHours(1));
        using var original = CreateClient(originalKey);

        // Concurrent requests with one key race their LastUsedAt compare-and-set; none may be denied.
        var burst = await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            using var response = await original.GetAsync("/api/v1/admin/api-keys");
            return response.StatusCode;
        }));
        Assert.All(burst, status => Assert.Equal(HttpStatusCode.OK, status));

        var (rotatedKey, rotationTraffic) = await WithTrafficAsync(original, async () =>
        {
            using var rotate = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/rotate", null);
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
            using var rotated = JsonDocument.Parse(await rotate.Content.ReadAsStringAsync());
            return rotated.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        });
        Assert.All(rotationTraffic, status => Assert.Contains(status, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized }));
        using (var stale = await original.GetAsync("/api/v1/admin/api-keys"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }

        using var current = CreateClient(rotatedKey);
        using (var accepted = await current.GetAsync("/api/v1/admin/api-keys"))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // A revocation issued while the live key is in constant use must win over its usage writes.
        var (revokeStatus, revocationTraffic) = await WithTrafficAsync(current, async () =>
        {
            using var revoke = await _admin.PostAsync($"/api/v1/admin/api-keys/{id}/revoke", null);
            return revoke.StatusCode;
        });
        Assert.Equal(HttpStatusCode.OK, revokeStatus);
        Assert.All(revocationTraffic, status => Assert.Contains(status, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized }));
        using (var denied = await current.GetAsync("/api/v1/admin/api-keys"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        Assert.NotNull((await _fixture.GetService<IAdminApiKeyStore>().GetAsync(id, CancellationToken.None))!.RevokedAt);
    }

    private static async Task<(T Result, HttpStatusCode[] Traffic)> WithTrafficAsync<T>(HttpClient client, Func<Task<T>> action)
    {
        using var stop = new CancellationTokenSource();
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<HttpStatusCode>();
        var loops = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var response = await client.GetAsync("/api/v1/admin/api-keys");
                statuses.Enqueue(response.StatusCode);
            }
        })).ToArray();

        while (statuses.IsEmpty)
        {
            await Task.Delay(10);
        }

        var result = await action();
        await Task.Delay(100);
        await stop.CancelAsync();
        await Task.WhenAll(loops);
        return (result, statuses.ToArray());
    }

    private static async Task WaitPastAsync(DateTimeOffset expiresAt)
    {
        var delay = expiresAt.AddMilliseconds(250) - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }
    }

    private async Task<(Guid Id, string Key)> CreateKeyAsync(string name, DateTimeOffset expiresAt, string[]? permissions = null)
    {
        using var body = new StringContent(JsonSerializer.Serialize(new
        {
            name,
            permissions = permissions ?? ["admin:*"],
            expiresAt,
        }), Encoding.UTF8, "application/json");
        using var create = await _admin.PostAsync("/api/v1/admin/api-keys", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var data = created.RootElement.GetProperty("data");
        var id = data.GetProperty("apiKey").GetProperty("id").GetGuid();
        _createdIds.Add(id);
        return (id, data.GetProperty("key").GetString()!);
    }

    private HttpClient CreateClient(string key) =>
        _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
}
