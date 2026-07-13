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
using Honua.TestKit.Extensions;

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
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/query?f=json&returnGeometry=false");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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

            foreach (var attributes in features.EnumerateArray().Select(feature => feature.GetProperty("attributes")))
            {
                attributes.GetProperty("BandCount").GetInt32().Should().Be(1);
                attributes.GetProperty("PixelType").GetString().Should().Be("32BF");
                attributes.GetProperty("CreatedAt").GetInt64().Should().BeGreaterThan(0);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_UsesSpatialMosaicSelectionAcrossMultipleRasters()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify?geometry=3.5,1&geometryType=esriGeometryPoint&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(40);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithNewestMergeRule_ReturnsNewestRasterValueAndCatalogItems()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&returnCatalogItems=true&mosaicRule=newest&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            json.RootElement.GetProperty("name").GetString().Should().Contain("mosaic");
            json.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(5);
            json.RootElement.GetProperty("catalogItems").GetArrayLength().Should().Be(2);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithMaxMergeRule_ReturnsMaximumPixelValue()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&mosaicRule=max&f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(20);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithTimeOnCommunityEdition_ReturnsPaymentRequired()
    {
        var fixture = await CreateFixtureAsync(HonuaEdition.Community);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&time=2024-02-15T00:00:00Z&f=json");

            await response.AssertGeoServicesErrorAsync(402);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    [Operation(Operations.Identify)]
    public async Task Identify_WithTimeOnProEdition_WhenNewestLayerSnapshotMissesPoint_ReturnsNoData()
    {
        var fixture = await CreateFixtureAsync(HonuaEdition.Pro);
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/identify" +
                "?geometry=1.5,1&geometryType=esriGeometryPoint&time=2024-01-20T00:00:00Z&f=json");

            // ArcGIS ImageServer identify returns a 200 NoData document (not a 404) when the
            // location does not intersect any raster for the active spatial/temporal selection,
            // matching getSamples. Returning 404 here breaks the ArcGIS JS/Python SDK identify call.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("value").GetString().Should().Be("NoData");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_AggregatesExtentAndTimeExtentAcrossLayerRasters()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestLayerId}/ImageServer?f=json");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfo_ServesBandStatisticsFromPersistedMosaicSnapshot()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            // The first request backfills the persisted layer-level statistics snapshot (#1639).
            var first = await fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestLayerId}/ImageServer?f=json");
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
            {
                json.RootElement.GetProperty("minValues")[0].GetDouble().Should().Be(5);
                json.RootElement.GetProperty("maxValues")[0].GetDouble().Should().Be(40);
            }

            // Tamper with the persisted snapshot. The next request must serve the tampered
            // value verbatim, proving service metadata is read from persisted statistics and
            // never recomputed from the raster pixels per request.
            // #2020: honua.raster_layer_statistics is a literal, process-global catalog table
            // (not search_path isolated), so route this UPDATE through the shared seed advisory
            // lock to keep parallel [Collection("Database")] tests off the 40P01 deadlock path.
            var tampered = 0;
            await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
            {
                await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE honua.raster_layer_statistics SET max_value = 12345 WHERE layer_id = @layerId;";
                command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
                tampered = await command.ExecuteNonQueryAsync();
            });
            tampered.Should().BeGreaterThan(0, "the first request must have persisted the mosaic statistics");

            var second = await fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestLayerId}/ImageServer?f=json");
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            using (var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
            {
                json.RootElement.GetProperty("maxValues")[0].GetDouble().Should().Be(12345);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImage_AsInlineJpegMosaic_AppliesCompressionQuality()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/exportImage" +
                "?bbox=0,0,4,2&size=64,32&format=jpg&compressionQuality=40&f=image");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
            (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    [Operation(Operations.Export)]
    public async Task ExportImagePost_WithTime_SelectsTemporalSliceAndAgreesWithGet()
    {
        // Regression for #2364: CreateExportImageRequest (the POST body/query merge builder)
        // did not map the `time` parameter, so POST exportImage silently ignored it and always
        // returned the default (newest) mosaic — diverging from the GET path, which binds time.
        var fixture = await CreateFixtureAsync(HonuaEdition.Pro);
        try
        {
            // bbox=1,0,3,2 straddles the overlap-newest raster (value 5, Feb 1). At time
            // 2024-01-20 that raster is excluded, so the slice shows the older west/east
            // rasters (values 20/40) instead — a visibly different mosaic than the default.
            const string bbox = "?bbox=1,0,3,2&size=64,32&format=png&f=image";
            const string time = "2024-01-20T00:00:00Z";

            var getWithTime = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/exportImage{bbox}&time={time}");
            getWithTime.StatusCode.Should().Be(HttpStatusCode.OK);
            var getWithTimeBytes = await getWithTime.Content.ReadAsByteArrayAsync();

            using var postWithTimeContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("bbox", "1,0,3,2"),
                new KeyValuePair<string, string>("size", "64,32"),
                new KeyValuePair<string, string>("format", "png"),
                new KeyValuePair<string, string>("time", time),
                new KeyValuePair<string, string>("f", "image"),
            });
            var postWithTime = await fixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/exportImage",
                postWithTimeContent);
            postWithTime.StatusCode.Should().Be(HttpStatusCode.OK);
            var postWithTimeBytes = await postWithTime.Content.ReadAsByteArrayAsync();

            using var postNewestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("bbox", "1,0,3,2"),
                new KeyValuePair<string, string>("size", "64,32"),
                new KeyValuePair<string, string>("format", "png"),
                new KeyValuePair<string, string>("f", "image"),
            });
            var postNewest = await fixture.Client.PostAsync(
                $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/exportImage",
                postNewestContent);
            postNewest.StatusCode.Should().Be(HttpStatusCode.OK);
            var postNewestBytes = await postNewest.Content.ReadAsByteArrayAsync();

            // POST with time must honor the requested slice: it agrees with GET for the same
            // time, and differs from the default (newest) mosaic. Before the fix POST dropped
            // `time`, so postWithTime == postNewest and postWithTime != getWithTime.
            postWithTimeBytes.Should().NotBeEmpty();
            postWithTimeBytes.Should().Equal(getWithTimeBytes, "POST and GET exportImage must honor time identically");
            postWithTimeBytes.Should().NotEqual(postNewestBytes, "POST exportImage must apply the requested temporal slice, not the default newest mosaic");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query, Operations.PerformanceTesting)]
    public async Task QueryRasters_ForTenOverlappingRasters_CompletesWithinTwoSeconds()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await RasterIntegrationTestData.SeedOverlappingRasterStackAsync(fixture, count: 10);
            var store = fixture.GetService<IRasterStore>();
            var query = new RasterSelectionQuery
            {
                Geometry = RasterIntegrationTestData.CreatePointSelectionGeometry(1, 1),
                GeometrySrid = 4326
            };

            _ = await store.QueryRastersAsync(WebAppFixture.TestLayerId, query);

            var stopwatch = Stopwatch.StartNew();
            var rasters = await store.QueryRastersAsync(WebAppFixture.TestLayerId, query);
            stopwatch.Stop();

            rasters.Should().HaveCount(10);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(HonuaEdition? edition = null)
    {
        var fixture = new WebAppFixture();
        if (edition.HasValue)
        {
            fixture.ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition.Value));
        }

        await fixture.InitializeAsync();
        await RasterIntegrationTestData.SeedIssue522MosaicAsync(fixture);
        return fixture;
    }
}
