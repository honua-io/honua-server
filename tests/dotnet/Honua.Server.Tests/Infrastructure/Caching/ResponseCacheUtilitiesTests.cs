// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Caching;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Infrastructure.Caching;

[Collection("Unit")]
public sealed class ResponseCacheUtilitiesTests
{
    [Fact]
    public void BuildODataLayerKey_DifferentOrigins_ProducesDifferentKeys()
    {
        var first = CreateRequest(
            scheme: "https",
            host: "api-a.example.test",
            pathBase: "/edge-a",
            path: "/odata/Features(0)",
            queryString: "?$top=25");
        var second = CreateRequest(
            scheme: "https",
            host: "api-b.example.test",
            pathBase: "/edge-b",
            path: "/odata/Features(0)",
            queryString: "?$top=25");

        var firstKey = ResponseCacheUtilities.BuildODataLayerKey(0, first.Request);
        var secondKey = ResponseCacheUtilities.BuildODataLayerKey(0, second.Request);

        firstKey.Should().NotBe(secondKey);
    }

    [Fact]
    public void BuildODataLayerKey_DifferentPreferHeaders_ProducesDifferentKeys()
    {
        var baseline = CreateRequest(
            scheme: "https",
            host: "api.example.test",
            pathBase: string.Empty,
            path: "/odata/Features(0)",
            queryString: "?$top=25");
        var trackChanges = CreateRequest(
            scheme: "https",
            host: "api.example.test",
            pathBase: string.Empty,
            path: "/odata/Features(0)",
            queryString: "?$top=25",
            prefer: "odata.track-changes");

        var baselineKey = ResponseCacheUtilities.BuildODataLayerKey(0, baseline.Request);
        var trackChangesKey = ResponseCacheUtilities.BuildODataLayerKey(0, trackChanges.Request);

        baselineKey.Should().NotBe(trackChangesKey);
    }

    private static DefaultHttpContext CreateRequest(
        string scheme,
        string host,
        string pathBase,
        string path,
        string queryString,
        string? prefer = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = scheme;
        context.Request.Host = HostString.FromUriComponent(host);
        context.Request.PathBase = new PathString(pathBase);
        context.Request.Path = new PathString(path);
        context.Request.QueryString = new QueryString(queryString);
        context.Request.Headers.Accept = "application/json";

        if (!string.IsNullOrWhiteSpace(prefer))
        {
            context.Request.Headers["Prefer"] = prefer;
        }

        return context;
    }
}
