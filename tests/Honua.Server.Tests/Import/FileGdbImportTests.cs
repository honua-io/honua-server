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
/// Integration tests for the full FileGDB import (upload) endpoint.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class FileGdbImportTests : IAsyncLifetime
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
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithFileGdbArchive_ImportsFeaturesToTable()
    {
        // Arrange
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", "FileGdb", "testopenfilegdb.gdb.zip");
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "testopenfilegdb.gdb.zip"
        };
        content.Add(fileContent);
        content.Add(new StringContent("filegdb_import_test"), "TableName");
        content.Add(new StringContent("4326"), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("filegdb_import_test");
        responseContent.Should().Contain("FileGdb");
        responseContent.Should().Contain("\"success\":true");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithSparseFileGdb_ReturnsNoFeaturesFound()
    {
        // Arrange - sparse.gdb contains only system tables with no spatial features.
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", "FileGdb", "sparse.gdb.zip");
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "sparse.gdb.zip"
        };
        content.Add(fileContent);
        content.Add(new StringContent("filegdb_sparse_import_test"), "TableName");
        content.Add(new StringContent("4326"), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert - sparse GDB has no features, so import reports no features found.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("filegdb_sparse_import_test");
        responseContent.Should().Contain("FileGdb");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithFileGdbArchive_OverwriteExisting_Succeeds()
    {
        // Arrange
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", "FileGdb", "testopenfilegdb.gdb.zip");
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        // First import
        var content1 = new MultipartFormDataContent();
        var fileContent1 = new ByteArrayContent(fileBytes);
        fileContent1.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        fileContent1.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "testopenfilegdb.gdb.zip"
        };
        content1.Add(fileContent1);
        content1.Add(new StringContent("filegdb_overwrite_test"), "TableName");
        content1.Add(new StringContent("4326"), "TargetSrid");
        content1.Add(new StringContent("true"), "OverwriteExisting");

        var response1 = await _client.PostAsync("/api/v1/admin/import/upload", content1);
        response1.BeSuccessful();

        // Second import with overwrite
        var content2 = new MultipartFormDataContent();
        var fileContent2 = new ByteArrayContent(fileBytes);
        fileContent2.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        fileContent2.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "testopenfilegdb.gdb.zip"
        };
        content2.Add(fileContent2);
        content2.Add(new StringContent("filegdb_overwrite_test"), "TableName");
        content2.Add(new StringContent("4326"), "TargetSrid");
        content2.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response2 = await _client.PostAsync("/api/v1/admin/import/upload", content2);

        // Assert
        response2.BeSuccessful();
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response2.Content.ReadAsStringAsync();
        responseContent.Should().Contain("filegdb_overwrite_test");
        responseContent.Should().Contain("FileGdb");
        responseContent.Should().Contain("\"success\":true");
    }
}
