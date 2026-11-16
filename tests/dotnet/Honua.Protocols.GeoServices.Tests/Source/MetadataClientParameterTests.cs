// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class MetadataClientParameterTests : IClassFixture<WebAppFixture>
{
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const string GeoServicesProMetadataParameters = "returnFieldGroups=true&returnPbfFeatureEncodings=true&returnAdvancedSymbols=true";

    private readonly WebAppFixture _fixture;

    public MetadataClientParameterTests(WebAppFixture fixture) => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServer_ServiceMetadata_WithGeoServicesProClientParameters_ReturnsSameAsPlain()
    {
        var plain = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer?f=json");
        var withParams = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer?f=json&{GeoServicesProMetadataParameters}");

        var withParamsBody = await withParams.Content.ReadAsStringAsync();
        withParams.StatusCode.Should().Be(HttpStatusCode.OK, withParamsBody);
        withParams.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var plainBody = await plain.Content.ReadAsStringAsync();
        withParamsBody.Should().Be(plainBody);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServer_LayerMetadata_WithGeoServicesProClientParameters_ReturnsSameAsPlain()
    {
        var plain = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json");
        var withParams = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json&{GeoServicesProMetadataParameters}");

        var withParamsBody = await withParams.Content.ReadAsStringAsync();
        withParams.StatusCode.Should().Be(HttpStatusCode.OK, withParamsBody);
        withParams.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var plainBody = await plain.Content.ReadAsStringAsync();
        withParamsBody.Should().Be(plainBody);
    }
}
