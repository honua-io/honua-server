// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text;
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
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ApplyingPreset_ReplacesPrimaryInCatalogGraphAndMcpReadback()
    {
        using var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);
        using var scope = _fixture.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var suffix = Guid.NewGuid().ToString("N");
        var oldStyle = $"a-old-{suffix}";
        var newStyle = $"z-new-{suffix}";
        (await catalog.CreateStyleAsync(oldStyle, BuildDefaultStyleJson(), title: "Old preset")).Should().NotBeNull();
        (await catalog.CreateStyleAsync(newStyle, BuildDefaultStyleJson(), title: "New preset")).Should().NotBeNull();
        using var initialize = await client.PostAsync("/mcp", new StringContent("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
              "protocolVersion":"2025-03-26","capabilities":{},
              "clientInfo":{"name":"style-primary-test","version":"1.0"}}}
            """, Encoding.UTF8, "application/json"));
        initialize.EnsureSuccessStatusCode();
        var session = initialize.Headers.GetValues("Mcp-Session-Id").Single();

        foreach (var styleId in new[] { oldStyle, newStyle, newStyle })
        {
            var applied = await CallMcpStyleAsync(client, session, "honua_apply_style_preset", styleId);
            applied.GetProperty("applied").GetBoolean().Should().BeTrue();
            applied.GetProperty("styleId").GetString().Should().Be(styleId);

            var styles = await catalog.GetStylesForLayerAsync(WebAppFixture.TestLayerId);
            styles[0].StyleId.Should().Be(styleId, "a successful preset application must select the effective primary");
            var associations = (await catalog.ListAssociationsAsync())
                .Where(association => association.LayerId == WebAppFixture.TestLayerId).ToArray();
            associations.Count(association => association.Ordinal == 0).Should().Be(1);
            associations.Single(association => association.Ordinal == 0).StyleId.Should().Be(styleId);

            var readback = await CallMcpStyleAsync(client, session, "honua_get_style", null);
            readback.GetProperty("styleId").GetString().Should().Be(styleId);
            var snapshot = _fixture.GetCurrentV2GraphSnapshot();
            var resource = snapshot.Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId];
            var primary = snapshot.Index.ResourcesById[resource.StyleResourceIds[0]];
            primary.Metadata.Name.Should().Be(styleId);
        }

        (await catalog.GetStylesForLayerAsync(WebAppFixture.TestLayerId))
            .Select(style => style.StyleId).Should().Contain(oldStyle).And.Contain(newStyle);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ConcurrentPrimaryPromotions_PreserveOnePrimaryAndMissingStyleLeavesOrderUnchanged()
    {
        using var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);
        using var scope = _fixture.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
        var first = $"primary-first-{Guid.NewGuid():N}";
        var second = $"primary-second-{Guid.NewGuid():N}";
        (await catalog.CreateStyleAsync(first, BuildDefaultStyleJson())).Should().NotBeNull();
        (await catalog.CreateStyleAsync(second, BuildDefaultStyleJson())).Should().NotBeNull();

        var promoted = await Task.WhenAll(
            catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, first),
            catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, second));
        promoted.Should().OnlyContain(applied => applied);
        var before = (await catalog.ListAssociationsAsync())
            .Where(association => association.LayerId == WebAppFixture.TestLayerId)
            .OrderBy(association => association.Ordinal).ToArray();
        before.Count(association => association.Ordinal == 0).Should().Be(1);
        before.Select(association => association.Ordinal).Should().OnlyHaveUniqueItems();
        before.Select(association => association.StyleId).Should().Contain(first).And.Contain(second);

        (await catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, $"missing-{Guid.NewGuid():N}"))
            .Should().BeFalse();
        var after = (await catalog.ListAssociationsAsync())
            .Where(association => association.LayerId == WebAppFixture.TestLayerId)
            .OrderBy(association => association.Ordinal).ToArray();
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private static async Task<JsonElement> CallMcpStyleAsync(HttpClient client, string session,
        string toolName, string? styleId)
    {
        var styleArgument = styleId is null ? string.Empty : $",\"styleId\":\"{styleId}\"";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent($$"""
                {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{
                  "name":"{{toolName}}","arguments":{"serviceId":"{{WebAppFixture.TestServiceId}}",
                  "layerId":{{WebAppFixture.TestLayerId}}{{styleArgument}}}}}
                """, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Mcp-Session-Id", session);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(root.ToString());
        var result = root.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse(result.ToString());
        return result.GetProperty("structuredContent").Clone();
    }

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

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task UpdateCatalogStyle_UpdatesOnlyAnExistingRecord()
    {
        using var scope = _fixture.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();

        var missing = await catalog.UpdateStyleAsync(
            $"missing-{Guid.NewGuid():N}",
            BuildDefaultStyleJson());
        missing.Should().BeNull();

        var styleId = $"update-{Guid.NewGuid():N}";
        var created = await catalog.CreateStyleAsync(styleId, BuildDefaultStyleJson(), title: "Original");
        created.Should().NotBeNull();

        var updated = await catalog.UpdateStyleAsync(styleId, BuildDefaultStyleJson(), title: "Updated");

        updated.Should().NotBeNull();
        updated!.Title.Should().Be("Updated");
        updated.StyleVersion.Should().Be(created!.StyleVersion + 1);
    }

    private static async Task SeedTestLayerStyleAsync(HttpClient adminClient)
    {
        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.Deserialize<JsonElement>(BuildDefaultStyleJson())
        };

        using var content = JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest);
        using var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            content);
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
