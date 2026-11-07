// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IRasterMapRenderer
{
    Task<RasterResult> RenderCollectionMapAsync(
        int layerId,
        MapRenderRequest request,
        CancellationToken cancellationToken = default);

    Task<RasterResult> RenderDatasetMapAsync(
        int[] layerIds,
        MapRenderRequest request,
        CancellationToken cancellationToken = default);

    Task<RasterResult> RenderStyledMapAsync(
        int layerId,
        string styleId,
        MapRenderRequest request,
        CancellationToken cancellationToken = default);
}

public readonly record struct MapRenderRequest
{
    public required double[] BoundingBox { get; init; }

    public int? BoundingBoxCrs { get; init; }

    public int? Crs { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public RasterFormat Format { get; init; } = RasterFormat.PNG;

    public bool Transparent { get; init; } = true;

    public string? BackgroundColor { get; init; }

    // For an instant this is the requested timestamp; for an interval it is the inclusive end, and null means the end is open.
    public DateTimeOffset? DateTime { get; init; }

    // An instant leaves this unset so the renderer uses newest-at-or-before; an interval uses it as the inclusive start.
    public DateTimeOffset? DateTimeFrom { get; init; }

    public IReadOnlyDictionary<string, TemporalFilter>? TemporalFiltersByResourceId { get; init; }

    public int? Quality { get; init; }

    // When a resolved identity is present the renderer must use it, because the first-wins storage-layer index can pick the wrong resource.
    public IReadOnlyList<ResolvedMapLayer>? ResolvedLayers { get; init; }

    // A supplied style replaces the stored default, and a document with no style layers draws nothing for that layer.
    public IReadOnlyDictionary<int, string>? AppliedStyleJsonByLayerId { get; init; }

    public MapRenderRequest(double[] boundingBox, int width, int height)
    {
        BoundingBox = boundingBox;
        Width = width;
        Height = height;
    }
}

public readonly record struct ResolvedMapLayer(int LayerId, string ResourceId);
