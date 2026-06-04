// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Regression coverage for the ArcGIS Pro / arcpy FeatureServer-add failure: when
// ArcGIS Pro resolves a FeatureServer service or layer by URL it appends
// returnFieldGroups=true&returnPbfFeatureEncodings=true to the metadata request.
// Honua's metadata allowlist previously permitted only "f", so those requests were
// rejected with 400 on a cold output cache. ArcGIS Pro reported the layer as
// "does not exist or is not supported" (MakeFeatureLayer / Add Data failed). These
// tests assert the metadata endpoints accept and ignore those standard client
// parameters, returning the same payload as a plain f=json request.
//
// returnAdvancedSymbols is the same class of bug for a different client: the ArcGIS
// Maps SDK for .NET appends it to the layer/service metadata GET during
// ServiceFeatureTable.LoadAsync. #1455 accepted it on the layer-query endpoint but
// not the metadata endpoints, so LoadAsync returned 400 and the entire .NET
// FeatureServer client was blocked. It is folded into the parameter set below.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Verifies the GeoServices metadata endpoints accept the default parameters
/// ArcGIS Pro / arcpy append when resolving a FeatureServer by URL.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class MetadataClientParameterTests : IAsyncLifetime
{
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const string ArcGisProMetadataParameters = "returnFieldGroups=true&returnPbfFeatureEncodings=true&returnAdvancedSymbols=true";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServer_ServiceMetadata_WithArcGisProClientParameters_ReturnsSameAsPlain()
    {
        var plain = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer?f=json");
        var withParams = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer?f=json&{ArcGisProMetadataParameters}");

        var withParamsBody = await withParams.Content.ReadAsStringAsync();
        withParams.StatusCode.Should().Be(HttpStatusCode.OK, withParamsBody);
        withParams.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var plainBody = await plain.Content.ReadAsStringAsync();
        withParamsBody.Should().Be(plainBody);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServer_LayerMetadata_WithArcGisProClientParameters_ReturnsSameAsPlain()
    {
        var plain = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json");
        var withParams = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json&{ArcGisProMetadataParameters}");

        var withParamsBody = await withParams.Content.ReadAsStringAsync();
        withParams.StatusCode.Should().Be(HttpStatusCode.OK, withParamsBody);
        withParams.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var plainBody = await plain.Content.ReadAsStringAsync();
        withParamsBody.Should().Be(plainBody);
    }
}
