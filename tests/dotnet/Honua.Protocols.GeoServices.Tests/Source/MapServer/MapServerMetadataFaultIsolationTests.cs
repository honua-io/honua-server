// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerMetadataFaultIsolationTests
{
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServer_Metadata_WhenTemporalExtentFails_OmitsTimeInfoAndReturnsMetadata()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<TemporalPropertyType>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TemporalExtentResult?>(
                new InvalidCastException("malformed temporal value")));

        var fixture = new WebAppFixture().ReplaceService<IFeatureReader>(reader);
        await fixture.InitializeAsync();
        try
        {
            var serviceResponse = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer?f=json");
            var serviceContent = await serviceResponse.Content.ReadAsStringAsync();

            serviceResponse.StatusCode.Should().Be(HttpStatusCode.OK, serviceContent);
            var service = JsonSerializer.Deserialize(
                serviceContent,
                MapServerJsonContext.Default.MapServerResponse);
            service.Should().NotBeNull();
            service!.Layers.Should().NotBeNullOrEmpty();
            service.TimeInfo.Should().BeNull();

            var layerResponse = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/{WebAppFixture.TestLayerId}?f=json");
            var layerContent = await layerResponse.Content.ReadAsStringAsync();

            layerResponse.StatusCode.Should().Be(HttpStatusCode.OK, layerContent);
            var layer = JsonSerializer.Deserialize(
                layerContent,
                MapServerJsonContext.Default.MapServerLayerResponse);
            layer.Should().NotBeNull();
            layer!.AdvancedQueryCapabilities.Should().NotBeNull();
            layer.TimeInfo.Should().BeNull();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
