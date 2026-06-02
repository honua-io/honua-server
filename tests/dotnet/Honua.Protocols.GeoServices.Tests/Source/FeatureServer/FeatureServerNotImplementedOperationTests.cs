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
/// <remarks>
/// The endpoints are intentionally exercised in a handful of grouped test methods
/// (service reads, service rejections, layer reads, layer rejections) rather than
/// one method per endpoint. Each <see cref="WebAppFixture"/> initialization seeds an
/// isolated Postgres schema (several seconds), so grouping keeps this file's
/// contribution to the FeatureServer test shard to a few fixture inits while still
/// sending a real HTTP request to every registered route — including both the
/// happy-path and the invalid-service (404) branch for each operation. Each route is
/// written as a full literal path so the EndpointRegistry HTTP-backing drift guard
/// can anchor every registry entry to a same-method request in this file.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerNotImplementedOperationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent EmptyJsonBody()
        => new("{}", Encoding.UTF8, "application/json");

    // ----- Service-level read operations (GET, spec-shaped 200) -----

    [IntegrationTest]
    [Operation(Operations.QueryContingentValues, Operations.SharedTemplates, Operations.HtmlPopup, Operations.Image)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/queryContingentValues")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/sharedTemplates")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/sharedTemplates/query")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/htmlPopup")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/image")]
    public async Task ServiceLevelReadOperations_ReturnSpecShapedResponsesAndHonest404s()
    {
        // queryContingentValues: spec-shaped empty document.
        var contingent = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/queryContingentValues?f=json");
        contingent.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await contingent.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            root.TryGetProperty("typeCodes", out var typeCodes).Should().BeTrue();
            typeCodes.ValueKind.Should().Be(JsonValueKind.Array);
            root.TryGetProperty("contingentValuesDefinitions", out var defs).Should().BeTrue();
            defs.ValueKind.Should().Be(JsonValueKind.Array);
            defs.GetArrayLength().Should().Be(0);
        }

        // sharedTemplates: empty collection.
        var shared = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates?f=json");
        shared.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await shared.Content.ReadAsStringAsync()))
        {
            document.RootElement.TryGetProperty("sharedTemplates", out var templates).Should().BeTrue();
            templates.ValueKind.Should().Be(JsonValueKind.Array);
            templates.GetArrayLength().Should().Be(0);
        }

        // sharedTemplates/query: empty collection.
        var sharedQuery = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates/query?f=json");
        sharedQuery.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await sharedQuery.Content.ReadAsStringAsync()))
        {
            document.RootElement.TryGetProperty("sharedTemplates", out var templates).Should().BeTrue();
            templates.ValueKind.Should().Be(JsonValueKind.Array);
        }

        // htmlPopup: always type none until author content is supported.
        var htmlPopup = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/htmlPopup?f=json");
        htmlPopup.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await htmlPopup.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetProperty("htmlPopupType").GetString()
                .Should().Be("esriServerHTMLPopupTypeNone");
        }

        // image: honest 404; this server stores no FeatureServer image resource.
        var image = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/image");
        image.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Invalid-service branch (404) for each GET surface.
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/queryContingentValues?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/sharedTemplates?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/sharedTemplates/query?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/htmlPopup?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/image"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- Service-level mutation operations (POST, honest rejection) -----

    [IntegrationTest]
    [Operation(Operations.SharedTemplates)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/sharedTemplates/add")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/sharedTemplates/update")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/sharedTemplates/delete")]
    public async Task ServiceLevelSharedTemplateMutations_RejectHonestlyAnd404OnMissingService()
    {
        // Valid service: honest rejection (no shared-template store).
        (await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates/add",
            EmptyJsonBody())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/sharedTemplates/update",
            EmptyJsonBody())).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Invalid service: 404 takes precedence over rejection.
        (await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/sharedTemplates/add",
            EmptyJsonBody())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/sharedTemplates/update",
            EmptyJsonBody())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/sharedTemplates/delete",
            EmptyJsonBody())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- Layer-level read operations (GET, spec-shaped 200) -----

    [IntegrationTest]
    [Operation(Operations.HasAssets, Operations.QueryAssets, Operations.CleanupAssets)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/hasAssets")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAssets")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/cleanupAssets")]
    public async Task LayerLevelAssetReads_ReturnEmptyAssetStoreAndHonest404s()
    {
        // hasAssets: false; no layer asset store.
        var hasAssets = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/hasAssets?f=json");
        hasAssets.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await hasAssets.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetProperty("hasAssets").GetBoolean().Should().BeFalse();
        }

        // queryAssets: empty collection.
        var queryAssets = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryAssets?f=json");
        queryAssets.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await queryAssets.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            root.TryGetProperty("assets", out var assets).Should().BeTrue();
            assets.ValueKind.Should().Be(JsonValueKind.Array);
            assets.GetArrayLength().Should().Be(0);
        }

        // cleanupAssets: success no-op (nothing to clean).
        var cleanup = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/cleanupAssets?f=json");
        cleanup.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var document = JsonDocument.Parse(await cleanup.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeTrue();
            root.GetProperty("cleanedAssetCount").GetInt32().Should().Be(0);
        }

        // Invalid-service branch (404) for each GET surface.
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/hasAssets?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/queryAssets?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/cleanupAssets?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- Layer-level rejection operations (honest rejection) -----

    [IntegrationTest]
    [Operation(Operations.UploadAssets, Operations.Convert3D, Operations.Query3D, Operations.UpdateMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/uploadAssets")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/convert3D")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query3D")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/metadata/update")]
    public async Task LayerLevelUnsupportedOperations_RejectHonestlyAnd404OnMissingService()
    {
        // Valid layer: honest rejection (no asset / 3D / metadata-write surface).
        (await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/uploadAssets?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/convert3D?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query3D?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/metadata/update",
            EmptyJsonBody())).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Invalid service: 404 takes precedence over rejection.
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/uploadAssets?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/convert3D?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.GetAsync(
            "/rest/services/nonexistent/FeatureServer/0/query3D?f=json"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/0/metadata/update",
            EmptyJsonBody())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
