// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    public void BuildODataLayerKey_QueryValuesWithDelimiters_DoNotCollide()
    {
        var encodedValue = CreateRequest(
            scheme: "https",
            host: "api.example.test",
            pathBase: string.Empty,
            path: "/odata/Features(0)",
            queryString: "?a=1%26b%3D2");
        var separateParameters = CreateRequest(
            scheme: "https",
            host: "api.example.test",
            pathBase: string.Empty,
            path: "/odata/Features(0)",
            queryString: "?a=1&b=2");

        var encodedValueKey = ResponseCacheUtilities.BuildODataLayerKey(0, encodedValue.Request);
        var separateParametersKey = ResponseCacheUtilities.BuildODataLayerKey(0, separateParameters.Request);

        encodedValueKey.Should().NotBe(separateParametersKey);
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

    [UnitTheory]
    [InlineData("Authorization", "Bearer secret")]
    [InlineData("X-API-Key", "secret")]
    [InlineData("X-Esri-Authorization", "secret")]
    [InlineData("X-Honua-Embed-Key", "secret")]
    [InlineData("X-Honua-Token", "secret")]
    public void ShouldCache_WithCredentialHeader_ReturnsFalse(string header, string value)
    {
        var context = CreateCachingRequest();
        context.Request.Headers[header] = value;

        ResponseCacheUtilities.ShouldCache(context, EnabledResponseCache()).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("?token=secret")]
    [InlineData("?key=secret")]
    [InlineData("?f=json&token=secret")]
    public void ShouldCache_WithCredentialQuery_ReturnsFalse(string queryString)
    {
        var context = CreateCachingRequest(queryString);

        ResponseCacheUtilities.ShouldCache(context, EnabledResponseCache()).Should().BeFalse();
    }

    [UnitTest]
    public void ShouldCache_WithAuthenticatedPrincipal_ReturnsFalse()
    {
        var context = CreateCachingRequest();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"));

        ResponseCacheUtilities.ShouldCache(context, EnabledResponseCache()).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("no-store")]
    [InlineData("no-cache")]
    [InlineData("private, no-store")]
    public void ShouldCache_WhenResponseCacheControlSuppressesSharedCache_ReturnsFalse(string cacheControl)
    {
        var context = CreateCachingRequest();
        context.Response.Headers.CacheControl = cacheControl;

        ResponseCacheUtilities.ShouldCache(context, EnabledResponseCache()).Should().BeFalse();
    }

    [UnitTest]
    public void ShouldCache_WhenResponseCacheControlIsPrivate_ReturnsTrue()
    {
        var context = CreateCachingRequest();
        context.Response.Headers.CacheControl = "private, must-revalidate";

        ResponseCacheUtilities.ShouldCache(context, EnabledResponseCache()).Should().BeTrue();
    }

    [UnitTest]
    public void BuildFeatureServerKey_DoesNotEmbedTheCredential()
    {
        var context = CreateCachingRequest("?f=json&where=1%3D1&token=super-secret-token");

        var key = ResponseCacheUtilities.BuildFeatureServerKey("roads", 0, context.Request);

        key.Should().NotContain("super-secret-token");
    }

    [UnitTest]
    public void BuildFeatureServerKey_DifferentLicenseFingerprints_ProducesDifferentKeys()
    {
        var pro = CreateCachingRequest();
        var community = CreateCachingRequest();
        UseServices(pro, testSchemaHeaders: false, new FixedLicenseStatusProvider(new LicenseStatus(HonuaEdition.Pro, true, null, null)));
        UseServices(community, testSchemaHeaders: false, new FixedLicenseStatusProvider(new LicenseStatus(HonuaEdition.Community, true, null, null)));

        var proKey = ResponseCacheUtilities.BuildFeatureServerKey("roads", 0, pro.Request);
        var communityKey = ResponseCacheUtilities.BuildFeatureServerKey("roads", 0, community.Request);

        proKey.Should().NotBe(communityKey);
    }

    [UnitTest]
    public void BuildFeatureServerKey_TestSchemaHeader_VariesOnlyWhenEnabled()
    {
        var first = CreateCachingRequest();
        var second = CreateCachingRequest();
        first.Request.Headers["X-Honua-Test-Schema"] = "schema-a";
        second.Request.Headers["X-Honua-Test-Schema"] = "schema-b";
        UseServices(first, testSchemaHeaders: false);
        UseServices(second, testSchemaHeaders: false);

        ResponseCacheUtilities.BuildFeatureServerKey("roads", 0, first.Request)
            .Should().Be(ResponseCacheUtilities.BuildFeatureServerKey("roads", 0, second.Request));

        UseServices(first, testSchemaHeaders: true);
        UseServices(second, testSchemaHeaders: true);

        ResponseCacheUtilities.BuildFeatureServerKey("roads", 0, first.Request)
            .Should().NotBe(ResponseCacheUtilities.BuildFeatureServerKey("roads", 0, second.Request));
    }

    private static CacheOptions EnabledResponseCache()
        => new()
        {
            Enabled = true,
            ResponseCachingEnabled = true
        };

    private static DefaultHttpContext CreateCachingRequest(string queryString = "?$top=25")
        => CreateRequest(
            scheme: "https",
            host: "api.example.test",
            pathBase: string.Empty,
            path: "/rest/services/roads/FeatureServer/0/query",
            queryString: queryString);

    private static void UseServices(
        DefaultHttpContext context,
        bool testSchemaHeaders,
        ILicenseStatusProvider? license = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_TEST_SCHEMA_HEADERS"] = testSchemaHeaders ? "true" : "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        if (license is not null)
        {
            services.AddSingleton<ILicenseStatusProvider>(license);
        }

        context.RequestServices = services.BuildServiceProvider();
    }

    private sealed class FixedLicenseStatusProvider(LicenseStatus status) : ILicenseStatusProvider
    {
        public LicenseStatus GetCurrentStatus() => status;

        public Task<LicenseUploadResult> UploadLicenseAsync(Stream licenseStream, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
