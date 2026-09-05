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

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpStylePresetIntegrationTests : IAsyncLifetime
{
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
