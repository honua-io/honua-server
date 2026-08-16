// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Infrastructure.Rendering;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Styling;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Styling;

/// <summary>
/// Proves that synchronizing the metadata-v2 style graph is treated as a post-commit,
/// best-effort mirror: by the time it runs the catalog write has already committed and
/// incremented <c>style_version</c>, so a synchronization failure must not be reported as a
/// failed edit (#3188). Reporting it would surface an applied edit as a 500, the endpoint
/// would skip its output-cache eviction, and a client retry would apply a second revision.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiStyles)]
public sealed class OgcStylesGraphSyncFailureTests : IAsyncLifetime
{
    private const string MapboxStyleMediaType = "application/vnd.mapbox.style+json";

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync()
    {
        _fixture.ConfigureServices(static services =>
        {
            services.RemoveAll<IMetadataV2StyleGraphSync>();
            services.AddScoped<IMetadataV2StyleGraphSync, ThrowingStyleGraphSync>();
        });

        return _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/styles/{styleId}")]
    public async Task PutStyle_AssociatedCatalogStyle_GraphSyncThrows_StillReportsTheEditAsApplied()
    {
        var client = _fixture.CreateAdminClient();
        await SeedTestLayerStyleAsync(client);

        var styleId = await CreateStandaloneStyleAsync(client, MetadataV2GeometryType.Point);

        // Associating the style is what makes the update path reach the graph sync at all:
        // it mirrors the catalog record onto a layer, and only associated layers are synced.
        using (var scope = _fixture.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
            (await catalog.AssociateLayerAsync(WebAppFixture.TestLayerId, styleId, ordinal: 1)).Should().BeTrue();
        }

        using var content = new StringContent(
            BuildStyleJson(MetadataV2GeometryType.LineString),
            Encoding.UTF8,
            MapboxStyleMediaType);
        var response = await client.PutAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}", content);

        response.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "the catalog write committed before the graph sync ran, so a post-commit sync failure must not be reported as a failed edit");

        // The edit is durable, not rolled back: a caller told "no content" must be able to
        // read back exactly what it wrote.
        var updated = await client.GetAsync($"/ogc/styles/{Uri.EscapeDataString(styleId)}");
        updated.Be200Ok();
        using var document = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("layers")[0].GetProperty("type").GetString().Should().Be("line");
    }

    private static async Task SeedTestLayerStyleAsync(HttpClient adminClient)
    {
        var request = new LayerStyleUpdateRequest
        {
            MapLibreStyle = JsonSerializer.Deserialize<JsonElement>(BuildStyleJson(MetadataV2GeometryType.Point))
        };

        using var content = JsonContent.Create(request, LayerStyleJsonContext.Default.LayerStyleUpdateRequest);
        using var response = await adminClient.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
            content);
        response.Be200Ok();
    }

    private static async Task<string> CreateStandaloneStyleAsync(
        HttpClient adminClient,
        MetadataV2GeometryType geometryType)
    {
        var styleId = $"sync-failure-{Guid.NewGuid():N}";
        using var content = new StringContent(BuildStyleJson(geometryType), Encoding.UTF8, MapboxStyleMediaType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);

        var response = await adminClient.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return styleId;
    }

    private static string BuildStyleJson(MetadataV2GeometryType geometryType)
    {
        var layer = new StyleLayerDescriptor(
            WebAppFixture.TestLayerId,
            "Test Layer",
            geometryType);
        var style = StyleDefaults.BuildDefaultMapLibreStyle(layer);
        return JsonSerializer.Serialize(style);
    }

    /// <summary>
    /// Stands in for the real graph sync and always fails, simulating a losing optimistic
    /// ETag check against a concurrent metadata writer.
    /// </summary>
    private sealed class ThrowingStyleGraphSync : IMetadataV2StyleGraphSync
    {
        public Task SyncLayerStylesAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                $"Simulated metadata-v2 graph synchronization failure for layer {layerId}.");
    }
}

public sealed class OgcStyleProjectionGraphSyncTests
{
    [UnitTest]
    public async Task UpdateStyle_WhenFirstAssociationSyncFails_ContinuesRemainingAssociations()
    {
        const string styleId = "shared-style";
        const string mapLibreStyle =
            """
            {
              "version": 8,
              "layers": [ { "id": "roads", "type": "line" } ]
            }
            """;
        var existing = new StyleCatalogRecord
        {
            StyleId = styleId,
            Title = "Shared style",
            MapLibreStyleJson = mapLibreStyle,
            StyleVersion = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };

        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(styleId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StyleCatalogRecord?>(existing));
        catalog.UpdateStyleAsync(
                styleId,
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StyleCatalogRecord?>(existing with { StyleVersion = 2 }));
        catalog.ListAssociationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<StyleLayerAssociation>>(
            [
                new StyleLayerAssociation(11, styleId, 0),
                new StyleLayerAssociation(22, styleId, 0)
            ]));

        var graphSync = new FirstLayerThrowingGraphSync(11);
        var projection = new OgcStyleProjection(
            new EmptyGraphProvider(),
            Substitute.For<ILayerStyleService>(),
            Substitute.For<ILayerStyleCatalog>(),
            Substitute.For<IGeoServicesStyleConverter>(),
            catalog,
            graphSync);

        var result = await projection.UpdateStyleAsync(styleId, mapLibreStyle, strict: false);

        result.Status.Should().Be(OgcStyleUpdateStatus.Updated);
        graphSync.Calls.Should().Equal(11, 22);
    }

    private sealed class FirstLayerThrowingGraphSync(int failingLayerId) : IMetadataV2StyleGraphSync
    {
        public List<int> Calls { get; } = [];

        public Task SyncLayerStylesAsync(int layerId, CancellationToken cancellationToken = default)
        {
            Calls.Add(layerId);
            return layerId == failingLayerId
                ? Task.FromException(new InvalidOperationException("Simulated graph conflict."))
                : Task.CompletedTask;
        }
    }

    private sealed class EmptyGraphProvider : IMetadataV2GraphProvider
    {
        private static readonly MetadataV2GraphSnapshot Snapshot = new(
            new MetadataV2Graph(),
            "\"empty\"",
            DateTimeOffset.UnixEpoch);

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(
            CancellationToken cancellationToken = default)
            => new(Snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => new((MetadataV2GraphSnapshot?)null);
    }
}
