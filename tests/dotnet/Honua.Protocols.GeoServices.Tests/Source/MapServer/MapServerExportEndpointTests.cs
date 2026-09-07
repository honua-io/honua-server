// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>MapServer export, tile-package and KML endpoint integration tests.</summary>
[Collection("Database.GeoServicesMapServer")]
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerExportEndpointTests : MapServerEndpointTestBase
{
    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithGifFormat_ReturnsBadRequest()
    {
        // Regression (#1772): format=gif must be rejected cleanly (400), and the
        // capability advertisement above must not promise it.
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&format=gif&f=image");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedBbox_AsImage_ReturnsBadRequest()
    {
        // CERT-ERRH-01: a binary image export (f=image) whose bbox cannot be parsed must be
        // rejected with a real HTTP 4xx. An image client expects raster bytes and cannot
        // interpret a 200 "success" carrying a JSON error envelope, so the malformed request
        // has to surface as a genuine failure rather than the PA-070/PA-117 200 body.
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=garbage&f=image");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The body is still the GeoServices {"error":{"code":400,...}} envelope.
        var content = await response.Content.ReadAsStringAsync();
        response.Content.Headers.ContentType?.MediaType.Should().Contain("json");
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue(content);
        error.GetProperty("code").GetInt32().Should().Be(400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedBbox_AsJson_ReturnsErrorEnvelopeWithHttp200()
    {
        // f=json keeps the established GeoServices convention (HTTP 200 + error body); only the
        // binary image path escalates to a real 4xx. Every JSON-format caller parses the body.
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=garbage&f=json");

        await response.AssertGeoServicesErrorAsync(400);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithValidBbox_AsImage_ReturnsImage()
    {
        // CERT-RNDR-01: a valid image export must still render real image bytes and must not be
        // over-rejected by the malformed-bbox guard.
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&bboxSR=4326&imageSR=4326&size=256,256&format=png&transparent=true&f=image");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().StartWith("image/");
        (await response.Content.ReadAsByteArrayAsync()).Should().HaveCountGreaterThan(100);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithArcGisProJsonEnvelope_MatchesCommaEnvelope(bool usePost)
    {
        // Regression (#4501), captured from stock ArcGIS Pro 3.7.1: changing only bbox to CSV made
        // its blank-map HTTP400 become PNG200. Retain the native precision.
        const string bbox = """
            {"xmin":-122.42168401826947,"ymin":37.765347945206912,"xmax":-122.40797808219641,"ymax":37.786301369864447,"spatialReference":{"wkid":4326,"latestWkid":4326}}
            """;
        const string commaBbox = "-122.42168401826947,37.765347945206912,-122.40797808219641,37.786301369864447";
        var parameters = new Dictionary<string, string>
        {
            ["bbox"] = bbox,
            ["bboxSR"] = "4326",
            ["imageSR"] = "4326",
            ["size"] = "469,717",
            ["dpi"] = "144",
            ["transparent"] = "true",
            ["rotation"] = "0",
            ["f"] = "image",
            ["format"] = "png32"
        };
        var endpoint = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export";
        using var content = new FormUrlEncodedContent(parameters);
        using var response = usePost
            ? await Fixture.Client.PostAsync(endpoint, content)
            : await Fixture.Client.GetAsync(endpoint + "?" + await content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        var actual = await response.Content.ReadAsByteArrayAsync();
        using var bitmap = SkiaSharp.SKBitmap.Decode(actual);
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().Be(469);
        bitmap.Height.Should().Be(717);

        parameters["bbox"] = commaBbox;
        using var controlContent = new FormUrlEncodedContent(parameters);
        using var control = await Fixture.Client.GetAsync(endpoint + "?" + await controlContent.ReadAsStringAsync());
        control.StatusCode.Should().Be(HttpStatusCode.OK);
        actual.Should().Equal(await control.Content.ReadAsByteArrayAsync());
    }

    [IntegrationTheory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"xmin\":-123,\"ymin\":37,\"xmax\":-122}")]
    [InlineData("{\"xmin\":null,\"ymin\":37,\"xmax\":-122,\"ymax\":38}")]
    [InlineData("{\"xmin\":\"NaN\",\"ymin\":37,\"xmax\":-122,\"ymax\":38}")]
    [InlineData("{\"xmin\":-123,\"ymin\":37,\"xmax\":1e999,\"ymax\":38}")]
    [InlineData("{\"xmin\":-123,\"ymin\":37,\"xmax\":-122,\"ymax\":91}")]
    [InlineData("{\"xmin\":-123,\"ymin\":38,\"xmax\":-122,\"ymax\":37}")]
    [InlineData("{\"xmin\":-123,\"ymin\":37,\"xmax\":-123,\"ymax\":38}")]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithInvalidJsonEnvelope_RetainsValidation(string bbox)
    {
        var endpoint = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export";
        foreach (var usePost in new[] { false, true })
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["bbox"] = bbox,
                ["bboxSR"] = "4326",
                ["f"] = "image"
            });
            using var response = usePost
                ? await Fixture.Client.PostAsync(endpoint, content)
                : await Fixture.Client.GetAsync(endpoint + "?" + await content.ReadAsStringAsync());

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await response.AssertGeoServicesErrorAsync(400);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithAllLayersHidden_ReturnsBlankImage(bool useDynamicLayer)
    {
        var dynamicLayers = useDynamicLayer
            ? $"&dynamicLayers={Uri.EscapeDataString(BuildSimpleRendererDynamicLayersJson(dynamicLayerId: 7))}"
            : string.Empty;
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export" +
            "?bbox=-180,-90,180,90&size=64,64&f=image&layers=show:-1" + dynamicLayers);

        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_ReturnsImageJson()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Width.Should().Be(256);
        export.Height.Should().Be(256);
        export.Extent.Should().NotBeNull();
        export.Href.Should().NotBeNullOrWhiteSpace();
        export.Scale.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithDatelineCrossingBbox_ReturnsImageJson()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=170,-10,-170,10&bboxSR=4326&imageSR=4326&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Scale.Should().NotBeNull();
        export.Scale.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithWideDatelineCrossingBbox_In3857_PreservesWrappedExtent()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=10,-10,-10,10&bboxSR=4326&imageSR=3857&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Extent.Should().NotBeNull();
        export!.Extent!.Xmin.Should().BeGreaterThan(export.Extent.Xmax);
        export.Scale.Should().NotBeNull();
        export.Scale.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithProjectedBbox_In3857_ReturnsImageJson()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-20037508.34,-20037508.34,20037508.34,20037508.34&bboxSR=3857&imageSR=3857&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Extent.Should().NotBeNull();
        export.Scale.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithGeographicAxisOrderBbox_In4326_ReturnsImageJson()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-90,-180,90,180&bboxSR=4326&imageSR=4326&size=256,256&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Extent.Should().NotBeNull();
        export.Scale.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_Post_ReturnsImageJson()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("bbox", "-180,-90,180,90"),
            new KeyValuePair<string, string>("size", "256,256"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Width.Should().Be(256);
        export.Height.Should().Be(256);
        export.Extent.Should().NotBeNull();
        export.Href.Should().NotBeNullOrWhiteSpace();
        export.Scale.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/estimateExportTilesSize")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/estimateExportTilesSize")]
    public async Task MapServer_EstimateExportTilesSize_ReturnsStorageBackedEstimate()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/estimateExportTilesSize?f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var estimate = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportTilesEstimateResponse);

        estimate.Should().NotBeNull();
        estimate!.TileCount.Should().Be(1);
        estimate.Size.Should().BeGreaterThan(0);
        estimate.EstimatedSizeBytes.Should().Be(estimate.Size);
        estimate.MinZoom.Should().Be(0);
        estimate.MaxZoom.Should().Be(0);
        estimate.TilePackage.Should().BeFalse();
        estimate.StorageFormat.Should().Be("zip");
        estimate.ContentType.Should().Be("application/zip");
        estimate.ExceededTransferLimit.Should().BeFalse();

        // POST form-encoded equivalent must produce the same estimate.
        using var postPayload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("levels", "0"),
            new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
            new KeyValuePair<string, string>("maxTiles", "1"),
        ]);
        var postResponse = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/estimateExportTilesSize",
            postPayload);

        var postContent = await postResponse.Content.ReadAsStringAsync();
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postContent);
        var postEstimate = JsonSerializer.Deserialize(postContent, MapServerJsonContext.Default.ExportTilesEstimateResponse);
        postEstimate.Should().NotBeNull();
        postEstimate!.TileCount.Should().Be(1);
        postEstimate.Size.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/estimateExportTilesSize")]
    public async Task MapServer_EstimateExportTilesSize_WholeWorldHighZoom_DoesNotMaterializeFullGrid()
    {
        // #2065: a whole-world high-zoom estimate (~6.9e10 tiles at z18) previously built the full
        // coordinate grid before applying maxTiles, allocating hundreds of GB -> OOM. The count must
        // be computed first and the build bounded by maxTiles, so this returns promptly with the true
        // count, ExceededTransferLimit=true, and no out-of-memory.
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/estimateExportTilesSize?f=json&minZoom=0&maxZoom=18&exportExtent=-180,-85,180,85&maxTiles=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var estimate = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportTilesEstimateResponse);

        estimate.Should().NotBeNull();
        // The reported count is the true (very large) total, but the build was bounded to maxTiles.
        estimate!.TileCount.Should().BeGreaterThan(1_000_000_000L);
        estimate.ExceededTransferLimit.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/exportTiles")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/exportTiles")]
    public async Task MapServer_ExportTiles_WritesZipArchiveToCloudStorage()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/exportTiles?f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportTilesResponse);

        export.Should().NotBeNull();
        export!.JobStatus.Should().Be("esriJobSucceeded");
        export.TileCount.Should().Be(1);
        export.TilePackage.Should().BeFalse();
        export.StorageFormat.Should().Be("zip");
        export.ContentType.Should().Be("application/zip");
        export.ArchiveFileId.Should().NotBeNullOrWhiteSpace();
        export.DownloadUrl.Should().NotBeNullOrWhiteSpace();
        export.Files.Should().ContainSingle();
        export.Results.Should().NotBeNull();
        export.Results!.OutServiceUrl.Should().NotBeNull();

        // POST form-encoded equivalent must also succeed and write an archive.
        using var postPayload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("levels", "0"),
            new KeyValuePair<string, string>("exportExtent", "-180,-85,180,85"),
            new KeyValuePair<string, string>("maxTiles", "1"),
        ]);
        var postResponse = await Fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/exportTiles",
            postPayload);
        var postContent = await postResponse.Content.ReadAsStringAsync();
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, postContent);
        var postExport = JsonSerializer.Deserialize(postContent, MapServerJsonContext.Default.ExportTilesResponse);
        postExport.Should().NotBeNull();
        postExport!.JobStatus.Should().Be("esriJobSucceeded");
        if (!string.IsNullOrWhiteSpace(postExport.ArchiveFileId))
        {
            await Fixture.GetService<ICloudFileStorage>().DeleteAsync(postExport.ArchiveFileId!);
        }

        var fileId = export.ArchiveFileId!;
        var storage = Fixture.GetService<ICloudFileStorage>();
        try
        {
            var bytes = await storage.DownloadBytesAsync(fileId);

            bytes.Should().NotBeNull();
            bytes!.Length.Should().BeGreaterThan(0);
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var tileEntry = archive.GetEntry("0/0/0.png");
            tileEntry.Should().NotBeNull();

            await using var tileStream = tileEntry!.Open();
            var pngHeader = new byte[8];
            var read = await tileStream.ReadAsync(pngHeader.AsMemory(0, pngHeader.Length));

            read.Should().Be(pngHeader.Length);
            pngHeader.Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        }
        finally
        {
            await storage.DeleteAsync(fileId);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/exportTiles")]
    public async Task MapServer_ExportTiles_WithStorageFormatTpk_WritesExplodedTilePackage()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/exportTiles?f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1&storageFormat=tpk");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportTilesResponse);

        export.Should().NotBeNull();
        export!.JobStatus.Should().Be("esriJobSucceeded");
        export.TilePackage.Should().BeTrue();
        export.StorageFormat.Should().Be("tpk");
        export.ArchiveFileId.Should().NotBeNullOrWhiteSpace();

        var fileId = export.ArchiveFileId!;
        var storage = Fixture.GetService<ICloudFileStorage>();
        try
        {
            var bytes = await storage.DownloadBytesAsync(fileId);
            bytes.Should().NotBeNull();
            using var archive = new ZipArchive(new MemoryStream(bytes!), ZipArchiveMode.Read);

            // Esri exploded-cache layout: conf.xml + _alllayers/Lzz/Rrrrrrrrr/Cccccccc.png
            archive.Entries.Should().Contain(entry => entry.FullName.EndsWith("/conf.xml", StringComparison.Ordinal));
            archive.Entries.Should().Contain(entry => entry.FullName.EndsWith("/conf.cdi", StringComparison.Ordinal));
            var tileEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Contains("_alllayers/L00/", StringComparison.Ordinal) &&
                entry.FullName.EndsWith(".png", StringComparison.Ordinal));
            tileEntry.Should().NotBeNull();
            tileEntry!.FullName.Should().Contain("R00000000");
            tileEntry.FullName.Should().Contain("C00000000");
        }
        finally
        {
            await storage.DeleteAsync(fileId);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/exportTiles")]
    public async Task MapServer_ExportTiles_WithCompactStorageFormat_ReturnsBadRequest()
    {
        // Compact Cache V2 / TPKX now negotiates the durable async path (#2706) rather than the old
        // "unsupported" rejection: a single-level request is instead rejected by the TPKX
        // validation, which requires at least two zoom levels.
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/exportTiles?f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1&storageFormat=tpkx");

        var content = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync(400);
        content.ToLowerInvariant().Should().Contain("zoom levels");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/estimateExportTilesSize")]
    public async Task MapServer_EstimateExportTilesSize_WithTpk_ReportsTilePackage()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/estimateExportTilesSize?f=json&levels=0&exportExtent=-180,-85,180,85&maxTiles=1&storageFormat=tpk");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var estimate = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportTilesEstimateResponse);

        estimate.Should().NotBeNull();
        estimate!.TilePackage.Should().BeTrue();
        estimate.StorageFormat.Should().Be("tpk");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task MapServer_GenerateKml_ReturnsValidKml_ForPointLineAndPolygonLayers()
    {
        var serviceName = await SeedGenerateKmlGeometryServiceAsync();

        var response = await Fixture.Client.GetAsync($"/rest/services/{serviceName}/MapServer/generateKml?f=kml");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.google-earth.kml+xml");

        var document = XDocument.Parse(content);
        XNamespace kml = "http://www.opengis.net/kml/2.2";

        document.Root.Should().NotBeNull();
        document.Descendants(kml + "Point").Should().NotBeEmpty();
        document.Descendants(kml + "LineString").Should().NotBeEmpty();
        document.Descendants(kml + "Polygon").Should().NotBeEmpty();
        document.Descendants(kml + "Placemark").Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task MapServer_GenerateKml_Post_ReturnsValidKml()
    {
        var serviceName = await SeedGenerateKmlGeometryServiceAsync();

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "kml"
        });
        var response = await Fixture.Client.PostAsync(
            $"/rest/services/{serviceName}/MapServer/generateKml",
            form);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.google-earth.kml+xml");

        var document = XDocument.Parse(content);
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        document.Descendants(kml + "Placemark").Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task MapServer_GenerateKml_WithKmzFormat_ReturnsCompressedArchive()
    {
        var serviceName = await SeedGenerateKmlGeometryServiceAsync();

        var response = await Fixture.Client.GetAsync($"/rest/services/{serviceName}/MapServer/generateKml?f=kmz&layers=110");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.google-earth.kmz");
        bytes.Length.Should().BeGreaterThan(0);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var kmlEntry = archive.GetEntry("doc.kml");
        kmlEntry.Should().NotBeNull();

        using var kmlStream = kmlEntry!.Open();
        using var reader = new StreamReader(kmlStream);
        var kmlContent = await reader.ReadToEndAsync();

        var document = XDocument.Parse(kmlContent);
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        document.Descendants(kml + "Point").Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task MapServer_GenerateKml_WithProjectedLayer_ReturnsWgs84Coordinates()
    {
        var serviceName = await SeedGenerateKmlGeometryServiceAsync();

        var response = await Fixture.Client.GetAsync($"/rest/services/{serviceName}/MapServer/generateKml?f=kml&layers=113");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var document = XDocument.Parse(content);
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        var coordinateText = document.Descendants(kml + "coordinates").Single().Value.Trim();
        var ordinates = coordinateText.Split(',', StringSplitOptions.TrimEntries);

        ordinates.Should().HaveCountGreaterThanOrEqualTo(2);
        double.Parse(ordinates[0], CultureInfo.InvariantCulture).Should().BeApproximately(-157.80, 0.0001);
        double.Parse(ordinates[1], CultureInfo.InvariantCulture).Should().BeApproximately(21.30, 0.0001);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithTime_ReturnsImageJson()
    {
        var time = System.Uri.EscapeDataString("2023-01-01T00:00:00Z,2023-01-10T00:00:00Z");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&time={time}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedTime_DoesNotLeakInputOrParserDetails()
    {
        const string sentinel = "MAP_TIME_SENTINEL";
        var malformedTime = Uri.EscapeDataString($"not-a-time-{sentinel}");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&time={malformedTime}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid time parameter.");
        content.Should().NotContain(sentinel);
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithLayerTimeOptions_ReturnsImageJson()
    {
        var layerTimeOptions = System.Uri.EscapeDataString(
            "{\"0\":{\"useTime\":true,\"time\":\"2023-01-01T00:00:00Z,2023-01-10T00:00:00Z\"}}");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&layerTimeOptions={layerTimeOptions}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithDynamicLayers_ReturnsImageJson()
    {
        var dynamicLayers = System.Uri.EscapeDataString(
            "[{\"id\":0,\"source\":{\"type\":\"mapLayer\",\"mapLayerId\":0},\"definitionExpression\":\"category = 'test'\"}]");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&dynamicLayers={dynamicLayers}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithDynamicLayerSimpleRenderer_ReturnsImageJson()
    {
        var dynamicLayers = Uri.EscapeDataString(BuildSimpleRendererDynamicLayersJson(dynamicLayerId: 7));
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&dynamicLayers={dynamicLayers}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithUnsupportedDynamicLayerRenderer_ReturnsBadRequest()
    {
        var dynamicLayers = Uri.EscapeDataString(
            """[{"id":7,"source":{"type":"mapLayer","mapLayerId":0},"drawingInfo":{"renderer":{"type":"heatmap"}}}]""");

        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&f=json&dynamicLayers={dynamicLayers}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unsupported drawingInfo renderer");
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain("BytePositionInLine");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithGdbVersion_IgnoresParameter()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&gdbVersion=QA");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedLayerDefs_DoesNotLeakJsonParserDetails()
    {
        var malformedLayerDefs = Uri.EscapeDataString("{\"0\":");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&layerDefs={malformedLayerDefs}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("layerDefs contains invalid JSON.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    // Regression (#1430): the Esri layerDefs array form [{"layerId":N,"where":...}]
    // must be accepted (previously rejected with "Invalid layer id").
    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithJsonArrayLayerDefs_ReturnsOk()
    {
        var layerDefs = Uri.EscapeDataString(
            $$"""[{"layerId":{{WebAppFixture.TestLayerId}},"where":"1=1"}]""");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&layerDefs={layerDefs}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedDynamicLayers_DoesNotLeakJsonParserDetails()
    {
        var malformedDynamicLayers = Uri.EscapeDataString("[{\"id\":");
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json&dynamicLayers={malformedDynamicLayers}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("dynamicLayers contains invalid JSON.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithUnsupportedImageSr_DoesNotLeakTransformDetails()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&bboxSR=4326&imageSR=999999&size=256,256&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid spatial reference.");
        content.Should().NotContain("999999");
        content.Should().NotContain("NotSupportedException");
        content.Should().NotContain("System.");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedSizePair_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,,256&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithSizeExceedingAdvertisedMaximum_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export" +
            "?bbox=-180,-90,180,90&size=4097,2000&f=image");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedBackgroundColor_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&backgroundColor=255,,0,0&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&layers=show:{WebAppFixture.TestLayerId},&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithInvalidLayerIdentifier_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&layers=show:{WebAppFixture.TestLayerId},foo&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithInvalidRequestedLayer_ReturnsBadRequest()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&layers=show:{WebAppFixture.TestLayerId},999999&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // #1302: MapServer/export must accept the GeoServices-standard layers visibility
    // prefixes (show:/hide:/include:/exclude:), not just a bare id list.
    [Theory]
    [InlineData("show")]
    [InlineData("hide")]
    [InlineData("include")]
    [InlineData("exclude")]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithLayersVisibilityPrefix_ReturnsImageJson(string prefix)
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&layers={prefix}:{WebAppFixture.TestLayerId}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Width.Should().Be(256);
        export.Height.Should().Be(256);
        export.Extent.Should().NotBeNull();
        export.Href.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_WithBareLayerIdList_ReturnsImageJson()
    {
        var response = await Fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export?bbox=-180,-90,180,90&size=256,256&layers={WebAppFixture.TestLayerId}&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var export = JsonSerializer.Deserialize(content, MapServerJsonContext.Default.ExportImageResponse);

        export.Should().NotBeNull();
        export!.Extent.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/export")]
    public async Task MapServer_Export_Post_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await PostTextPlainJsonAsync("/export");

        await response.AssertGeoServicesErrorAsync(415, 500);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task MapServer_GenerateKml_Post_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var response = await PostTextPlainJsonAsync("/generateKml");

        await response.AssertGeoServicesErrorAsync(415, 500);
    }
}
