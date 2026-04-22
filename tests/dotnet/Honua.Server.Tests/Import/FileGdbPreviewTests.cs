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
/// Integration tests for FileGDB preview and format discovery endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
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
    public async Task GetFormats_IncludesFileGdbFormat()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/formats");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain(".gdb");
        responseContent.Should().Contain(".gdb.zip");
        responseContent.Should().Contain("File Geodatabase");
    }
}
