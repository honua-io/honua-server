// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

/// <summary>
/// WMTS 1.0 sections 7.2.2.2 and 7.3.2.2 require unknown KVP keys to be ignored.
/// QGIS appends SLD_VERSION and TRANSPARENT to the advertised legend tile URL.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wmts10)]
public sealed class OgcClassicWmtsUnknownParameterTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetTile")]
    public async Task Wmts_GetTile_UnknownParameters_PreservesImageAndKnownParameterValidation()
    {
        foreach (var prefix in new[]
        {
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS",
            $"/ogc/services/{WebAppFixture.TestServiceId}/wmts"
        })
        {
            var url = prefix + $"?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}" +
                "&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0";
            var baseline = await _fixture.Client.GetAsync(url);
            baseline.StatusCode.Should().Be(HttpStatusCode.OK);
            var expected = await baseline.Content.ReadAsByteArrayAsync();
            expected.Take(8).Should().Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            foreach (var extras in new[] { "&SLD_VERSION=1.1.0&TRANSPARENT=true", "&vendor_hint=1", "&time=not-a-time&elevation=400" })
            {
                var response = await _fixture.Client.GetAsync(url + extras);
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
                (await response.Content.ReadAsByteArrayAsync()).Should().Equal(expected);
            }

            var invalid = await _fixture.Client.GetAsync(url.Replace("TILEMATRIX=0", "TILEMATRIX=invalid", StringComparison.Ordinal) + "&vendor_hint=1");
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await invalid.Content.ReadAsStringAsync()).Should().Contain("InvalidParameterValue");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetFeatureInfo")]
    public async Task Wmts_GetFeatureInfo_UnknownParameters_PreservesResponse()
    {
        var url = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0&LAYER={WebAppFixture.TestLayerId}" +
            "&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=128&J=128&INFOFORMAT=application/json";
        var baseline = await _fixture.Client.GetAsync(url);
        baseline.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = await baseline.Content.ReadAsStringAsync();

        var response = await _fixture.Client.GetAsync(url + "&vendor_hint=1&time=not-a-time&elevation=400");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Be(expected);
    }
}
