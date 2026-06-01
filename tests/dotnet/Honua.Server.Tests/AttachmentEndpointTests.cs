// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for FeatureServer attachment endpoints.
/// Tests Issue #13 - Attachment CRUD operations implementation.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class AttachmentEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const long TestFeatureId = 1;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var storage = _fixture.GetService<ICloudFileStorage>();
        await AttachmentTestData.SeedAsync(_fixture.Postgres, storage, TestLayerId, TestFeatureId);
    }

    public async Task DisposeAsync()
    {
        var storage = _fixture.GetService<ICloudFileStorage>();
        await AttachmentTestData.CleanupAsync(_fixture.Postgres, storage, TestLayerId, TestFeatureId);
        await _fixture.DisposeAsync();
    }

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
        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentGroups.Should().HaveCount(1);
        result.AttachmentGroups[0].ParentObjectId.Should().Be(TestFeatureId);
        result.AttachmentGroups[0].AttachmentInfos.Should().HaveCount(2);
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

        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentInfos.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithObjectIdsAndReturnUrl_ReturnsGroupedResponseWithUrls()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectIds={TestFeatureId},999&returnUrl=true");

        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentGroups.Should().HaveCount(2);
        result.AttachmentInfos.Should().BeNull();

        var seededGroup = result.AttachmentGroups.Single(group => group.ParentObjectId == TestFeatureId);
        seededGroup.AttachmentInfos.Should().NotBeEmpty();
        seededGroup.AttachmentInfos.Should().OnlyContain(attachment => !string.IsNullOrWhiteSpace(attachment.Url));

        var emptyGroup = result.AttachmentGroups.Single(group => group.ParentObjectId == 999);
        emptyGroup.AttachmentInfos.Should().BeEmpty();
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
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithMalformedObjectIdsDelimiter_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectIds={TestFeatureId},");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
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
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);

        // Assert
        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AddAttachmentResponse);

        result.Should().NotBeNull();
        result!.AddAttachmentResult.Success.Should().BeTrue();
        result.AddAttachmentResult.ObjectId.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments")]
    public async Task AddAttachment_ThenGetAttachmentInfos_ReturnsNewAttachmentNotStaleCache()
    {
        // Regression: the per-feature attachment-infos GET must not be served from the
        // anonymous-only output cache, otherwise the ArcGIS SDK round-trip
        // attachments.add(oid) -> get_list(oid) returns the stale empty list cached by the
        // first get_list. Prime the cache, add, then re-read for the SAME oid.
        // GET /rest/services/test/FeatureServer/0/1/attachments
        var primeResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments?f=json");
        primeResponse.BeSuccessful();
        var primeContent = await primeResponse.Content.ReadAsStringAsync();
        var primed = JsonSerializer.Deserialize(primeContent, FeatureServerJsonContext.Default.AttachmentInfosResponse);
        var primedCount = primed!.AttachmentInfos.Length;

        var fileContent = "Round trip file content"u8.ToArray();
        var byteContent = new ByteArrayContent(fileContent);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { byteContent, "attachment", "roundtrip.pdf" }
        };

        var addResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);
        addResponse.BeSuccessful();

        var addContent = await addResponse.Content.ReadAsStringAsync();
        var added = JsonSerializer.Deserialize(addContent, FeatureServerJsonContext.Default.AddAttachmentResponse);
        var newId = added!.AddAttachmentResult.ObjectId;

        var afterResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments?f=json");
        afterResponse.BeSuccessful();
        var afterContent = await afterResponse.Content.ReadAsStringAsync();
        var after = JsonSerializer.Deserialize(afterContent, FeatureServerJsonContext.Default.AttachmentInfosResponse);

        after!.AttachmentInfos.Length.Should().Be(primedCount + 1);
        after.AttachmentInfos.Should().Contain(a => a.Id == newId);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_WithCanonicalFeatureRoute_ReturnsSuccess()
    {
        var fileContent = "Canonical route file content"u8.ToArray();
        var byteContent = new ByteArrayContent(fileContent);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

        var form = new MultipartFormDataContent
        {
            { new StringContent("test,canonical"), "keywords" },
            { byteContent, "attachment", "canonical.pdf" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);

        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AddAttachmentResponse);

        result.Should().NotBeNull();
        result!.AddAttachmentResult.Success.Should().BeTrue();
        result.AddAttachmentResult.ObjectId.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_WithoutFile_Returns400()
    {
        // Arrange
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
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
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_WithUnsupportedContentType_Returns415()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment",
            new StringContent("objectId=1", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.UnsupportedMediaType);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported Media Type");
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
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
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/updateAttachment", form);

        // Assert
        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.UpdateAttachmentResponse);

        result.Should().NotBeNull();
        result!.UpdateAttachmentResult.Success.Should().BeTrue();
        result.UpdateAttachmentResult.ObjectId.Should().Be(attachmentId);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    public async Task UpdateAttachment_WithReplacementFile_ReplacesStoredContent()
    {
        const long attachmentId = 1;
        var updatedBytes = "Updated attachment content"u8.ToArray();
        var fileContent = new ByteArrayContent(updatedBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent(attachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentId" },
            { new StringContent("updated,file"), "keywords" },
            { fileContent, "attachment", "updated.pdf" }
        };

        var updateResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/updateAttachment", form);

        updateResponse.BeSuccessful();

        var downloadResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments/{attachmentId}");

        downloadResponse.BeSuccessful();
        downloadResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");

        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        downloadedBytes.Should().Equal(updatedBytes);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    public async Task UpdateAttachment_WithCanonicalFeatureRoute_ReturnsSuccess()
    {
        const long attachmentId = 1;
        var form = new MultipartFormDataContent
        {
            { new StringContent(attachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentId" },
            { new StringContent("canonical,keywords"), "keywords" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/updateAttachment", form);

        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.UpdateAttachmentResponse);

        result.Should().NotBeNull();
        result!.UpdateAttachmentResult.Success.Should().BeTrue();
        result.UpdateAttachmentResult.ObjectId.Should().Be(attachmentId);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
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
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/updateAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    public async Task UpdateAttachment_WithUnsupportedContentType_Returns415()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/updateAttachment",
            new StringContent("objectId=1&attachmentId=1", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.UnsupportedMediaType);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported Media Type");
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
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
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        // Assert
        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.DeleteAttachmentsResponse);

        result.Should().NotBeNull();
        result!.DeleteAttachmentResults.Should().HaveCount(2);
        result.DeleteAttachmentResults.Should().OnlyContain(r => r.Success);
        result.DeleteAttachmentResults.Should().Contain(r => r.ObjectId == 1);
        result.DeleteAttachmentResults.Should().Contain(r => r.ObjectId == 2);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithCanonicalFeatureRoute_ReturnsSuccess()
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("1"), "attachmentIds" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.DeleteAttachmentsResponse);

        result.Should().NotBeNull();
        result!.DeleteAttachmentResults.Should().HaveCount(1);
        result.DeleteAttachmentResults[0].Success.Should().BeTrue();
        result.DeleteAttachmentResults[0].ObjectId.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithoutAttachmentIds_Returns400()
    {
        // Arrange
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithMalformedAttachmentIdsDelimiter_Returns400()
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent("999,"), "attachmentIds" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithInvalidAttachmentIdsToken_Returns400()
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent("999,abc"), "attachmentIds" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithUnsupportedContentType_Returns415()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments",
            new StringContent("objectId=1&attachmentIds=1", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.UnsupportedMediaType);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported Media Type");
    }

    [IntegrationTest]
    [Operation(Operations.DownloadAttachment)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments")]
    public async Task AttachmentInfos_WithValidFeature_ReturnsAttachmentInfos()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments?f=json");

        response.BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentInfosResponse);

        result.Should().NotBeNull();
        result!.AttachmentInfos.Should().HaveCount(2);
        result.AttachmentInfos.Should().Contain(a => a.Name == "test1.txt");
        result.AttachmentInfos.Should().Contain(a => a.Name == "test2.jpg");
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
        response.BeSuccessful();
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
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_FileTooLarge_Returns413()
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
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.RequestEntityTooLarge);

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
        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentInfos.Should().BeEmpty();
    }
}
