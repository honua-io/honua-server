// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Server.Features.Infrastructure.RateLimiting;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Middleware;

public sealed class RateLimitingMiddlewareTests
{
    [UnitTest]
    public async Task InvokeAsync_WithSpoofedForwardedHeaders_UsesRemoteIpAddressForRateLimitKey()
    {
        var middleware = CreateMiddleware();

        var firstContext = CreateContext("198.51.100.10", "203.0.113.1");
        firstContext.Request.Headers["X-Forwarded-For"] = "1.1.1.1";
        await middleware.InvokeAsync(firstContext);

        var secondContext = CreateContext("198.51.100.10", "203.0.113.1");
        secondContext.Request.Headers["X-Forwarded-For"] = "8.8.8.8";
        await middleware.InvokeAsync(secondContext);

        firstContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        secondContext.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    [UnitTest]
    public async Task InvokeAsync_WithIpv4MappedIpv6RemoteAddress_NormalizesToSingleRateLimitKey()
    {
        var middleware = CreateMiddleware();

        var firstContext = CreateContext("::ffff:203.0.113.20");
        await middleware.InvokeAsync(firstContext);

        var secondContext = CreateContext("203.0.113.20");
        await middleware.InvokeAsync(secondContext);

        firstContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        secondContext.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    [UnitTest]
    public async Task InvokeAsync_WhenRateLimitExceeded_ReturnsExpectedJsonContract()
    {
        var middleware = CreateMiddleware();

        var firstContext = CreateContext("198.51.100.30");
        await middleware.InvokeAsync(firstContext);

        var secondContext = CreateContext("198.51.100.30");
        await middleware.InvokeAsync(secondContext);

        secondContext.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        secondContext.Response.Body.Position = 0;

        using var responseDocument = await JsonDocument.ParseAsync(secondContext.Response.Body);
        responseDocument.RootElement.GetProperty("error").GetString().Should().Be("rate_limit_exceeded");
        responseDocument.RootElement.GetProperty("message").GetString().Should().Be("Too many requests. Please try again later.");
        responseDocument.RootElement.GetProperty("details").GetProperty("limit").GetInt32().Should().Be(1);
        responseDocument.RootElement.GetProperty("details").TryGetProperty("window_reset", out _).Should().BeTrue();
    }

    private static RateLimitingMiddleware CreateMiddleware()
    {
        var policyStore = Substitute.For<IRateLimitPolicyStore>();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        return new RateLimitingMiddleware(
            next: context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            policyStore,
            cache,
            redis: null,
            Options.Create(new RateLimitingOptions
            {
                Enabled = true,
                GlobalRequestsPerMinute = 1
            }),
            NullLogger<RateLimitingMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(string remoteIp, string? localIp = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/rest/services/test/FeatureServer/0/query";
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (!string.IsNullOrWhiteSpace(localIp))
        {
            context.Connection.LocalIpAddress = IPAddress.Parse(localIp);
        }

        return context;
    }
}
