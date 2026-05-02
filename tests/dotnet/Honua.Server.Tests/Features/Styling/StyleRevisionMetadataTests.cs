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

    [Fact]
    public async Task GetStyleAsync_OnUnstyledLayer_DoesNotCreateRevision()
    {
        // Regression for the PUT-only revision contract: a public read on a
        // newly-published layer that has no stored MapLibre must NOT bump
        // style_version or stamp style_revised_at.  The default style is
        // computed in memory and returned without persistence so the canonical
        // row stays at version 0 with null revision metadata until the first
        // PUT lands.
        var layer = LayerDefinition.CreateBasic(14, "unstyled", GeometryType.Polygon);
        var catalog = new InMemoryLayerStyleCatalog(layer.Id);
        var service = new LayerStyleService(catalog, NullLogger<LayerStyleService>.Instance);

        var snapshot = await service.GetStyleAsync(layer);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.StyleVersion);
        Assert.Null(snapshot.RevisedAt);
        Assert.Null(snapshot.RevisedBy);
        Assert.Null(snapshot.ChangeSummary);
        Assert.NotNull(snapshot.MapLibreStyle);
        Assert.NotNull(snapshot.DrawingInfo);

        var stored = await catalog.GetLayerStyleAsync(layer.Id);
        Assert.NotNull(stored);
        Assert.Null(stored!.MapLibreStyleJson);
        Assert.Null(stored.DrawingInfoJson);
        Assert.Equal(0, stored.StyleVersion);
        Assert.Null(stored.StyleRevisedAt);
        Assert.Null(stored.StyleRevisedBy);
        Assert.Null(stored.StyleChangeSummary);
    }

    [Fact]
    public async Task GetStyleAsync_OnUnstyledLayer_RepeatedReadsStayAtVersionZero()
    {
        var layer = LayerDefinition.CreateBasic(15, "unstyled-repeat", GeometryType.LineString);
        var catalog = new InMemoryLayerStyleCatalog(layer.Id);
        var service = new LayerStyleService(catalog, NullLogger<LayerStyleService>.Instance);

        var first = await service.GetStyleAsync(layer);
        var second = await service.GetStyleAsync(layer);
        var third = await service.GetStyleAsync(layer);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal(0, first!.StyleVersion);
        Assert.Equal(0, second!.StyleVersion);
        Assert.Equal(0, third!.StyleVersion);
        Assert.Null(third.RevisedAt);
    }

    [Fact]
    public async Task GetStyleAsync_AfterPutOnPreviouslyUnstyledLayer_StampsFirstRevision()
    {
        // The unstyled-read path must not "use up" the first revision: the
        // first PUT on a previously read-only layer must still land as
        // version 1 with the operator-supplied revisedBy/changeSummary.
        var layer = LayerDefinition.CreateBasic(16, "first-put", GeometryType.Polygon);
        var catalog = new InMemoryLayerStyleCatalog(layer.Id);
        var service = new LayerStyleService(catalog, NullLogger<LayerStyleService>.Instance);

        var preReadSnapshot = await service.GetStyleAsync(layer);
        Assert.NotNull(preReadSnapshot);
        Assert.Equal(0, preReadSnapshot!.StyleVersion);

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
        var update = await service.UpdateStyleAsync(
            layer,
            mapLibreStyle: null,
            drawingInfo: doc.RootElement,
            revisedBy: "operator",
            changeSummary: "First operator-driven revision.");

        Assert.Equal(LayerStyleUpdateStatus.Updated, update.Status);
        Assert.Equal(1, update.Style!.StyleVersion);
        Assert.Equal("operator", update.Style!.RevisedBy);
        Assert.Equal("First operator-driven revision.", update.Style!.ChangeSummary);
        Assert.NotNull(update.Style!.RevisedAt);
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
