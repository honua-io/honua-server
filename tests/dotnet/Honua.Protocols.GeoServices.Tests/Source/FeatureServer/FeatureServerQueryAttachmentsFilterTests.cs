// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the Esri <c>queryAttachments</c> parameter surface
/// (<c>attachmentTypes</c>, <c>keywords</c>, <c>size</c>, <c>definitionExpression</c>,
/// and the explicit <c>globalIds</c> rejection). The seed fixture attaches two files to
/// the test feature: <c>test1.txt</c> (text/plain, 17 bytes, keywords "test,document")
/// and <c>test2.jpg</c> (image/jpeg, 4 bytes, keywords "test,image").
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryAttachmentsFilterTests : IAsyncLifetime
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

    private async Task<AttachmentQueryResponse> QueryAsync(string queryString)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectIds={TestFeatureId}&{queryString}");

        response.BeSuccessful();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);
        result.Should().NotBeNull();
        return result!;
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithAttachmentTypesMime_FiltersByContentType()
    {
        var result = await QueryAsync("attachmentTypes=image/jpeg");

        result.AttachmentInfos.Should().ContainSingle();
        result.AttachmentInfos![0].Name.Should().Be("test2.jpg");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithAttachmentTypesExtension_FiltersByFilenameExtension()
    {
        var result = await QueryAsync("attachmentTypes=txt");

        result.AttachmentInfos.Should().ContainSingle();
        result.AttachmentInfos![0].Name.Should().Be("test1.txt");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithKeywords_FiltersByKeyword()
    {
        var result = await QueryAsync("keywords=image");

        result.AttachmentInfos.Should().ContainSingle();
        result.AttachmentInfos![0].Name.Should().Be("test2.jpg");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithSharedKeyword_ReturnsAllMatching()
    {
        var result = await QueryAsync("keywords=test");

        result.AttachmentInfos.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithSizeRange_FiltersBySizeBounds()
    {
        // test2.jpg is 4 bytes; test1.txt is 17 bytes. Upper-bound the result to <= 10 bytes.
        var result = await QueryAsync("size=0,10");

        result.AttachmentInfos.Should().ContainSingle();
        result.AttachmentInfos![0].Name.Should().Be("test2.jpg");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithLowerSizeBound_FiltersBySizeBounds()
    {
        var result = await QueryAsync("size=10,");

        result.AttachmentInfos.Should().ContainSingle();
        result.AttachmentInfos![0].Name.Should().Be("test1.txt");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithInvalidSize_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?objectIds={TestFeatureId}&size=abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithGlobalIds_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments?globalIds=abc");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("globalIds");
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithMatchingDefinitionExpression_ReturnsAttachments()
    {
        // The seeded feature satisfies a tautological WHERE clause, so its attachments remain.
        var result = await QueryAsync("definitionExpression=" + Uri.EscapeDataString("objectid >= 0"));

        result.AttachmentInfos.Should().HaveCount(2);
    }

    [IntegrationTest]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task QueryAttachments_WithExcludingDefinitionExpression_ReturnsNoAttachments()
    {
        // A WHERE clause the feature cannot satisfy filters its parent out entirely.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/queryAttachments" +
            $"?objectIds={TestFeatureId}&definitionExpression={Uri.EscapeDataString("objectid < 0")}");

        response.BeSuccessful();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.AttachmentQueryResponse);

        result.Should().NotBeNull();
        result!.AttachmentGroups.Should().BeEmpty();
    }
}
