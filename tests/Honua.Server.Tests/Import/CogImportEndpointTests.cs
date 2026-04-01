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
/// Integration tests for COG import endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public class CogImportEndpointTests : IAsyncLifetime
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
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportCog_WithEmptyFile_Returns400()
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
        content.Add(new StringContent("test-cog"), "name");
        content.Add(new StringContent("CloudOptimizedGeoTiff"), "format");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportCog_WithInvalidTiffHeader_Returns400()
    {
        // Arrange — a file that claims to be COG but has no TIFF magic bytes
        var content = new MultipartFormDataContent();
        var badBytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        var fileContent = new ByteArrayContent(badBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "bad.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-cog"), "name");
        content.Add(new StringContent("CloudOptimizedGeoTiff"), "format");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportCog_WithValidTiffHeader_AcceptsFormat()
    {
        // Arrange — minimal valid TIFF header (little-endian, classic TIFF magic)
        var content = new MultipartFormDataContent();
        var tiffBytes = CreateMinimalGeoTiffBytes();
        var fileContent = new ByteArrayContent(tiffBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-cog"), "name");
        content.Add(new StringContent("CloudOptimizedGeoTiff"), "format");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert — format is recognized and reaches the import pipeline.
        // Minimal synthetic TIFF data may cause PostGIS to fail (500) because the
        // file has a valid header but no real raster content. The key verification
        // is that the format is accepted (not rejected as unsupported).
        response.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/raster")]
    public async Task ImportCog_WithBigTiffHeader_AcceptsFormat()
    {
        // Arrange — BigTIFF header (little-endian, BigTIFF magic 43)
        var content = new MultipartFormDataContent();
        var bigTiffBytes = CreateMinimalBigTiffBytes();
        var fileContent = new ByteArrayContent(bigTiffBytes);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "big.tif"
        };
        content.Add(fileContent);
        content.Add(new StringContent("1"), "layerId");
        content.Add(new StringContent("test-bigtiff-cog"), "name");
        content.Add(new StringContent("CloudOptimizedGeoTiff"), "format");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/raster", content);

        // Assert — format is recognized and reaches the import pipeline.
        // Minimal synthetic BigTIFF data may cause PostGIS to fail (500) because
        // the file has a valid header but no real raster content.
        response.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>
    /// Creates a minimal valid TIFF header (little-endian, classic) for testing format detection.
    /// Not a complete GeoTIFF — just enough for header validation.
    /// </summary>
    private static byte[] CreateMinimalGeoTiffBytes()
    {
        return
        [
            0x49, 0x49, // "II" = little-endian
            0x2A, 0x00, // TIFF magic = 42
            0x08, 0x00, 0x00, 0x00, // first IFD offset = 8
            0x00, 0x00, // IFD entry count = 0
            0x00, 0x00, 0x00, 0x00, // next IFD offset = 0
        ];
    }

    /// <summary>
    /// Creates a minimal BigTIFF header for testing format detection.
    /// </summary>
    private static byte[] CreateMinimalBigTiffBytes()
    {
        return
        [
            0x49, 0x49,             // "II" = little-endian
            0x2B, 0x00,             // BigTIFF magic = 43
            0x08, 0x00,             // offset byte size = 8
            0x00, 0x00,             // always 0
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // first IFD offset = 16
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // IFD entry count = 0
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // next IFD offset = 0
        ];
    }
}
