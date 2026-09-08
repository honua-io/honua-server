// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Verifies native mosaic resolution through the HTTP metadata contract.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerMosaicPixelSizeTests
{
    [IntegrationTheory]
    [InlineData(-122.44, 37.78, 0.0, 0.01, 0.01, false, 0.01, 0.01)]
    [InlineData(37.78, -73.44, 0.0, 0.01, 0.01, false, 0.01, 0.01)]
    [InlineData(-122.44, 37.78, 0.005, 0.01, 0.01, false, 0.01, 0.01)]
    [InlineData(-122.44, 37.78, 0.0, 0.005, 0.02, false, 0.005, 0.01)]
    [InlineData(-122.44, 37.78, 0.005, 0.01, 0.01, true, 0.01125, 0.01)]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_MosaicExtents_PreservesNativePixelResolution(
        double x, double y, double offset, double secondaryPixelX, double secondaryPixelY,
        bool omitTransform, double expectedPixelX, double expectedPixelY)
    {
        var rasters = new[]
        {
            CreateRaster(100, x, y, 0.01, 0.01, omitTransform),
            CreateRaster(101, x + offset, y, secondaryPixelX, secondaryPixelY, omitTransform)
        };
        var store = Substitute.For<IRasterStore>();
        store.ListRastersAsync(WebAppFixture.TestLayerId, Arg.Any<CancellationToken>())
            .Returns(rasters);
        store.GetMosaicStatisticsAsync(WebAppFixture.TestLayerId, Arg.Any<long[]>(),
                Arg.Any<RasterMergeStrategy>(), Arg.Any<int[]?>(),
                Arg.Any<RasterIdentifyRendering?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterStatistics>());

        var fixture = new WebAppFixture().ReplaceService(store);
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer?f=json");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            using var document = JsonDocument.Parse(content);
            document.RootElement.GetProperty("pixelSizeX").GetDouble()
                .Should().BeApproximately(expectedPixelX, 1e-12);
            document.RootElement.GetProperty("pixelSizeY").GetDouble()
                .Should().BeApproximately(expectedPixelY, 1e-12);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static RasterInfo CreateRaster(long id, double x, double y,
        double pixelX, double pixelY, bool omitTransform) => new()
        {
            Id = id,
            LayerId = WebAppFixture.TestLayerId,
            Name = $"resolution-{id}",
            Width = 4,
            Height = 4,
            BandCount = 2,
            PixelType = "32BF",
            Srid = 4326,
            GeoTransform = omitTransform ? null : [x, pixelX, 0, y, 0, -pixelY],
            Extent = new RasterExtent
            {
                XMin = x,
                XMax = x + 4 * pixelX,
                YMin = y - 4 * pixelY,
                YMax = y,
                Srid = 4326
            },
            CreatedAt = DateTimeOffset.UnixEpoch
        };
}
