// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for raster import endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public class RasterImportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/raster/formats")]
    public async Task GetRasterFormats_ReturnsAllSupportedExtensions()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/raster/formats");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(".tif");
        content.Should().Contain(".tiff");
        content.Should().Contain(".png");
        content.Should().Contain(".jpg");
        content.Should().Contain("GeoTIFF");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithEmptyFile_Returns400()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var emptyFile = new ByteArrayContent(Array.Empty<byte>());
        emptyFile.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "empty.tif"
        };
        content.Add(emptyFile);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithMissingLayerId_Returns400()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileBytes = CreateMinimalGeoTiffBytes();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("test-raster"), "name");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("layerId");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithMissingName_Returns400()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileBytes = CreateMinimalGeoTiffBytes();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("name");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithUnsupportedFormat_Returns400()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.bmp"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Unsupported raster format");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithInvalidTiffHeader_Returns400()
    {
        // Arrange: file with .tif extension but invalid header
        var content = new MultipartFormDataContent();
        var fileBytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "invalid.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("TIFF header");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithNoFile_Returns400()
    {
        // Arrange: multipart with only form fields, no file
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Raster file is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_PngWithoutWorldFile_Returns400()
    {
        // Arrange: PNG without world file (no SRID either)
        var content = new MultipartFormDataContent();
        var pngBytes = CreateMinimalPngBytes();
        var fileContent = new ByteArrayContent(pngBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.png"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("world file");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_PngWithSridButNoWorldFile_Returns400()
    {
        // Arrange: PNG with explicit SRID but no world file — SRID alone cannot
        // replace the geotransform, so import must be rejected
        var content = new MultipartFormDataContent();
        var pngBytes = CreateMinimalPngBytes();
        var fileContent = new ByteArrayContent(pngBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.png"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");
        content.Add(new StringContent("4326"), "srid");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("world file");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithInvalidTileZoomLevels_Returns400()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileBytes = CreateMinimalGeoTiffBytes();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");
        content.Add(new StringContent("abc,xyz"), "tileZoomLevels");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("tileZoomLevels");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportRaster_WithOutOfRangeTileZoomLevels_Returns400()
    {
        // Arrange: valid integers but outside 0-24 range
        var content = new MultipartFormDataContent();
        var fileBytes = CreateMinimalGeoTiffBytes();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-raster"), "name");
        content.Add(new StringContent("0,5,25"), "tileZoomLevels");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("tileZoomLevels");
    }

    // =============================================================================
    // Test data helpers
    // =============================================================================

    /// <summary>
    /// Creates a minimal valid TIFF header (little-endian, classic TIFF).
    /// This is not a complete GeoTIFF but has the correct magic bytes for header validation.
    /// PostGIS will reject it on actual ingest since it's incomplete, which tests the
    /// error path for corrupt files.
    /// </summary>
    private static byte[] CreateMinimalGeoTiffBytes()
    {
        // TIFF little-endian header: "II" + version 42
        return
        [
            0x49, 0x49, 0x2A, 0x00, // TIFF little-endian magic
            0x08, 0x00, 0x00, 0x00, // Offset to first IFD
            0x00, 0x00, // IFD entry count (0 = minimal)
            0x00, 0x00, 0x00, 0x00 // Next IFD offset (0 = no more IFDs)
        ];
    }

    /// <summary>
    /// Creates minimal PNG file bytes (valid PNG header).
    /// </summary>
    private static byte[] CreateMinimalPngBytes()
    {
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, // IHDR chunk length
            0x49, 0x48, 0x44, 0x52, // IHDR
            0x00, 0x00, 0x00, 0x01, // Width: 1
            0x00, 0x00, 0x00, 0x01, // Height: 1
            0x08, 0x02, // Bit depth: 8, Color type: RGB
            0x00, 0x00, 0x00, // Compression, filter, interlace
            0x90, 0x77, 0x53, 0xDE, // CRC
            0x00, 0x00, 0x00, 0x00, // IEND chunk length
            0x49, 0x45, 0x4E, 0x44, // IEND
            0xAE, 0x42, 0x60, 0x82 // CRC
        ];
    }
}
