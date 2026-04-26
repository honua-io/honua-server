// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
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

    [Fact]
    public void ShouldCache_ByDefault_ReturnsFalse()
    {
        var context = CreateRequest(
            scheme: "https",
            host: "api.example.test",
            pathBase: string.Empty,
            path: "/odata/Features(0)",
            queryString: "?$top=25");

        var result = ResponseCacheUtilities.ShouldCache(context, new CacheOptions());

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldCache_WithResponseCachingEnabled_ReturnsTrue()
    {
        var context = CreateRequest(
            scheme: "https",
            host: "api.example.test",
            pathBase: string.Empty,
            path: "/odata/Features(0)",
            queryString: "?$top=25");

        var result = ResponseCacheUtilities.ShouldCache(
            context,
            new CacheOptions
            {
                Enabled = true,
                ResponseCachingEnabled = true
            });

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("S_INTERSECTS(geometry, POINT(1 2))")]
    [InlineData("S_DWITHIN(geometry, POINT(1 2), 10, meters)")]
    [InlineData("{\"op\":\"s_intersects\",\"args\":[{\"property\":\"geometry\"},{\"type\":\"Point\",\"coordinates\":[1,2]}]}")]
    [InlineData("geo.intersects(Geometry, geography'POINT(1 2)')")]
    [InlineData("geo.distance(Geometry, geography'POINT(1 2)') lt 100")]
    public void ShouldBypassAdHocSpatialResponseCache_WithSpatialFilterText_ReturnsTrue(string filter)
    {
        var query = new FeatureQuery();

        var result = ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(query, filter);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldBypassAdHocSpatialResponseCache_WithSpatialFilter_ReturnsTrue()
    {
        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create([0x01], SpatialRelationship.Intersects, 4326)
        };

        var result = ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(query);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldBypassAdHocSpatialResponseCache_WithSpatialSql_ReturnsTrue()
    {
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("ST_Intersects(geometry, @p0)", [Array.Empty<byte>()])
        };

        var result = ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(query);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldBypassAdHocSpatialResponseCache_WithAttributeFilter_ReturnsFalse()
    {
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment("category = @p0", ["park"])
        };

        var result = ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(query, "category = 'park'");

        result.Should().BeFalse();
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
