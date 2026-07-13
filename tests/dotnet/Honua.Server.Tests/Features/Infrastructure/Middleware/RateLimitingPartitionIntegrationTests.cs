// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Infrastructure.RateLimiting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Middleware;

/// <summary>
/// End-to-end tests for the rate-limiting middleware running inside a real endpoint-routing
/// pipeline. These prove per-endpoint partitioning of the fixed-window counter (issue #2779):
/// traffic to one rate-limited endpoint from a single tenant+IP must not consume another
/// endpoint's allowance, and OIDC/auth endpoints stay reachable under ordinary Console/BFF
/// traffic from a single egress IP.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class RateLimitingPartitionIntegrationTests
{
    private const string ConsolePagePath = "/console/pages";
    private const string AuthorizeUrlTemplate = "/api/v1/admin/auth/providers/{providerKey}/authorize-url";
    private const string AuthorizeUrlPath = "/api/v1/admin/auth/providers/entra/authorize-url";

    [IntegrationTest]
    [Operation(Operations.Security)]
    public async Task MixedConsoleTraffic_FromSingleEgressIp_KeepsAuthorizeUrlReachable()
    {
        await using var app = await CreateStartedAppAsync("203.0.113.201");
        var client = app.GetTestClient();

        // Ordinary Console page traffic from one egress IP, well beyond the auth endpoint's 5/min
        // limit. Before #2779 this shared the same tenant+IP counter as authorize-url.
        for (var i = 0; i < 50; i++)
        {
            var page = await client.GetAsync(ConsolePagePath);
            page.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The OIDC authorize-url endpoint must remain reachable on its first request from that IP.
        var authorize = await client.PostAsync(AuthorizeUrlPath, content: null);
        authorize.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    public async Task AuthorizeUrl_ExceedingItsOwnPerEndpointLimit_Returns429WithRetryAfter()
    {
        await using var app = await CreateStartedAppAsync("203.0.113.202");
        var client = app.GetTestClient();

        HttpResponseMessage? lastResponse = null;
        for (var i = 0; i < 6; i++)
        {
            lastResponse = await client.PostAsync(AuthorizeUrlPath, content: null);
        }

        // Requests 1-5 are allowed; the 6th exceeds the endpoint's own 5/min bucket.
        lastResponse!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        lastResponse.Headers.TryGetValues("Retry-After", out var retryAfter).Should().BeTrue(
            "a 429 must advise clients when to retry (#355 contract preserved)");
        int.Parse(retryAfter!.Single(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(0);
    }

    private static async Task<WebApplication> CreateStartedAppAsync(string clientIp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();

        var policyStore = Substitute.For<IRateLimitPolicyStore>();
        var options = Options.Create(new RateLimitingOptions
        {
            Enabled = true,
            GlobalRequestsPerMinute = 1000,
        });

        var app = builder.Build();

        // WebApplication inserts UseRouting at the start of the pipeline, so both middlewares below
        // run after routing and can resolve per-endpoint metadata via context.GetEndpoint().

        // Force a deterministic client IP; TestServer does not set one by default.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
            await next();
        });

        // Construct the middleware directly (redis: null -> process-local fixed-window path) so the
        // test does not require a Redis dependency.
        app.Use(next =>
        {
            var middleware = new RateLimitingMiddleware(
                next,
                policyStore,
                options,
                NullLogger<RateLimitingMiddleware>.Instance,
                redis: null);
            return context => middleware.InvokeAsync(context);
        });

        app.MapGet(ConsolePagePath, () => Results.Ok())
            .WithMetadata(new RateLimitAttribute(1000));
        app.MapPost(AuthorizeUrlTemplate, (string providerKey) => Results.Ok())
            .WithMetadata(new RateLimitAttribute(5));

        await app.StartAsync();
        return app;
    }
}
