// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for FeatureServer attachment endpoints.
/// Tests Issue #13 - Attachment CRUD operations implementation.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database.CoreFeatureStore")]
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
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_AlwaysEmitsParentGlobalIdKey()
    {
        // Regression: the ArcGIS API for Python AttachmentManager.search() reads
        // group['parentGlobalId'] unconditionally and raises KeyError when the key
        // is absent. Esri always emits the key (empty string when there is no
        // global-id column). Assert the raw JSON carries the key on every group,
        // including groups with no attachments.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectIds={TestFeatureId},999");

        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var groups = document.RootElement.GetProperty("attachmentGroups");
        groups.GetArrayLength().Should().Be(2);
        foreach (var group in groups.EnumerateArray())
        {
            group.TryGetProperty("parentGlobalId", out var parentGlobalId).Should().BeTrue(
                "every attachment group must carry the parentGlobalId key");
            parentGlobalId.ValueKind.Should().Be(JsonValueKind.String);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithPost_ReturnsAttachments()
    {
        using var requestContent = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectId={TestFeatureId}",
            requestContent);

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

        // #4404: a non-blank URL was never dereferenced, so a wrong or unroutable URL passed.
        // Fetch the advertised URL for the seeded text attachment and compare the bytes.
        var textAttachment = seededGroup.AttachmentInfos.Single(attachment => attachment.Name == "test1.txt");
        var urlResponse = await _fixture.Client.GetAsync(ToRelativeUri(textAttachment.Url!));
        urlResponse.BeSuccessful();
        (await urlResponse.Content.ReadAsByteArrayAsync()).Should().Equal(AttachmentTestData.SeededTextFileBytes.ToArray());

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
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithMalformedObjectIdsDelimiter_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectIds={TestFeatureId},");

        await response.AssertGeoServicesErrorAsync(400);
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

        using var form = new MultipartFormDataContent
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

        // #4404: a successful insert over a missing or garbled blob used to pass here.
        // Read the bytes back through the public download route.
        var downloaded = await DownloadAttachmentBytesAsync(result.AddAttachmentResult.ObjectId);
        downloaded.Should().Equal(fileContent);
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

        using var form = new MultipartFormDataContent
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

        using var form = new MultipartFormDataContent
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

        // #4404: a successful insert over a missing or garbled blob used to pass here.
        // Read the bytes back through the public download route.
        var downloaded = await DownloadAttachmentBytesAsync(result.AddAttachmentResult.ObjectId);
        downloaded.Should().Equal(fileContent);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_WithoutFile_Returns400()
    {
        // Arrange
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);

        // Assert
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_WithInvalidMimeType_Returns400()
    {
        // Arrange
        var fileContent = "Executable content"u8.ToArray();
        using var form = new MultipartFormDataContent
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
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_WithUnsupportedContentType_Returns415()
    {
        using var requestContent = new StringContent("objectId=1", Encoding.UTF8, "text/plain");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment",
            requestContent);

        // #4404: 500 was previously accepted here, so an unhandled crash passed a media-type
        // test. The endpoint must answer an unsupported media type with 415.
        await response.AssertGeoServicesErrorAsync(415);
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
        using var form = new MultipartFormDataContent
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

        using var form = new MultipartFormDataContent
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
        using var form = new MultipartFormDataContent
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
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent(nonExistentAttachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentId" },
            { new StringContent("keywords"), "keywords" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/updateAttachment", form);

        // Assert
        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    public async Task UpdateAttachment_WithUnsupportedContentType_Returns415()
    {
        using var requestContent = new StringContent("objectId=1&attachmentId=1", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/updateAttachment",
            requestContent);

        // #4404: 500 was previously accepted here, so an unhandled crash passed a media-type
        // test. The endpoint must answer an unsupported media type with 415.
        await response.AssertGeoServicesErrorAsync(415);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported Media Type");
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithValidIds_ReturnsSuccess()
    {
        // Arrange
        using var form = new MultipartFormDataContent
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
        using var form = new MultipartFormDataContent
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
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        // Assert
        ((int)response.StatusCode).Should().Be(200);
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_ForMissingAttachment_ReturnsFailureErrorObject()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent("999"), "attachmentIds" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        response.Be200Ok();
        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(), FeatureServerJsonContext.Default.DeleteAttachmentsResponse);
        result.Should().NotBeNull();
        result!.DeleteAttachmentResults.Should().ContainSingle();
        var deleteResult = result.DeleteAttachmentResults[0];
        deleteResult.Success.Should().BeFalse();
        deleteResult.Error.Should().NotBeNull();
        deleteResult.Error!.Code.Should().Be(1000);
        deleteResult.Error.Description.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithMalformedAttachmentIdsDelimiter_Returns400()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent("999,"), "attachmentIds" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithInvalidAttachmentIdsToken_Returns400()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent("999,abc"), "attachmentIds" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_WithUnsupportedContentType_Returns415()
    {
        using var requestContent = new StringContent("objectId=1&attachmentIds=1", Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments",
            requestContent);

        // #4404: 500 was previously accepted here, so an unhandled crash passed a media-type
        // test. The endpoint must answer an unsupported media type with 415.
        await response.AssertGeoServicesErrorAsync(415);
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

        // #4404: `NotBeEmpty` passed for a truncated or wrong object. The seeded content is
        // known exactly (AttachmentTestData), so assert it exactly.
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(AttachmentTestData.SeededTextFileBytes.ToArray());
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
        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_FileTooLarge_Returns413()
    {
        // Arrange - Create a 15MB file (larger than default 10MB limit)
        var largeContent = new byte[15 * 1024 * 1024];
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new ByteArrayContent(largeContent), "attachment", "large.txt" }
        };

        // Act
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);

        // Assert
        await response.AssertGeoServicesErrorAsync(413);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("exceeds maximum allowed size");
    }

    /// <summary>
    /// The core attachment promise: what goes in comes back out, byte for byte, through the
    /// public API. Before this test exactly one assertion in the whole endpoint suite compared
    /// bytes, and it was on the replace path — an <c>addAttachment</c> that committed a row
    /// over a garbled or truncated blob passed (honua-server#4404).
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_ThenDownload_RoundTripsBinaryBytesExactly()
    {
        // A deterministic binary payload that is not valid UTF-8 and contains embedded NULs,
        // CR and LF, so any text decoding, newline translation or truncation shows up as a
        // byte mismatch rather than passing a "non-empty" check.
        var payload = BuildBinaryPayload(4096);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        var attachmentId = await AddAttachmentAsync(payload, "roundtrip.bin", "application/octet-stream");

        var downloaded = await DownloadAttachmentBytesAsync(attachmentId);
        downloaded.Should().HaveCount(payload.Length, "a truncated object must not pass");
        downloaded.Should().Equal(payload);
        Convert.ToHexString(SHA256.HashData(downloaded)).Should().Be(expectedHash);

        // The indexed size column must agree with the object that was actually stored, so a
        // drifted size cannot make a size filter lie.
        var info = await GetAttachmentInfoAsync(attachmentId);
        info.Size.Should().Be(payload.Length);
        info.ContentType.Should().Be("application/octet-stream");
        info.Name.Should().Be("roundtrip.bin");
    }

    /// <summary>
    /// <c>deleteAttachments</c> must remove both halves of the two-store write. The existing
    /// test asserted only that the response said success and never re-queried, making it
    /// strictly weaker than the store-level test one layer down (honua-server#4404).
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task DeleteAttachments_RemovesTheMetadataRowAndTheStoredObject()
    {
        var payload = BuildBinaryPayload(512);
        var attachmentId = await AddAttachmentAsync(payload, "doomed.bin", "application/octet-stream");

        // Capture the storage path while the row still exists; after the delete there is no
        // way to learn which object should have gone.
        var storagePath = await GetStoragePathAsync(attachmentId);
        var storage = _fixture.GetService<ICloudFileStorage>();
        (await storage.ExistsAsync(storagePath)).Should().BeTrue("precondition: the object was stored");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent(attachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentIds" }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", form);
        response.BeSuccessful();

        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            FeatureServerJsonContext.Default.DeleteAttachmentsResponse);
        result!.DeleteAttachmentResults.Should().OnlyContain(r => r.Success);

        // Metadata row is gone: neither the listing nor a direct download sees it.
        var infos = await ListAttachmentInfosAsync();
        infos.Should().NotContain(info => info.Id == attachmentId);

        var download = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments/{attachmentId}");
        await download.AssertGeoServicesErrorAsync(404);

        // And the object is gone from storage, not merely unreferenced.
        (await storage.ExistsAsync(storagePath)).Should().BeFalse(
            "a deleted attachment must not leave its object behind");
    }

    /// <summary>
    /// A rejected upload must leave neither a row nor a partial object. The 413 and 415 tests
    /// asserted only the status, so a handler that streamed the body to storage before
    /// rejecting it would have passed (honua-server#4404).
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_RejectedUploads_LeaveNoPartialObjectAndNoRow()
    {
        var storage = _fixture.GetService<ICloudFileStorage>();
        var objectsBefore = await ListStoredObjectIdsAsync(storage);
        var infosBefore = (await ListAttachmentInfosAsync()).Select(info => info.Id).ToHashSet();

        // Oversize: 15 MB against the 10 MB default limit.
        using (var oversize = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new ByteArrayContent(new byte[15 * 1024 * 1024]), "attachment", "large.txt" }
        })
        {
            var response = await _fixture.Client.PostAsync(
                $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", oversize);
            await response.AssertGeoServicesErrorAsync(413);
        }

        // Unsupported media type on the request itself (same shape as
        // AddAttachment_WithUnsupportedContentType_Returns415).
        using (var unsupported = new StringContent("objectId=1", Encoding.UTF8, "text/plain"))
        {
            var response = await _fixture.Client.PostAsync(
                $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", unsupported);
            await response.AssertGeoServicesErrorAsync(415);
        }

        (await ListStoredObjectIdsAsync(storage)).Should().BeEquivalentTo(
            objectsBefore, "a rejected upload must not leave a partial object in storage");
        (await ListAttachmentInfosAsync()).Select(info => info.Id).Should().BeEquivalentTo(
            infosBefore, "a rejected upload must not leave a metadata row");
    }

    /// <summary>
    /// Two uploads to the same feature at the same time must both land as distinct, complete
    /// attachments — no id collision, no crossed blobs. No attachment test had any concurrency
    /// at all before this (honua-server#4404, see also #4250).
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_ConcurrentUploadsToTheSameFeature_BothPersistWithTheirOwnBytes()
    {
        // Distinct payloads of different lengths: a crossed blob shows up as both a byte and a
        // length mismatch, and an id collision shows up as a duplicate object id.
        var first = BuildBinaryPayload(1024, seed: 11);
        var second = BuildBinaryPayload(2048, seed: 29);

        var uploads = await Task.WhenAll(
            AddAttachmentAsync(first, "concurrent-a.bin", "application/octet-stream"),
            AddAttachmentAsync(second, "concurrent-b.bin", "application/octet-stream"));

        uploads.Should().OnlyHaveUniqueItems("two concurrent uploads must not share one attachment id");

        (await DownloadAttachmentBytesAsync(uploads[0])).Should().Equal(first);
        (await DownloadAttachmentBytesAsync(uploads[1])).Should().Equal(second);

        var infos = await ListAttachmentInfosAsync();
        infos.Single(info => info.Id == uploads[0]).Size.Should().Be(first.Length);
        infos.Single(info => info.Id == uploads[1]).Size.Should().Be(second.Length);
    }

    /// <summary>
    /// An upload racing the delete of a different attachment on the same feature must leave
    /// both outcomes intact: the survivor keeps its exact bytes and the deleted attachment
    /// takes its object with it (honua-server#4404).
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    public async Task AddAttachment_RacingADeleteOnTheSameFeature_LeavesBothStoresConsistent()
    {
        var doomed = BuildBinaryPayload(768, seed: 41);
        var doomedId = await AddAttachmentAsync(doomed, "racing-doomed.bin", "application/octet-stream");
        var doomedPath = await GetStoragePathAsync(doomedId);

        var arriving = BuildBinaryPayload(1536, seed: 53);

        using var deleteForm = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { new StringContent(doomedId.ToString(CultureInfo.InvariantCulture)), "attachmentIds" }
        };

        var uploadTask = AddAttachmentAsync(arriving, "racing-arriving.bin", "application/octet-stream");
        var deleteTask = _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/deleteAttachments", deleteForm);

        await Task.WhenAll(uploadTask, deleteTask);

        var arrivingId = await uploadTask;
        (await deleteTask).BeSuccessful();

        // The arriving attachment survives intact.
        (await DownloadAttachmentBytesAsync(arrivingId)).Should().Equal(arriving);

        // The deleted one is gone from both stores.
        var infos = await ListAttachmentInfosAsync();
        infos.Should().NotContain(info => info.Id == doomedId);
        infos.Should().Contain(info => info.Id == arrivingId);

        var storage = _fixture.GetService<ICloudFileStorage>();
        (await storage.ExistsAsync(doomedPath)).Should().BeFalse();
    }

    /// <summary>
    /// Attachment authorization off the development bypass. Every other test in this file runs
    /// as the bypass principal, so cross-service isolation on the attachment routes had no
    /// evidence at all — a grep for "attachment" across both security test projects returned
    /// nothing (honua-server#4404).
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.DownloadAttachment)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}")]
    public async Task DownloadAttachment_WithoutAuthentication_IsRejectedBeforeReadingTheObject()
    {
        // Disable the dev-auth bypass the shared fixture enables, so the authentication
        // requirement on the attachment routes is genuinely enforced.
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();

        try
        {
            var storage = fixture.GetService<ICloudFileStorage>();
            await AttachmentTestData.SeedAsync(fixture.Postgres, storage, TestLayerId, TestFeatureId);

            try
            {
                var anonymous = fixture.CreateClient();

                var download = await anonymous.GetAsync(
                    $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments/1");
                download.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                    "an unauthenticated principal must not receive attachment bytes");
                (await download.Content.ReadAsByteArrayAsync()).Should().NotEqual(
                    AttachmentTestData.SeededTextFileBytes.ToArray(),
                    "the rejection must happen before the object is read");

                var query = await anonymous.GetAsync(
                    $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectId={TestFeatureId}");
                query.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                // The same request with credentials succeeds, so the 401 above is authorization
                // and not a broken route.
                var authorized = await fixture.CreateAdminClient().GetAsync(
                    $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments/1");
                authorized.StatusCode.Should().Be(HttpStatusCode.OK);
                (await authorized.Content.ReadAsByteArrayAsync()).Should().Equal(
                    AttachmentTestData.SeededTextFileBytes.ToArray());
            }
            finally
            {
                await AttachmentTestData.CleanupAsync(fixture.Postgres, storage, TestLayerId, TestFeatureId);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
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
    /// <summary>
    /// Deterministic binary payload: not valid UTF-8, contains NUL/CR/LF, and is a pure
    /// function of (length, seed) so an expected value can be recomputed rather than
    /// snapshotted from the server's own output.
    /// </summary>
    private static byte[] BuildBinaryPayload(int length, int seed = 7)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++)
        {
            payload[index] = (byte)((index * 37 + seed * 101 + (index % 13)) % 256);
        }

        // Force the bytes that a text or newline-translating codepath would corrupt.
        if (length >= 4)
        {
            payload[0] = 0x00;
            payload[1] = 0xFF;
            payload[2] = 0x0D;
            payload[3] = 0x0A;
        }

        return payload;
    }

    private async Task<long> AddAttachmentAsync(byte[] payload, string filename, string contentType)
    {
        var body = new ByteArrayContent(payload);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(TestFeatureId.ToString(CultureInfo.InvariantCulture)), "objectId" },
            { body, "attachment", filename }
        };

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/addAttachment", form);
        response.BeSuccessful();

        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            FeatureServerJsonContext.Default.AddAttachmentResponse);
        result.Should().NotBeNull();
        result!.AddAttachmentResult.Success.Should().BeTrue();
        result.AddAttachmentResult.ObjectId.Should().BeGreaterThan(0);
        return result.AddAttachmentResult.ObjectId;
    }

    private async Task<byte[]> DownloadAttachmentBytesAsync(long attachmentId)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/{TestFeatureId}/attachments/{attachmentId}");
        response.BeSuccessful();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<AttachmentInfo[]> ListAttachmentInfosAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectId={TestFeatureId}");
        response.BeSuccessful();

        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            FeatureServerJsonContext.Default.AttachmentQueryResponse);
        return result?.AttachmentInfos ?? [];
    }

    private async Task<AttachmentInfo> GetAttachmentInfoAsync(long attachmentId)
    {
        var infos = await ListAttachmentInfosAsync();
        return infos.Should().ContainSingle(info => info.Id == attachmentId).Subject;
    }

    /// <summary>
    /// Reads the storage identifier of an attachment straight from the metadata table, so a
    /// test can assert on the object after the row that names it has been deleted.
    /// </summary>
    private async Task<string> GetStoragePathAsync(long attachmentId)
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT storage_path FROM honua.attachments WHERE id = @id";
        command.Parameters.AddWithValue("id", attachmentId);
        var value = await command.ExecuteScalarAsync();
        value.Should().BeOfType<string>("attachment {0} must have a storage path", attachmentId);
        return (string)value!;
    }

    private static async Task<HashSet<string>> ListStoredObjectIdsAsync(ICloudFileStorage storage)
    {
        var files = await storage.ListFilesAsync(maxResults: 5000);
        return files.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The <c>returnUrl</c> form emits either an absolute or a base-path-relative URL
    /// depending on host configuration; the test client is rooted at the test server, so
    /// reduce it to a path the client can fetch.
    /// </summary>
    private static string ToRelativeUri(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : url;
}
