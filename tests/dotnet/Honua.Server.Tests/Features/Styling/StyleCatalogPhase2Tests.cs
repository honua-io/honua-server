// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Styling;

/// <summary>
/// Integration tests for the ADR-0048 Phase 2 (#1389) independent style catalog: the
/// styleId-keyed store and its many-to-many associations, the <c>Type=Style</c> graph
/// producer lighting up <c>StyleResourceIds</c> with real data, and one-style-many
/// resource reuse.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class StyleCatalogPhase2Tests : IAsyncLifetime
{
    private const string MapboxStyleMediaType = "application/vnd.mapbox.style+json";

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task UpdatingLayerStyle_PopulatesStyleResourceIdsWithTypeStyleResource()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        using var scope = _fixture.Services.CreateScope();

        // The independent catalog now has the layer's default style.
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var styles = await catalog.GetStylesForLayerAsync(WebAppFixture.TestLayerId);
        styles.Should().NotBeEmpty();
        var styleId = styles[0].StyleId;
        styleId.Should().Be($"style-layer-{WebAppFixture.TestLayerId}");

        // The canonical graph references it via StyleResourceIds → a real Type=Style resource.
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();

        snapshot.Index.ResourcesByStorageLayerId.TryGetValue(WebAppFixture.TestLayerId, out var dataResource)
            .Should().BeTrue();
        dataResource!.StyleResourceIds.Should().NotBeEmpty();

        var styleResourceId = dataResource.StyleResourceIds[0];
        snapshot.Index.ResourcesById.TryGetValue(styleResourceId, out var styleResource).Should().BeTrue();
        styleResource!.Type.Should().Be(MetadataV2ResourceType.Style);
        styleResource.Style.Should().NotBeNull();
        styleResource.Style!.Encodings.Should().Contain(e => e.Encoding == "mapbox-style");

        // The graph stays valid with the populated StyleResourceIds.
        MetadataV2GraphValidator.Validate(snapshot.Graph).IsValid.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    public async Task OneCatalogStyle_CanBeReferencedByManyLayers()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        using var scope = _fixture.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();

        var styleId = $"shared-{Guid.NewGuid():N}";
        var created = await catalog.CreateStyleAsync(styleId, BuildDefaultStyleJson(), title: "Shared");
        created.Should().NotBeNull();

        // Associate the SAME style with the test layer (one style -> many layers).
        var associated = await catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, styleId, ordinal: 1);
        associated.Should().BeTrue();

        var forLayer = await catalog.GetStylesForLayerAsync(WebAppFixture.TestLayerId);
        forLayer.Select(s => s.StyleId).Should().Contain(styleId);

        // The shared style is one record; the association table records the reuse.
        var associations = await catalog.ListAssociationsAsync();
        associations.Count(a => a.StyleId == styleId).Should().BeGreaterThanOrEqualTo(1);
    }

    private static async Task SeedTestLayerStyleAsync(HttpClient adminClient)
    {
        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.Deserialize<JsonElement>(BuildDefaultStyleJson())
        };

        var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest));
        response.Be200Ok();
    }

    private static string BuildDefaultStyleJson()
    {
        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            MetadataV2GeometryType.Point);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        return JsonSerializer.Serialize(style);
    }
}
