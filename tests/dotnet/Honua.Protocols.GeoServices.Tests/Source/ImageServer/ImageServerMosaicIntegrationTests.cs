// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerMosaicIntegrationTests
{
    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/query")]
    [Operation(Operations.Query)]
    public async Task QueryCatalog_ListsAllRastersWithAcquisitionDates()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/query?f=json&returnGeometry=false");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var features = json.RootElement.GetProperty("features");
            features.GetArrayLength().Should().Be(3);

            var acquisitionDates = features.EnumerateArray()
                .Select(feature => feature.GetProperty("attributes").GetProperty("AcquisitionDate").GetInt64())
                .OrderBy(value => value)
                .ToArray();

            acquisitionDates.Should().Equal(
                RasterIntegrationTestData.WestAcquisition.ToUnixTimeMilliseconds(),
                RasterIntegrationTestData.EastAcquisition.ToUnixTimeMilliseconds(),
                RasterIntegrationTestData.OverlapAcquisition.ToUnixTimeMilliseconds());

            foreach (var feature in features.EnumerateArray())
            {
                var attributes = feature.GetProperty("attributes");
                attributes.GetProperty("BandCount").GetInt32().Should().Be(1);
                attributes.GetProperty("PixelType").GetString().Should().Be("32BF");
                attributes.GetProperty("CreatedAt").GetInt64().Should().BeGreaterThan(0);
            }
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_UsesSpatialMosaicSelectionAcrossMultipleRasters()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify?geometry=3.5,1&geometryType=esriGeometryPoint&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            json.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(40);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithNewestMergeRule_ReturnsNewestRasterValueAndCatalogItems()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&returnCatalogItems=true&mosaicRule=newest&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

            json.RootElement.GetProperty("name").GetString().Should().Contain("mosaic");
            json.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(5);
            json.RootElement.GetProperty("catalogItems").GetArrayLength().Should().Be(2);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithMaxMergeRule_ReturnsMaximumPixelValue()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&mosaicRule=max&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            json.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(20);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithTimeOnCommunityEdition_ReturnsPaymentRequired()
    {
        var fixture = await CreateFixtureAsync(HonuaEdition.Community).ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&time=2024-02-15T00:00:00Z&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithTimeOnProEdition_WhenNewestLayerSnapshotMissesPoint_ReturnsNoData()
    {
        var fixture = await CreateFixtureAsync(HonuaEdition.Pro).ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&time=2024-01-20T00:00:00Z&f=json");

            // ArcGIS ImageServer identify returns a 200 NoData document (not a 404) when the
            // location does not intersect any raster for the active spatial/temporal selection,
            // matching getSamples. Returning 404 here breaks the ArcGIS JS/Python SDK identify call.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            json.RootElement.GetProperty("value").GetString().Should().Be("NoData");
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_AggregatesExtentAndTimeExtentAcrossLayerRasters()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestLayerId}/ImageServer?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var extent = json.RootElement.GetProperty("extent");
            extent.GetProperty("xmin").GetDouble().Should().Be(0);
            extent.GetProperty("xmax").GetDouble().Should().Be(4);
            extent.GetProperty("ymin").GetDouble().Should().Be(0);
            extent.GetProperty("ymax").GetDouble().Should().Be(2);

            var timeExtent = json.RootElement.GetProperty("timeInfo").GetProperty("timeExtent");
            timeExtent[0].GetInt64().Should().Be(RasterIntegrationTestData.WestAcquisition.ToUnixTimeMilliseconds());
            timeExtent[1].GetInt64().Should().Be(RasterIntegrationTestData.OverlapAcquisition.ToUnixTimeMilliseconds());
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_AsInlineJpegMosaic_AppliesCompressionQuality()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/exportImage" +
                "?bbox=0,0,4,2&size=64,32&format=jpg&compressionQuality=40&f=image");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
            (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().NotBeEmpty();
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query, Operations.PerformanceTesting)]
    public async Task QueryRasters_ForTenOverlappingRasters_CompletesWithinTwoSeconds()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            await RasterIntegrationTestData.SeedOverlappingRasterStackAsync(fixture, count: 10).ConfigureAwait(false);
            var store = fixture.GetService<IRasterStore>();
            var query = new RasterSelectionQuery
            {
                Geometry = RasterIntegrationTestData.CreatePointSelectionGeometry(1, 1),
                GeometrySrid = 4326
            };

            _ = await store.QueryRastersAsync(WebAppFixture.TestLayerId, query).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var rasters = await store.QueryRastersAsync(WebAppFixture.TestLayerId, query).ConfigureAwait(false);
            stopwatch.Stop();

            rasters.Should().HaveCount(10);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(HonuaEdition? edition = null)
    {
        var fixture = new WebAppFixture();
        if (edition.HasValue)
        {
            fixture.ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition.Value));
        }

        await fixture.InitializeAsync().ConfigureAwait(false);
        await RasterIntegrationTestData.SeedIssue522MosaicAsync(fixture).ConfigureAwait(false);
        return fixture;
    }

}
