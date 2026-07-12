// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
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

    [UnitTest]
    public async Task InvokeAsync_AcrossTwoInstancesSharingTheCounterStore_EnforcesASingleLimit()
    {
        // Two distinct middleware instances stand in for two server nodes coordinating through a
        // shared counter store (the process-global counter table here; Redis in production). A
        // client must not exceed its limit by hopping between instances (issue #2158).
        var nodeA = CreateMiddleware(limit: 2);
        var nodeB = CreateMiddleware(limit: 2);
        const string clientIp = "198.51.100.111";

        var first = CreateContext(clientIp);
        await nodeA.InvokeAsync(first);
        var second = CreateContext(clientIp);
        await nodeB.InvokeAsync(second);
        var third = CreateContext(clientIp);
        await nodeA.InvokeAsync(third);

        first.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        second.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        third.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests,
            "the shared counter must aggregate requests across instances so a third request trips the limit of 2");
    }

    [UnitTest]
    public async Task InvokeAsync_WithMatchingTenantTierPolicy_AppliesPolicyLimitOverGlobalDefault()
    {
        // A per-tenant tier policy (limit 1) is more specific than the generous global default and
        // must drive the enforced limit (issue #2158 tier precedence applied at runtime).
        var store = new TestRateLimitPolicyStore(new RateLimitPolicy
        {
            Name = "tenant-tier",
            Scope = RateLimitScopes.Tenant,
            Key = "tier-tenant",
            RequestsPerWindow = 1,
            WindowDuration = TimeSpan.FromMinutes(1),
        });
        var middleware = CreateMiddleware(limit: 10_000, policyStore: store);

        var first = CreateContext("198.51.100.121", user: "svc", tenantId: "tier-tenant");
        await middleware.InvokeAsync(first);
        var second = CreateContext("198.51.100.121", user: "svc", tenantId: "tier-tenant");
        await middleware.InvokeAsync(second);

        first.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        second.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        first.Response.Headers["X-RateLimit-Limit"].ToString().Should().Be("1");
    }

    [UnitTest]
    public async Task InvokeAsync_PartitionsByEndpointPolicy_DoesNotDrainAnotherEndpointAllowance()
    {
        // Global limit is generous; each endpoint declares its own 1/min override. A single
        // tenant+IP exhausting one endpoint must not consume a different endpoint's allowance
        // (issue #2779: Console traffic exhausting the OIDC authorize-url 5/min limit).
        var middleware = CreateMiddleware(limit: 10_000);

        // Exhaust endpoint A's own 1/min bucket from one IP.
        var endpointAFirst = CreateContext("198.51.100.201", endpointName: "endpoint-a", endpointLimit: 1);
        await middleware.InvokeAsync(endpointAFirst);
        var endpointASecond = CreateContext("198.51.100.201", endpointName: "endpoint-a", endpointLimit: 1);
        await middleware.InvokeAsync(endpointASecond);

        // Endpoint B has never been called by this caller; its first request must be allowed even
        // though endpoint A's allowance is already exhausted for the same tenant+IP.
        var endpointBFirst = CreateContext("198.51.100.201", endpointName: "endpoint-b", endpointLimit: 1);
        await middleware.InvokeAsync(endpointBFirst);

        endpointAFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        endpointASecond.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        endpointBFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [UnitTest]
    public async Task InvokeAsync_AttributedEndpointTraffic_CountsTowardSubjectGlobalCeiling()
    {
        // Global ceiling is 2; the endpoint's own limit is generous (100). Traffic to an
        // attributed endpoint must still count against the subject's shared global ceiling, so the
        // third request trips the global limit even though the endpoint's own budget is untouched
        // (regression guard: post-#2779 the scoped counter must not replace the global one).
        var middleware = CreateMiddleware(limit: 2);

        var first = CreateContext("198.51.100.211", endpointName: "endpoint-generous", endpointLimit: 100);
        await middleware.InvokeAsync(first);
        var second = CreateContext("198.51.100.211", endpointName: "endpoint-generous", endpointLimit: 100);
        await middleware.InvokeAsync(second);
        var third = CreateContext("198.51.100.211", endpointName: "endpoint-generous", endpointLimit: 100);
        await middleware.InvokeAsync(third);

        first.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        second.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        third.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests,
            "attributed-endpoint traffic must count toward the subject's global ceiling");
    }

    [UnitTest]
    public async Task InvokeAsync_MixedAttributedAndUnattributed_DoesNotBlockUnattributedUnderGlobal()
    {
        // Generous global ceiling. Exhausting an attributed endpoint's own 1/min budget must not
        // spill onto the subject's unattributed traffic: a subsequent unattributed request from the
        // same IP still passes because the shared ceiling is nowhere near exhausted.
        var middleware = CreateMiddleware(limit: 10_000);

        var attributedFirst = CreateContext("198.51.100.221", endpointName: "endpoint-a", endpointLimit: 1);
        await middleware.InvokeAsync(attributedFirst);
        var attributedSecond = CreateContext("198.51.100.221", endpointName: "endpoint-a", endpointLimit: 1);
        await middleware.InvokeAsync(attributedSecond);

        // Unattributed request (no endpoint) from the same subject.
        var unattributed = CreateContext("198.51.100.221");
        await middleware.InvokeAsync(unattributed);

        attributedFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        attributedSecond.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        unattributed.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [UnitTest]
    public async Task InvokeAsync_EndpointScope_PartitionsByHttpMethod()
    {
        // The same endpoint reached by different verbs must not share a scoped counter: a GET
        // exhausting its 1/min budget must not 429 a POST to the same endpoint (issue #2779 scope
        // must include the HTTP method).
        var middleware = CreateMiddleware(limit: 10_000);

        var getFirst = CreateContext("198.51.100.231", endpointName: "authorize", endpointLimit: 1, httpMethod: "GET");
        await middleware.InvokeAsync(getFirst);
        var getSecond = CreateContext("198.51.100.231", endpointName: "authorize", endpointLimit: 1, httpMethod: "GET");
        await middleware.InvokeAsync(getSecond);

        var postFirst = CreateContext("198.51.100.231", endpointName: "authorize", endpointLimit: 1, httpMethod: "POST");
        await middleware.InvokeAsync(postFirst);

        getFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        getSecond.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        postFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK,
            "the endpoint scope includes the HTTP verb, so POST has its own counter");
    }

    private static RateLimitingMiddleware CreateMiddleware(
        bool enabled = true,
        int limit = 1,
        IRateLimitPolicyStore? policyStore = null)
    {
        policyStore ??= Substitute.For<IRateLimitPolicyStore>();

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
        string? tenantId = null,
        string? endpointName = null,
        int endpointLimit = 0,
        string httpMethod = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = httpMethod;
        context.Request.Path = "/rest/services/test/FeatureServer/0/query";
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (!string.IsNullOrWhiteSpace(localIp))
        {
            context.Connection.LocalIpAddress = IPAddress.Parse(localIp);
        }

        if (!string.IsNullOrWhiteSpace(endpointName))
        {
            var endpoint = new Endpoint(
                requestDelegate: null,
                new EndpointMetadataCollection(new RateLimitAttribute(endpointLimit)),
                endpointName);
            context.SetEndpoint(endpoint);
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

    private sealed class TestRateLimitPolicyStore(params RateLimitPolicy[] policies) : IRateLimitPolicyStore
    {
        private readonly IReadOnlyList<RateLimitPolicy> _policies = policies;

        public Task<IReadOnlyList<RateLimitPolicy>> ListPoliciesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_policies);

        public Task<RateLimitPolicy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
            => Task.FromResult(_policies.FirstOrDefault(p => p.PolicyId == policyId));

        public Task<RateLimitPolicy> CreatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default)
            => Task.FromResult(policy);

        public Task<RateLimitPolicy?> UpdatePolicyAsync(RateLimitPolicy policy, CancellationToken cancellationToken = default)
            => Task.FromResult<RateLimitPolicy?>(policy);

        public Task<bool> DeletePolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<RateLimitStatus?> GetStatusAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<RateLimitStatus?>(null);
    }
}
