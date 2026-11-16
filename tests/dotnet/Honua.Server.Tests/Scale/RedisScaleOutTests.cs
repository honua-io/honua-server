// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Scale;

[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class RedisScaleOutTests
{
    private const string BaseUrlEnv = "HONUA_SCALE_TEST_BASE_URL";

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

    [ScaleTest(BaseUrlEnv)]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /rest/services")]
    [Endpoint("GET /odata/$metadata")]
    [Endpoint("GET /ogc/features")]
    public async Task LoadBalancer_EmitsInstanceHeaders_ForRestOgcAndODataPaths()
    {
        using var client = CreateClient();
        var paths = new[]
        {
            "/rest/services?f=json",
            "/ogc/features",
            "/odata/$metadata"
        };

        foreach (var path in paths)
        {
            using var response = await SendRequestAsync(client, path);
            ((int)response.StatusCode).Should().BeLessThan(500, $"upstream should respond for '{path}'");
            await response.Content.ReadAsByteArrayAsync();

            var hasInstanceHeader = response.Headers.TryGetValues("X-Instance-ID", out var instanceHeaders);
            hasInstanceHeader.Should().BeTrue($"scale-test nginx should emit X-Instance-ID for '{path}'");

            var instanceId = instanceHeaders?.FirstOrDefault();
            instanceId.Should().NotBeNullOrWhiteSpace($"X-Instance-ID should identify the upstream instance for '{path}'");
        }
    }

    [ScaleTest(BaseUrlEnv)]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /rest/services")]
    [Endpoint("GET /odata/$metadata")]
    public async Task LoadBalancer_DistributesRequestsAcrossInstances_ForRestAndOData()
    {
        using var client = CreateClient();
        var restInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var odataInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < 15; i++)
        {
            using (var restResponse = await SendRequestAsync(client, "/rest/services?f=json"))
            {
                ((int)restResponse.StatusCode).Should().BeLessThan(500);
                await restResponse.Content.ReadAsByteArrayAsync();
                restResponse.Headers.TryGetValues("X-Instance-ID", out var headers).Should().BeTrue();
                restInstances.Add(headers!.First());
            }

            using (var odataResponse = await SendRequestAsync(client, "/odata/$metadata"))
            {
                ((int)odataResponse.StatusCode).Should().BeLessThan(500);
                await odataResponse.Content.ReadAsByteArrayAsync();
                odataResponse.Headers.TryGetValues("X-Instance-ID", out var headers).Should().BeTrue();
                odataInstances.Add(headers!.First());
            }
        }

        restInstances.Should().HaveCountGreaterOrEqualTo(2, "REST requests should route to multiple Honua instances");
        odataInstances.Should().HaveCountGreaterOrEqualTo(2, "OData requests should route to multiple Honua instances");
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
