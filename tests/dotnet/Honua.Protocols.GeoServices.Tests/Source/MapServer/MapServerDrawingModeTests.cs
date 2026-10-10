// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerDrawingModeTests : MapServerEndpointTestBase
{
    private static readonly string[] PackageFormats = ["zip", "tpk"];
    [IntegrationTheory]
    [InlineData(null, true)]
    [InlineData("cached", true)]
    [InlineData("dynamic", false)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/estimateExportTilesSize")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/exportTiles")]
    public async Task DrawingMode_AdvertisesAndEnforcesItsCacheContract_WhilePreservingSameSqlData(string? mode, bool cached)
    {
        var serviceName = AddSameDataPublication(Fixture, mode);
        var root = $"/rest/services/{serviceName}/MapServer";
        using var metadataResponse = await Fixture.Client.GetAsync(root + "?f=json");
        using var metadata = await ReadJsonAsync(metadataResponse);
        metadata.RootElement.GetProperty("singleFusedMapCache").GetBoolean().Should().Be(cached);
        metadata.RootElement.GetProperty("exportTilesAllowed").GetBoolean().Should().Be(cached);
        metadata.RootElement.GetProperty("supportsDynamicLayers").GetBoolean().Should().BeFalse();
        metadata.RootElement.TryGetProperty("tileInfo", out var tileInfo).Should().Be(cached);
        if (cached)
        {
            tileInfo.GetProperty("rows").GetInt32().Should().Be(256);
            tileInfo.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
            metadata.RootElement.GetProperty("maxExportTilesCount").GetInt32().Should().BeGreaterThan(0);
        }
        else
        {
            metadata.RootElement.GetProperty("maxExportTilesCount").GetInt32().Should().Be(0);
        }

        var ids = await ReadSqlIdsAsync();
        ids.Should().NotBeEmpty();
        foreach (var name in new[] { serviceName, WebAppFixture.TestServiceId })
        {
            using var queryResponse = await Fixture.Client.GetAsync(
                $"/rest/services/{name}/MapServer/0/query?f=json&where=1%3D1&returnIdsOnly=true");
            using var query = await ReadJsonAsync(queryResponse);
            query.RootElement.GetProperty("objectIds").EnumerateArray().Select(id => id.GetInt64()).Order()
                .Should().Equal(ids);
        }

        using var countResponse = await Fixture.Client.GetAsync(root + "/0/query?f=json&where=1%3D1&returnCountOnly=true");
        using var count = await ReadJsonAsync(countResponse);
        count.RootElement.GetProperty("count").GetInt64().Should().Be(ids.Length);
        var statistics = Uri.EscapeDataString("""[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"row_count"}]""");
        using var statisticsResponse = await Fixture.Client.GetAsync(root + "/0/query?f=json&where=1%3D1&outStatistics=" + statistics);
        using var statisticsJson = await ReadJsonAsync(statisticsResponse);
        statisticsJson.RootElement.GetProperty("features")[0].GetProperty("attributes").GetProperty("row_count").GetInt64().Should().Be(ids.Length);

        // A dynamic drawing mode continues serving actual map images and layer definitions.
        var layerDefinitions = Uri.EscapeDataString("""{"0":"objectid > 0"}""");
        using var exportResponse = await Fixture.Client.GetAsync(root +
            "/export?bbox=-123,37,-122,38&bboxSR=4326&imageSR=3857&size=256,256&format=png&f=image&layerDefs=" + layerDefinitions);
        await AssertPngAsync(exportResponse);

        using var tileResponse = await Fixture.Client.GetAsync(root + "/tile/0/0/0");
        if (cached) { await AssertPngAsync(tileResponse); }
        else { await AssertUnavailableAsync(tileResponse, HttpStatusCode.BadRequest); }

        var wmts = root + "/WMTS?SERVICE=WMTS&VERSION=1.0.0";
        using var capabilitiesResponse = await Fixture.Client.GetAsync(wmts + "&REQUEST=GetCapabilities");
        if (cached)
        {
            var body = await capabilitiesResponse.Content.ReadAsStringAsync();
            capabilitiesResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var capabilities = XDocument.Parse(body);
            capabilities.Root!.Name.LocalName.Should().Be("Capabilities");
            capabilities.Descendants().Where(element => element.Name.LocalName == "Layer").Should().NotBeEmpty();
        }
        else { await AssertUnavailableAsync(capabilitiesResponse, HttpStatusCode.NotFound); }

        using var wmtsTileResponse = await Fixture.Client.GetAsync(wmts +
            "&REQUEST=GetTile&LAYER=0&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
        if (cached) { await AssertPngAsync(wmtsTileResponse); }
        else { await AssertUnavailableAsync(wmtsTileResponse, HttpStatusCode.NotFound); }

        // Canonical OGC WMTS is an independent on-demand protocol publication.
        var canonicalWmts = $"/ogc/services/{serviceName}/wmts?SERVICE=WMTS&VERSION=1.0.0";
        using var canonicalCapabilitiesResponse = await Fixture.Client.GetAsync(canonicalWmts + "&REQUEST=GetCapabilities");
        var canonicalBody = await canonicalCapabilitiesResponse.Content.ReadAsStringAsync();
        canonicalCapabilitiesResponse.StatusCode.Should().Be(HttpStatusCode.OK, canonicalBody);
        XDocument.Parse(canonicalBody).Descendants().Where(element => element.Name.LocalName == "Layer").Should().NotBeEmpty();
        using var canonicalTileResponse = await Fixture.Client.GetAsync(canonicalWmts +
            "&REQUEST=GetTile&LAYER=0&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
        await AssertPngAsync(canonicalTileResponse);

        const string selection = "?f=json&levels=0&exportExtent=-180,-85,180,85&exportExtentSR=4326&maxTiles=1";
        using var estimateResponse = await Fixture.Client.GetAsync(root + "/estimateExportTilesSize" + selection);
        if (cached)
        {
            using var estimate = await ReadJsonAsync(estimateResponse);
            estimate.RootElement.GetProperty("tileCount").GetInt64().Should().Be(1);
        }
        else { await AssertUnavailableAsync(estimateResponse, HttpStatusCode.BadRequest); }

        foreach (var format in PackageFormats)
        {
            using var packageResponse = await Fixture.Client.GetAsync(root + "/exportTiles" + selection + "&storageFormat=" + format);
            if (!cached)
            {
                await AssertUnavailableAsync(packageResponse, HttpStatusCode.BadRequest);
                continue;
            }
            using var result = await ReadJsonAsync(packageResponse);
            result.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded");
            result.RootElement.GetProperty("tileCount").GetInt64().Should().Be(1);
            var fileId = result.RootElement.GetProperty("archiveFileId").GetString()!;
            var storage = Fixture.GetService<ICloudFileStorage>();
            try
            {
                var bytes = await storage.DownloadBytesAsync(fileId);
                bytes.Should().NotBeNull();
                using var archive = new ZipArchive(new MemoryStream(bytes!), ZipArchiveMode.Read);
                var entry = archive.Entries.Single(item => item.FullName.EndsWith(".png", StringComparison.Ordinal));
                await using var stream = entry.Open();
                using var bitmap = SKBitmap.Decode(stream);
                bitmap.Should().NotBeNull();
                bitmap!.Width.Should().Be(256);
                bitmap.Height.Should().Be(256);
            }
            finally { await storage.DeleteAsync(fileId); }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/exportTiles")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/exportTiles")]
    public async Task DynamicMode_DurableTileExportNegotiation_IsRejectedBeforeSubmission()
    {
        await using var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.AddSingleton<IExecutionJobStore>(new InMemoryExecutionJobStore());
            services.AddSingleton<IJobQueue>(new InMemoryJobQueue());
        });
        await fixture.InitializeAsync();
        var name = AddSameDataPublication(fixture, "dynamic");
        var root = $"/rest/services/{name}/MapServer/exportTiles";
        using var get = await fixture.Client.GetAsync(root +
            "?f=json&storageFormatType=esriMapCacheStorageModeCompactV2&exportExtent=-180,-85,180,85&exportExtentSR=4326&levels=0&maxTiles=1");
        await AssertUnavailableAsync(get, HttpStatusCode.BadRequest);
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("storageFormatType", "esriMapCacheStorageModeCompactV2"),
            new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
            new KeyValuePair<string, string>("exportExtentSR", "4326"),
            new KeyValuePair<string, string>("levels", "0"),
            new KeyValuePair<string, string>("maxTiles", "1")
        ]);
        using var post = await fixture.Client.PostAsync(root, form);
        await AssertUnavailableAsync(post, HttpStatusCode.BadRequest);
    }

    private static string AddSameDataPublication(WebAppFixture fixture, string? mode)
    {
        var snapshot = fixture.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph;
        var originalPublication = graph.Publications.Single(publication =>
            publication.PublicationType == MetadataV2PublicationType.EsriMapLayer &&
            publication.LayerIndex == WebAppFixture.TestLayerId &&
            snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var owningService) &&
            owningService.Metadata.Name == WebAppFixture.TestServiceId &&
            owningService.Protocols.Contains(ServiceProtocols.MapServer, StringComparer.OrdinalIgnoreCase));
        var original = snapshot.Index.ServicesById[originalPublication.ServiceId];
        snapshot.ResolveStorageLayerId(originalPublication).Should().Be(WebAppFixture.TestLayerId);
        var serviceName = "drawing_" + Guid.NewGuid().ToString("N");
        var serviceId = "svc-" + serviceName;
        var options = original.Options.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        options.Remove(MetadataV2MapServerDrawing.OptionName);
        if (mode is not null)
        {
            using var value = JsonDocument.Parse(JsonSerializer.Serialize(mode));
            options[MetadataV2MapServerDrawing.OptionName] = value.RootElement.Clone();
        }
        var service = original with
        {
            Metadata = original.Metadata with { Id = serviceId, Name = serviceName },
            Protocols = original.Protocols.Append(ServiceProtocols.Wmts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Options = options
        };
        var publications = graph.Publications.Where(publication => publication.ServiceId == original.Metadata.Id)
            .Select(publication => publication with
            {
                Metadata = publication.Metadata with { Id = serviceId + "-" + publication.Metadata.Id },
                ServiceId = serviceId
            }).ToArray();
        publications.Should().NotBeEmpty();
        var updated = graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services.Append(service).ToArray(),
            Publications = graph.Publications.Concat(publications).ToArray()
        };
        var validation = MetadataV2GraphValidator.Validate(updated);
        validation.IsValid.Should().BeTrue(string.Join("; ", validation.Errors));
        fixture.GetService<TestMetadataV2GraphProvider>().SetGraph(updated, schema: fixture.CurrentSchema);
        return serviceName;
    }

    private async Task<long[]> ReadSqlIdsAsync()
    {
        await using var connection = await Fixture.Postgres.GetConnectionAsync(Fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT objectid FROM features WHERE layer_id = @layerId ORDER BY objectid";
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { ids.Add(reader.GetInt64(0)); }
        return ids.ToArray();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        return json;
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        body.Should().Contain("Dynamic MapServer drawing");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be((int)expected);
    }

    private static async Task AssertPngAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var bitmap = SKBitmap.Decode(bytes);
        bitmap.Should().NotBeNull();
        bitmap!.Width.Should().Be(256);
        bitmap.Height.Should().Be(256);
    }
}
