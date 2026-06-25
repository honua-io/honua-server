// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Infrastructure.RateLimiting;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    [UnitTest]
    public async Task InvokeAsync_WhenRateLimitExceeded_EmitsRetryAfterHeader()
    {
        var middleware = CreateMiddleware();

        var firstContext = CreateContext("198.51.100.40");
        await middleware.InvokeAsync(firstContext);

        var secondContext = CreateContext("198.51.100.40");
        await middleware.InvokeAsync(secondContext);

        secondContext.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);

        var retryAfter = secondContext.Response.Headers.RetryAfter.ToString();
        retryAfter.Should().NotBeNullOrEmpty("a 429 must advise clients when to retry (RFC 9110)");
        int.TryParse(retryAfter, out var seconds).Should().BeTrue("Retry-After is emitted as a delay in seconds");
        seconds.Should().BeInRange(0, 60);
    }

    [UnitTest]
    public async Task InvokeAsync_UnderLimit_AllowsRequestThrough()
    {
        // Limit of 5; a single request is comfortably under the limit and must pass.
        var middleware = CreateMiddleware(limit: 5);

        var context = CreateContext("198.51.100.50");
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers["X-RateLimit-Remaining"].ToString().Should().Be("4");
    }

    [UnitTest]
    public async Task InvokeAsync_WhenDisabled_DoesNotRateLimitEvenWhenOverLimit()
    {
        // Disabled is the shipped default. Even far beyond the configured limit, every
        // request must pass through untouched and no rate-limit headers are written.
        var middleware = CreateMiddleware(enabled: false, limit: 1);

        for (var i = 0; i < 10; i++)
        {
            var context = CreateContext("198.51.100.60");
            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.Headers.ContainsKey("X-RateLimit-Limit").Should().BeFalse();
        }
    }

    [UnitTest]
    public async Task InvokeAsync_PartitionsByAuthenticatedUser_IndependentlyOfOtherUsers()
    {
        var middleware = CreateMiddleware(limit: 1);

        // Alice consumes her single-request budget from the same IP.
        var aliceFirst = CreateContext("198.51.100.70", user: "alice");
        await middleware.InvokeAsync(aliceFirst);
        var aliceSecond = CreateContext("198.51.100.70", user: "alice");
        await middleware.InvokeAsync(aliceSecond);

        // Bob shares the IP but has his own bucket — his first request must still pass.
        var bobFirst = CreateContext("198.51.100.70", user: "bob");
        await middleware.InvokeAsync(bobFirst);

        aliceFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        aliceSecond.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        bobFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [UnitTest]
    public async Task InvokeAsync_PartitionsByTenant_IndependentlyForSameUserName()
    {
        var middleware = CreateMiddleware(limit: 1);

        // Same principal name, but two different tenants must not share a counter.
        var tenantAFirst = CreateContext("198.51.100.80", user: "svc", tenantId: "tenant-a");
        await middleware.InvokeAsync(tenantAFirst);
        var tenantASecond = CreateContext("198.51.100.80", user: "svc", tenantId: "tenant-a");
        await middleware.InvokeAsync(tenantASecond);

        var tenantBFirst = CreateContext("198.51.100.80", user: "svc", tenantId: "tenant-b");
        await middleware.InvokeAsync(tenantBFirst);

        tenantAFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        tenantASecond.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        tenantBFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    private static RateLimitingMiddleware CreateMiddleware(bool enabled = true, int limit = 1)
    {
        var policyStore = Substitute.For<IRateLimitPolicyStore>();

        return new RateLimitingMiddleware(
            next: context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            policyStore,
            Options.Create(new RateLimitingOptions
            {
                Enabled = enabled,
                GlobalRequestsPerMinute = limit
            }),
            NullLogger<RateLimitingMiddleware>.Instance,
            redis: null);
    }

    private static DefaultHttpContext CreateContext(
        string remoteIp,
        string? localIp = null,
        string? user = null,
        string? tenantId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/rest/services/test/FeatureServer/0/query";
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (!string.IsNullOrWhiteSpace(localIp))
        {
            context.Connection.LocalIpAddress = IPAddress.Parse(localIp);
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, user)],
                authenticationType: "Test");
            context.User = new ClaimsPrincipal(identity);
        }

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenantContext(tenantId));
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private sealed class StubTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source { get; } =
            tenantId is null ? TenantContextSource.Anonymous : TenantContextSource.Claim;

        public bool RequireTenantId(out string resolvedTenantId, out string? reason)
        {
            if (string.IsNullOrEmpty(TenantId))
            {
                resolvedTenantId = string.Empty;
                reason = "no tenant context resolved";
                return false;
            }

            resolvedTenantId = TenantId;
            reason = null;
            return true;
        }
    }
}
