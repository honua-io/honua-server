// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

// temporalExtent under production authentication: a secured layer's time range is not disclosed to anonymous
// or wrongly keyed callers, and the owner still reads it.
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerTemporalExtentAccessTests : IAsyncLifetime
{
    private static readonly string ExtentPath =
        $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/temporalExtent?f=json";

    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var secured = new AccessPolicy { AllowAnonymous = false };
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: secured);
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: secured,
            temporal: new MetadataV2ObjectTemporal { StartTimeField = "timestamp" });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_SecuredLayerAnonymous_IsDeniedWithoutTheRange()
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync(ExtentPath);

        await AssertDeniedWithoutRangeAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_SecuredLayerWrongKey_IsDeniedWithoutTheRange()
    {
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "made-up"));

        using var response = await client.GetAsync(ExtentPath);

        await AssertDeniedWithoutRangeAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/temporalExtent")]
    public async Task TemporalExtent_SecuredLayerAdminKey_ReturnsTheRange()
    {
        using var client = _fixture.CreateClient(
            c => c.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));

        using var response = await client.GetAsync(ExtentPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"minEpochMs\":");
    }

    private static async Task AssertDeniedWithoutRangeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("minEpochMs");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Honua.TestKit.Extensions.GeoServicesErrorAssertions.AssertGeoServicesError(200, body, [401, 403, 499]);
            return;
        }

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
