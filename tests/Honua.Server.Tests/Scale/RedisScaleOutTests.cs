// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Scale;

/// <summary>
/// Scale-out validation for multi-node deployments using Redis and the nginx load balancer.
/// </summary>
[Protocol(Protocols.OgcApiFeatures)]
public sealed class RedisScaleOutTests
{
    private const string BaseUrlEnv = "HONUA_SCALE_TEST_BASE_URL";
    private const string RedisEnv = "HONUA_SCALE_TEST_REDIS";
    private const string OutputCachePrefix = "honua:outputcache:";

    [ScaleTest(BaseUrlEnv)]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /ogc/features")]
    public async Task LoadBalancer_DistributesRequestsAcrossInstances()
    {
        using var client = CreateClient();
        var instanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < 15; i++)
        {
            using var response = await SendRequestAsync(client, "/ogc/features");
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync();

            var hasInstanceHeader = response.Headers.TryGetValues("X-Instance-ID", out var instanceHeaders);
            hasInstanceHeader.Should().BeTrue("scale-test nginx should emit X-Instance-ID for load balancing validation");

            var instanceId = instanceHeaders?.FirstOrDefault();
            instanceId.Should().NotBeNullOrWhiteSpace("X-Instance-ID should identify the upstream instance");
            instanceIds.Add(instanceId!);
        }

        instanceIds.Should().HaveCountGreaterOrEqualTo(2, "scale tests should exercise multiple instances via nginx");
    }

    [ScaleTest(BaseUrlEnv, RedisEnv)]
    [Operation(Operations.Cache)]
    [Endpoint("GET /ogc/features")]
    public async Task OutputCache_WritesToRedisStore()
    {
        var redisConnection = GetRequiredEnv(RedisEnv);
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var server = GetServer(multiplexer);
        var database = multiplexer.GetDatabase();

        await ClearKeysAsync(server, database, $"{OutputCachePrefix}*");

        using var client = CreateClient();
        using var response = await SendRequestAsync(client, "/ogc/features");
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsByteArrayAsync();

        var keys = server.Keys(pattern: $"{OutputCachePrefix}*").ToArray();
        keys.Should().NotBeEmpty("output cache entries should be persisted in Redis");
    }

    private static HttpClient CreateClient()
    {
        var baseUrl = GetRequiredEnv(BaseUrlEnv).TrimEnd('/');
        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ConnectionClose = true;
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task ClearKeysAsync(IServer server, IDatabase database, string pattern)
    {
        var keys = server.Keys(pattern: pattern).ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        await database.KeyDeleteAsync(keys);
    }

    private static IServer GetServer(ConnectionMultiplexer multiplexer)
    {
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException("Redis connection string did not provide any endpoints.");
        }

        return multiplexer.GetServer(endpoints[0]);
    }

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for scale tests.");
        }

        return value;
    }
}
