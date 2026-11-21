// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class ETagEndpointFilterTests
{
    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvokeAsync_UnregisteredTypeInStrictResolver_SkipsETagWithoutThrowing()
    {
        var jsonOptions = Options.Create(new JsonOptions());
        jsonOptions.Value.SerializerOptions.TypeInfoResolver = new EmptyTypeInfoResolver();
        var filter = new ETagEndpointFilter(NullLogger<ETagEndpointFilter>.Instance, jsonOptions);

        var etagService = Substitute.For<IETagService>();
        using var services = new ServiceCollection()
            .AddSingleton(etagService)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/rest/services";
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);
        var original = Results.Ok(new Payload("test"));

        // Warm up JIT/caches.
        var warmupResult = await filter.InvokeAsync(invocation, _ => ValueTask.FromResult<object?>(original));
        Assert.Same(original, warmupResult);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            var result = await filter.InvokeAsync(invocation, _ => ValueTask.FromResult<object?>(original));
            Assert.Same(original, result);
        }

        var allocatedPerCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100.0;
        Assert.True(allocatedPerCall < 1024, $"Expected < 1024 B/call without exception throw, got {allocatedPerCall:F0} B.");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvokeAsync_RegisteredType_AppliesETagAndConditionalHeaders()
    {
        var jsonOptions = Options.Create(new JsonOptions());
        jsonOptions.Value.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        var filter = new ETagEndpointFilter(NullLogger<ETagEndpointFilter>.Instance, jsonOptions);

        var etagService = new ETagService(NullLogger<ETagService>.Instance);
        using var services = new ServiceCollection()
            .AddSingleton<IETagService>(etagService)
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/rest/services";
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);
        var payload = new Payload("alpha");
        var original = Results.Ok(payload);

        var firstResult = Assert.IsAssignableFrom<IResult>(
            await filter.InvokeAsync(invocation, _ => ValueTask.FromResult<object?>(original)));
        await firstResult.ExecuteAsync(httpContext);

        var etag = httpContext.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));

        var conditionalContext = new DefaultHttpContext { RequestServices = services };
        conditionalContext.Request.Method = HttpMethods.Get;
        conditionalContext.Request.Path = "/rest/services";
        conditionalContext.Request.Headers.IfNoneMatch = etag;
        var conditionalResult = Assert.IsAssignableFrom<IResult>(
            await filter.InvokeAsync(
                new DefaultEndpointFilterInvocationContext(conditionalContext),
                _ => ValueTask.FromResult<object?>(original)));
        await conditionalResult.ExecuteAsync(conditionalContext);

        Assert.Equal((int)HttpStatusCode.NotModified, conditionalContext.Response.StatusCode);
    }

    private sealed record Payload(string Name);

    private sealed class EmptyTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }
}
