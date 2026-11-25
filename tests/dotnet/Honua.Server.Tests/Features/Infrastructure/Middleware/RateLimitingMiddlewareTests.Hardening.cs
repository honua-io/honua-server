// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.RateLimiting;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Middleware;

public sealed partial class RateLimitingMiddlewareTests
{
    [UnitTheory]
    [InlineData("/healthz")]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    [InlineData("/healthz/startup")]
    public async Task InvokeAsync_HealthzProbes_AreExemptFromRateLimiting(string probePath)
    {
        var middleware = CreateMiddleware(limit: 1);

        for (var i = 0; i < 5; i++)
        {
            var context = CreateContext("198.51.100.240");
            context.Request.Path = probePath;
            await middleware.InvokeAsync(context);
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }
    }

    [UnitTest]
    public async Task InvokeAsync_WithNullRemoteIpAddress_StillRateLimitsAuthenticatedAndAnonymousCallers()
    {
        var middleware = CreateMiddleware(limit: 1);

        var authFirst = CreateContext(remoteIp: null, subject: "uds-user-" + Guid.NewGuid().ToString("N"));
        await middleware.InvokeAsync(authFirst);
        var authSecond = CreateContext(remoteIp: null, subject: authFirst.User.FindFirst("sub")!.Value);
        await middleware.InvokeAsync(authSecond);

        authFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        authSecond.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);

        var anonTenant = "uds-anon-" + Guid.NewGuid().ToString("N");
        var anonFirst = CreateContext(remoteIp: null, tenantId: anonTenant);
        await middleware.InvokeAsync(anonFirst);
        var anonSecond = CreateContext(remoteIp: null, tenantId: anonTenant);
        await middleware.InvokeAsync(anonSecond);

        anonFirst.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        anonSecond.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    [UnitTest]
    public async Task InvokeAsync_CategoryCeilings_EnforceConfiguredUploadQueryAndMetadataLimits()
    {
        var nextCalls = 0;
        var middleware = CreateIsolatedMiddleware(
            new RateLimitingOptions
            {
                Enabled = true,
                GlobalRequestsPerMinute = 1000,
                UploadRequestsPerMinute = 1,
                QueryRequestsPerMinute = 2,
                MetadataRequestsPerMinute = 1,
            },
            () => nextCalls++);

        (await InvokeCategoryRequestAsync(middleware, "POST", "/api/v1/admin/import/upload")).Should().Be(StatusCodes.Status200OK);
        (await InvokeCategoryRequestAsync(middleware, "POST", "/api/v1/admin/import/upload")).Should().Be(StatusCodes.Status429TooManyRequests);

        (await InvokeCategoryRequestAsync(middleware, "GET", "/openapi.json")).Should().Be(StatusCodes.Status200OK);
        (await InvokeCategoryRequestAsync(middleware, "GET", "/openapi.json")).Should().Be(StatusCodes.Status429TooManyRequests);

        (await InvokeCategoryRequestAsync(middleware, "GET", "/ogc/collections/demo/items")).Should().Be(StatusCodes.Status200OK);
        (await InvokeCategoryRequestAsync(middleware, "GET", "/rest/services/demo/FeatureServer/0/query")).Should().Be(StatusCodes.Status200OK);
        (await InvokeCategoryRequestAsync(middleware, "GET", "/ogc/collections/demo/items")).Should().Be(StatusCodes.Status429TooManyRequests);
        nextCalls.Should().Be(4);
    }

    [UnitTest]
    public async Task InvokeAsync_WithCustomStatusCode_ReturnsConfiguredStatusCode()
    {
        var middleware = CreateIsolatedMiddleware(new RateLimitingOptions
        {
            Enabled = true,
            GlobalRequestsPerMinute = 1,
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        });

        var first = CreateContext("198.51.100.169");
        await middleware.InvokeAsync(first);
        var second = CreateContext("198.51.100.169");
        await middleware.InvokeAsync(second);

        first.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        second.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    private static RateLimitingMiddleware CreateIsolatedMiddleware(RateLimitingOptions options, Action? onNext = null)
        => new(
            next: context =>
            {
                onNext?.Invoke();
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            new RateLimitingDependencies
            {
                PolicyStore = Substitute.For<IRateLimitPolicyStore>(),
                Options = Options.Create(options),
                Logger = NullLogger<RateLimitingMiddleware>.Instance,
                TimeProvider = new FrozenTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 10, TimeSpan.Zero)),
                CounterStore = new ShardedRateLimitCounterStore(1024),
            });

    private static async Task<int> InvokeCategoryRequestAsync(RateLimitingMiddleware middleware, string method, string path)
    {
        var context = CreateContext("198.51.100.161", httpMethod: method);
        context.Request.Path = path;
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
