// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the FeatureServer operations that complete Esri REST
/// coverage with honest, specification-shaped responses: contingent values, shared
/// templates, HTML pop-ups, the image resource, layer assets, 3D geometry, and
/// layer-metadata updates. These surfaces have no backing data model yet, so the
/// assertions verify the documented spec shape and honest status codes rather than
/// fabricated success.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerNotImplementedOperationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent EmptyJsonBody()
        => new("{}", Encoding.UTF8, "application/json");

    // ----- queryContingentValues -----

    [IntegrationTest]
    [Operation(Operations.QueryContingentValues)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryContingentValues")]
    public async Task QueryContingentValues_ValidService_ReturnsSpecShapedDocument()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/queryContingentValues?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("typeCodes", out var typeCodes).Should().BeTrue();
        typeCodes.ValueKind.Should().Be(JsonValueKind.Array);
        root.TryGetProperty("contingentValuesDefinitions", out var defs).Should().BeTrue();
        defs.ValueKind.Should().Be(JsonValueKind.Array);
        defs.GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.QueryContingentValues)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryContingentValues")]
    public async Task QueryContingentValues_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/queryContingentValues?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- sharedTemplates -----

    [IntegrationTest]
    [Operation(Operations.SharedTemplates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/sharedTemplates")]
    public async Task SharedTemplates_ValidService_ReturnsEmptyCollection()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("sharedTemplates", out var templates).Should().BeTrue();
        templates.ValueKind.Should().Be(JsonValueKind.Array);
        templates.GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.SharedTemplates)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/sharedTemplates/query")]
    public async Task SharedTemplatesQuery_ValidService_ReturnsEmptyCollection()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates/query?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("sharedTemplates", out var templates).Should().BeTrue();
        templates.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.SharedTemplates)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/sharedTemplates/add")]
    public async Task SharedTemplatesAdd_ValidService_ReturnsBadRequestHonestly()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates/add",
            EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.SharedTemplates)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/sharedTemplates/update")]
    public async Task SharedTemplatesUpdate_ValidService_ReturnsBadRequestHonestly()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates/update",
            EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.SharedTemplates)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/sharedTemplates/delete")]
    public async Task SharedTemplatesDelete_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/sharedTemplates/delete",
            EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- htmlPopup -----

    [IntegrationTest]
    [Operation(Operations.HtmlPopup)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/htmlPopup")]
    public async Task HtmlPopup_ValidService_ReturnsPopupTypeNone()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/htmlPopup?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("htmlPopupType").GetString()
            .Should().Be("esriServerHTMLPopupTypeNone");
    }

    [IntegrationTest]
    [Operation(Operations.HtmlPopup)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/htmlPopup")]
    public async Task HtmlPopup_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/htmlPopup?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- image -----

    [IntegrationTest]
    [Operation(Operations.Image)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/image")]
    public async Task ServiceImage_ValidService_ReturnsNotFoundHonestly()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/image");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Image)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/image")]
    public async Task ServiceImage_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/image");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- hasAssets -----

    [IntegrationTest]
    [Operation(Operations.HasAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/hasAssets")]
    public async Task HasAssets_ValidLayer_ReturnsFalse()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/hasAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("hasAssets").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.HasAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/hasAssets")]
    public async Task HasAssets_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/hasAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- queryAssets -----

    [IntegrationTest]
    [Operation(Operations.QueryAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAssets")]
    public async Task QueryAssets_ValidLayer_ReturnsEmptyCollection()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("assets", out var assets).Should().BeTrue();
        assets.ValueKind.Should().Be(JsonValueKind.Array);
        assets.GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.QueryAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAssets")]
    public async Task QueryAssets_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/queryAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- cleanupAssets -----

    [IntegrationTest]
    [Operation(Operations.CleanupAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/cleanupAssets")]
    public async Task CleanupAssets_ValidLayer_ReturnsSuccessNoOp()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/cleanupAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("cleanedAssetCount").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.CleanupAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/cleanupAssets")]
    public async Task CleanupAssets_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/cleanupAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- uploadAssets -----

    [IntegrationTest]
    [Operation(Operations.UploadAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/uploadAssets")]
    public async Task UploadAssets_ValidLayer_ReturnsBadRequestHonestly()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/uploadAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.UploadAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/uploadAssets")]
    public async Task UploadAssets_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/uploadAssets?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- convert3D -----

    [IntegrationTest]
    [Operation(Operations.Convert3D)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/convert3D")]
    public async Task Convert3D_ValidLayer_ReturnsBadRequestHonestly()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/convert3D?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Convert3D)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/convert3D")]
    public async Task Convert3D_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/convert3D?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- query3D -----

    [IntegrationTest]
    [Operation(Operations.Query3D)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query3D")]
    public async Task Query3D_ValidLayer_ReturnsBadRequestHonestly()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query3D?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query3D)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query3D")]
    public async Task Query3D_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/query3D?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- metadata/update -----

    [IntegrationTest]
    [Operation(Operations.UpdateMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/metadata/update")]
    public async Task UpdateMetadata_ValidLayer_ReturnsBadRequestHonestly()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/metadata/update",
            EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateMetadata)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/metadata/update")]
    public async Task UpdateMetadata_InvalidService_ReturnsNotFound()
    {
        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/0/metadata/update",
            EmptyJsonBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
