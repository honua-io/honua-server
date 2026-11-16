// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
//
// Regression coverage for the ArcGIS Pro / arcpy FeatureServer-add failure: when
// ArcGIS Pro resolves a FeatureServer service or layer by URL it appends
// returnFieldGroups=true&returnPbfFeatureEncodings=true to the metadata request. A metadata allowlist that
// permits only "f" rejects those requests with 400 on a cold output cache, and ArcGIS Pro reports the
// layer as "does not exist or is not supported" (MakeFeatureLayer / Add Data fails). These tests assert
// the metadata endpoints accept and ignore those standard client parameters, returning the same payload as
// a plain f=json request.
//
// returnAdvancedSymbols is the same class of parameter for a different client: the ArcGIS Maps SDK for
// .NET appends it to the layer/service metadata GET during ServiceFeatureTable.LoadAsync. If only the
// layer-query endpoint accepts it, LoadAsync gets 400 and the entire .NET FeatureServer client is blocked.
// It is folded into the parameter set below.

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
