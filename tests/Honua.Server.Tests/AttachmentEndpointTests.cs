// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Xunit;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for FeatureServer attachment endpoints.
/// Tests Issue #13 - Attachment CRUD operations implementation.
/// </summary>
[Protocol(Protocols.FeatureServer)]
[Collection("Database")]
public sealed class AttachmentEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const long TestFeatureId = 123;

    public async Task InitializeAsync()
    {
        // Replace services with test implementations
        _fixture.ReplaceService<ILayerCatalog>(new TestLayerCatalog());
        _fixture.ReplaceService<IFeatureStore>(new TestFeatureStore());

        var testAttachmentStore = new TestAttachmentStore();
        await testAttachmentStore.SeedTestData(TestLayerId, TestFeatureId);
        _fixture.ReplaceService<IAttachmentStore>(testAttachmentStore);

        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithValidFeature_ReturnsAttachments()
    {
        // Arrange
        // Test data is already seeded during InitializeAsync()

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectId={TestFeatureId}");

        // Assert
        response.Should().BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentInfos.Should().HaveCount(2);
        result.AttachmentInfos.Should().Contain(a => a.Name == "test1.txt");
        result.AttachmentInfos.Should().Contain(a => a.Name == "test2.jpg");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithPost_ReturnsAttachments()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectId={TestFeatureId}",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.Should().BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentInfos.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithoutObjectId_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment")]
    public async Task AddAttachment_WithValidFile_ReturnsSuccess()
    {
        // Arrange
        var fileContent = "Test file content"u8.ToArray();
        var byteContent = new ByteArrayContent(fileContent);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent("test,keywords"), "keywords" },
            { byteContent, "attachment", "test.pdf" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addAttachment", form);

        // Assert
        response.Should().BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AddAttachmentResponse);

        result.Should().NotBeNull();
        result!.AddAttachmentResult.Success.Should().BeTrue();
        result.AddAttachmentResult.ObjectId.Should().Be(TestFeatureId);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment")]
    public async Task AddAttachment_WithoutFile_Returns400()
    {
        // Arrange
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment")]
    public async Task AddAttachment_WithInvalidMimeType_Returns400()
    {
        // Arrange
        var fileContent = "Executable content"u8.ToArray();
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" }
        };

        var fileContent2 = new ByteArrayContent(fileContent);
        fileContent2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-executable");
        form.Add(fileContent2, "attachment", "malicious.exe");

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateAttachment")]
    public async Task UpdateAttachment_WithValidData_ReturnsSuccess()
    {
        // Arrange
        const long attachmentId = 1;
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent(attachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentId" },
            { new StringContent("updated,keywords"), "keywords" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateAttachment", form);

        // Assert
        response.Should().BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.UpdateAttachmentResponse);

        result.Should().NotBeNull();
        result!.UpdateAttachmentResult.Success.Should().BeTrue();
        result.UpdateAttachmentResult.ObjectId.Should().Be(TestFeatureId);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateAttachment")]
    public async Task UpdateAttachment_WithNonExistentAttachment_Returns404()
    {
        // Arrange
        const long nonExistentAttachmentId = 99999;
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent(nonExistentAttachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentId" },
            { new StringContent("keywords"), "keywords" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteAttachments")]
    public async Task DeleteAttachments_WithValidIds_ReturnsSuccess()
    {
        // Arrange
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent("1,2"), "attachmentIds" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteAttachments", form);

        // Assert
        response.Should().BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.DeleteAttachmentsResponse);

        result.Should().NotBeNull();
        result!.DeleteAttachmentResults.Should().HaveCount(2);
        result.DeleteAttachmentResults.Should().OnlyContain(r => r.Success);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteAttachments")]
    public async Task DeleteAttachments_WithoutAttachmentIds_Returns400()
    {
        // Arrange
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteAttachments", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.DownloadAttachment)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}")]
    public async Task DownloadAttachment_WithValidId_ReturnsFileContent()
    {
        // Arrange
        const long attachmentId = 1;

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments/{attachmentId}");

        // Assert
        response.Should().BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.DownloadAttachment)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}")]
    public async Task DownloadAttachment_WithNonExistentId_Returns404()
    {
        // Arrange
        const long nonExistentAttachmentId = 99999;

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments/{nonExistentAttachmentId}");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment")]
    public async Task AddAttachment_FileTooLarge_Returns400()
    {
        // Arrange - Create a 15MB file (larger than default 10MB limit)
        var largeContent = new byte[15 * 1024 * 1024];
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new ByteArrayContent(largeContent), "attachment", "large.txt" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("exceeds maximum allowed size");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_FeatureWithNoAttachments_ReturnsEmptyArray()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectId=999");

        // Assert
        response.Should().BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentInfos.Should().BeEmpty();
    }
}
