// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for FileGDB preview and format discovery endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class FileGdbPreviewTests : IAsyncLifetime
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
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_WithFileGdbArchive_ReturnsPreviewWithFeatures()
    {
        // Arrange
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", "FileGdb", "testopenfilegdb.gdb.zip");
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "testopenfilegdb.gdb.zip"
        };
        content.Add(fileContent);

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("FileGdb");
        responseContent.Should().Contain("totalFeatureCount");
        responseContent.Should().Contain("availableLayers");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_WithSparseFileGdb_ReturnsPreviewWithFeatures()
    {
        // Arrange
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", "FileGdb", "sparse.gdb.zip");
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "sparse.gdb.zip"
        };
        content.Add(fileContent);

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("FileGdb");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/formats")]
    public async Task GetFormats_IncludesOnlyZippedFileGdbFormat()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/formats");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseContent);
        var extensions = document.RootElement
            .GetProperty("supportedExtensions")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        extensions.Should().Contain(".gdb.zip");
        extensions.Should().NotContain(".gdb");

        var descriptions = document.RootElement.GetProperty("formatDescriptions");
        descriptions.TryGetProperty(".gdb", out _).Should().BeFalse();
        descriptions.GetProperty(".gdb.zip").GetString().Should().Contain("Zipped File Geodatabase");
    }
}
