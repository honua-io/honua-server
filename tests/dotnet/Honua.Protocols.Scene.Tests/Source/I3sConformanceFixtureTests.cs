// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// I3S SceneServer conformance harness (#1813): mounts the Honua-authored
/// synthetic I3S 1.7 fixture as a hosted scene, drives the live SceneServer
/// endpoints (service / layer / node pages / statistics), and runs the protocol
/// shape validator over each served resource. This is the test oracle for the
/// node-page (#1809) / layerType (#1812) / statistics (#1811) lanes.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class I3sConformanceFixtureTests : IAsyncLifetime
{
    private const string SceneId = "i3s-conformance";
    private readonly WebAppFixture _fixture;
    private readonly string _tilesetRoot;

    public I3sConformanceFixtureTests()
    {
        _tilesetRoot = I3sConformanceFixturePaths.ResolveSourceTilesetRoot();
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"Scenes:Datasets:0:Id"] = SceneId,
                        [$"Scenes:Datasets:0:Name"] = "I3S Conformance Fixture Scene",
                        [$"Scenes:Datasets:0:AssetRoot"] = _tilesetRoot,
                    });
                });
            })
            .ConfigureServices(services =>
                services.AddSingleton<ILicenseStatusProvider>(
                    new EnterpriseLicenseStatusProvider()));
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer")]
    public async Task ServiceDescriptor_PassesProtocolShapeValidator()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{SceneId}/SceneServer");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var violations = I3sProtocolShapeValidator.ValidateService(json.RootElement);
        violations.Should().BeEmpty("the served SceneServer service must be I3S-shape conformant");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task LayerDescriptor_AdvertisesNodePages_AndPassesValidator()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{SceneId}/SceneServer/layers/0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var violations = I3sProtocolShapeValidator.ValidateLayer(root);
        violations.Should().BeEmpty("the served scene layer must be I3S-shape conformant");

        // The fixture tileset is loadable, so the descriptor must advertise a
        // fetchable node-page store (#1809).
        var store = root.GetProperty("store");
        store.TryGetProperty("nodePages", out var nodePages).Should().BeTrue();
        nodePages.GetProperty("nodesPerPage").GetInt32().Should().BePositive();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}")]
    public async Task NodePage_ProjectsTilesetTree_AndPassesValidator()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodepages/0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var violations = I3sProtocolShapeValidator.ValidateNodePage(root);
        violations.Should().BeEmpty("the served node page must be I3S-shape conformant");

        var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        // Fixture tree: 1 root + 2 content children = 3 nodes.
        nodes.Should().HaveCount(3);

        // The root references its two children by global index and carries no mesh.
        var rootNode = nodes[0];
        rootNode.GetProperty("children").EnumerateArray().Select(c => c.GetInt32())
            .Should().BeEquivalentTo([1, 2]);
        rootNode.TryGetProperty("mesh", out _).Should().BeFalse();

        // Each content child carries a mesh whose resource id is its global index.
        nodes[1].GetProperty("mesh").GetProperty("geometry").GetProperty("resource").GetInt32().Should().Be(1);
        nodes[1].GetProperty("parentIndex").GetInt32().Should().Be(0);
        nodes[2].GetProperty("mesh").GetProperty("geometry").GetProperty("resource").GetInt32().Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}")]
    public async Task NodePage_OutOfRange_Returns404()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodepages/99");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/statistics/{fieldKey}/0")]
    public async Task Statistics_ForObjectId_PassesValidator_AndCountsContentNodes()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/statistics/f_0/0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var violations = I3sProtocolShapeValidator.ValidateStatistics(root);
        violations.Should().BeEmpty("the served statistics document must be I3S-shape conformant");

        // Two content-bearing nodes in the fixture tree.
        root.GetProperty("stats").GetProperty("totalValuesCount").GetInt64().Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/statistics/{fieldKey}/0")]
    public async Task Statistics_UnknownField_Returns404()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/statistics/f_99/0");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class EnterpriseLicenseStatusProvider : ILicenseStatusProvider
    {
        public LicenseStatus GetCurrentStatus() =>
            new(HonuaEdition.Enterprise, IsValid: true, ExpiresAt: null, LicensedTo: "test");

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicenseUploadResult(false, "Stub does not support upload."));
    }
}
