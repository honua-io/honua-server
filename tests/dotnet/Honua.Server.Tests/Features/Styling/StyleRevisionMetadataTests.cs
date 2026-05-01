// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Infrastructure.Styling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Styling;

[Trait("Category", "Unit")]
[Trait("Component", "Styling")]
[Trait("Feature", "StyleRevision")]
public class StyleRevisionMetadataTests
{
    [Fact]
    public async Task UpdateStyleAsync_WithChangeSummary_PopulatesRevisionMetadata()
    {
        var layer = LayerDefinition.CreateBasic(11, "districts", GeometryType.Polygon);
        var catalog = new InMemoryLayerStyleCatalog(layer.Id);
        var service = new LayerStyleService(catalog, NullLogger<LayerStyleService>.Instance);

        var drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSFS",
              "color": [10, 20, 30, 200]
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(drawingInfoJson);

        var result = await service.UpdateStyleAsync(
            layer,
            mapLibreStyle: null,
            drawingInfo: doc.RootElement,
            revisedBy: "operator@example.com",
            changeSummary: "Initial style import.");

        Assert.Equal(LayerStyleUpdateStatus.Updated, result.Status);
        Assert.NotNull(result.Style);
        Assert.Equal(1, result.Style!.StyleVersion);
        Assert.Equal("operator@example.com", result.Style!.RevisedBy);
        Assert.Equal("Initial style import.", result.Style!.ChangeSummary);
        Assert.NotNull(result.Style!.RevisedAt);
    }

    [Fact]
    public async Task UpdateStyleAsync_TwoRevisions_IncrementsStyleVersionAndUpdatesMetadata()
    {
        var layer = LayerDefinition.CreateBasic(12, "lines", GeometryType.LineString);
        var catalog = new InMemoryLayerStyleCatalog(layer.Id);
        var service = new LayerStyleService(catalog, NullLogger<LayerStyleService>.Instance);

        var drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSLS",
              "color": [200, 50, 0, 255],
              "width": 1
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(drawingInfoJson);

        var first = await service.UpdateStyleAsync(
            layer,
            mapLibreStyle: null,
            drawingInfo: doc.RootElement,
            revisedBy: "alice",
            changeSummary: "First revision.");

        var second = await service.UpdateStyleAsync(
            layer,
            mapLibreStyle: null,
            drawingInfo: doc.RootElement,
            revisedBy: "bob",
            changeSummary: "Second revision.");

        Assert.Equal(LayerStyleUpdateStatus.Updated, first.Status);
        Assert.Equal(LayerStyleUpdateStatus.Updated, second.Status);
        Assert.Equal(1, first.Style!.StyleVersion);
        Assert.Equal(2, second.Style!.StyleVersion);
        Assert.Equal("alice", first.Style!.RevisedBy);
        Assert.Equal("bob", second.Style!.RevisedBy);
        Assert.NotNull(first.Style!.RevisedAt);
        Assert.NotNull(second.Style!.RevisedAt);
        Assert.True(second.Style!.RevisedAt >= first.Style!.RevisedAt);
    }

    [Fact]
    public async Task GetStyleAsync_AfterUpdate_ReflectsLatestRevisionMetadata()
    {
        var layer = LayerDefinition.CreateBasic(13, "points", GeometryType.Point);
        var catalog = new InMemoryLayerStyleCatalog(layer.Id);
        var service = new LayerStyleService(catalog, NullLogger<LayerStyleService>.Instance);

        var drawingInfoJson = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSMS",
              "color": [0, 128, 0, 255],
              "size": 8
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(drawingInfoJson);

        await service.UpdateStyleAsync(
            layer,
            mapLibreStyle: null,
            drawingInfo: doc.RootElement,
            revisedBy: "import-bot",
            changeSummary: "Imported from FeatureService.");

        var snapshot = await service.GetStyleAsync(layer);
        Assert.NotNull(snapshot);
        Assert.Equal("import-bot", snapshot!.RevisedBy);
        Assert.Equal("Imported from FeatureService.", snapshot!.ChangeSummary);
        Assert.Equal(1, snapshot!.StyleVersion);
    }

    private sealed class InMemoryLayerStyleCatalog : ILayerStyleCatalog
    {
        private readonly int _expectedLayerId;
        private LayerStyleDefinition? _state;

        public InMemoryLayerStyleCatalog(int layerId)
        {
            _expectedLayerId = layerId;
            _state = new LayerStyleDefinition { LayerId = layerId };
        }

        public Task<LayerStyleDefinition?> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(layerId == _expectedLayerId ? _state : null);
        }

        public Task<LayerStyleDefinition?> SetMapLibreStyleAsync(
            int layerId,
            string mapLibreStyleJson,
            string? revisedBy = null,
            string? changeSummary = null,
            CancellationToken cancellationToken = default)
        {
            if (layerId != _expectedLayerId)
            {
                return Task.FromResult<LayerStyleDefinition?>(null);
            }

            _state = new LayerStyleDefinition
            {
                LayerId = layerId,
                MapLibreStyleJson = mapLibreStyleJson,
                DrawingInfoJson = null,
                StyleVersion = (_state?.StyleVersion ?? 0) + 1,
                StyleRevisedAt = DateTimeOffset.UtcNow,
                StyleRevisedBy = revisedBy,
                StyleChangeSummary = changeSummary
            };
            return Task.FromResult<LayerStyleDefinition?>(_state);
        }

        public Task<LayerStyleDefinition?> SetStyleAsync(
            int layerId,
            string mapLibreStyleJson,
            string drawingInfoJson,
            string? revisedBy = null,
            string? changeSummary = null,
            CancellationToken cancellationToken = default)
        {
            if (layerId != _expectedLayerId)
            {
                return Task.FromResult<LayerStyleDefinition?>(null);
            }

            _state = new LayerStyleDefinition
            {
                LayerId = layerId,
                MapLibreStyleJson = mapLibreStyleJson,
                DrawingInfoJson = drawingInfoJson,
                StyleVersion = (_state?.StyleVersion ?? 0) + 1,
                StyleRevisedAt = DateTimeOffset.UtcNow,
                StyleRevisedBy = revisedBy,
                StyleChangeSummary = changeSummary
            };
            return Task.FromResult<LayerStyleDefinition?>(_state);
        }

        public Task<LayerStyleDefinition?> SetDrawingInfoAsync(int layerId, string drawingInfoJson, CancellationToken cancellationToken = default)
        {
            if (layerId != _expectedLayerId || _state == null)
            {
                return Task.FromResult<LayerStyleDefinition?>(null);
            }

            _state = _state with { DrawingInfoJson = drawingInfoJson };
            return Task.FromResult<LayerStyleDefinition?>(_state);
        }
    }
}
