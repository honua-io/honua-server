// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryDateBinsAccessTests : IAsyncLifetime
{
    private const string MonthBin = """{"calendarBin":{"unit":"month"}}""";
    private static readonly string DateBinsPath =
        $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDateBins";
    private static readonly string DateBinsParameters =
        $"?f=json&binField=timestamp&bin={Uri.EscapeDataString(MonthBin)}";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_SecuredLayerAnonymous_IsDeniedWithoutBuckets()
    {
        SecureLayer();
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync(DateBinsPath + DateBinsParameters);

        await AssertDeniedWithoutBucketsAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_SecuredLayerWrongKey_IsDeniedWithoutBuckets()
    {
        SecureLayer();
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "made-up"));

        using var response = await client.GetAsync(DateBinsPath + DateBinsParameters);

        await AssertDeniedWithoutBucketsAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBinsPost_SecuredLayerAnonymous_IsDeniedWithoutBuckets()
    {
        SecureLayer();
        using var client = _fixture.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["binField"] = "timestamp",
            ["bin"] = MonthBin,
        });

        using var response = await client.PostAsync(DateBinsPath, form);

        await AssertDeniedWithoutBucketsAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_AuthenticatedWithoutLayerRole_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () => new FeatureServerAccessFilteringCatalog());
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");
        var path = $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/"
            + $"{ServiceRbacTestFixture.BetaLayerId}/queryDateBins{DateBinsParameters}";

        using var response = await client.GetAsync(path);

        await response.AssertGeoServicesErrorAsync(403);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("\"features\"");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDateBins)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryDateBins")]
    public async Task QueryDateBins_PermanentRowFilter_LimitsBucketCounts()
    {
        var secured = SecureLayer();
        using var client = _fixture.CreateClient(
            c => c.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));
        var path = DateBinsPath + DateBinsParameters;

        using var baseline = await client.GetAsync(path);
        (await ReadBucketCountsAsync(baseline)).Should().BeEquivalentTo(new long[] { 1, 3, 1 });
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: secured,
            permanentFilter: new MetadataV2PermanentFilter { Expression = "objectid = 1" });

        using var filtered = await client.GetAsync(path);

        (await ReadBucketCountsAsync(filtered)).Should().Equal(1L);
    }

    private AccessPolicy SecureLayer()
    {
        var secured = new AccessPolicy { AllowAnonymous = false };
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: secured);
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: secured);
        return secured;
    }

    private static async Task AssertDeniedWithoutBucketsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"features\"");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            await response.AssertGeoServicesErrorAsync(401, 403, 499);
            return;
        }

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private static async Task<long[]> ReadBucketCountsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("count").GetInt64())
            .ToArray();
    }
}
