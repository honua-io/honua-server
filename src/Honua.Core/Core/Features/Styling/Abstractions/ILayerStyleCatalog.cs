// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

public interface ILayerStyleCatalog
{
    Task<LayerStyleDefinition?> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default);

    Task<LayerStyleDefinition?> SetMapLibreStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    // The layer row's own style_version (0 before its first save), which every layer style write
    // increments. GetLayerStyleAsync can report a catalog style's version instead. Null when the
    // layer does not exist. Never cached; a caching catalog also drops its entry for the layer and
    // reads past its cache for the rest of the request, so the review that follows is current.
    Task<int?> GetLayerStyleWriteVersionAsync(int layerId, CancellationToken cancellationToken = default);

    // Stores only while the layer's style_version (0 before its first save) still equals
    // write.ExpectedVersion, in one statement. Null when the version moved on or the layer is gone.
    Task<LayerStyleDefinition?> SetStyleIfVersionAsync(
        int layerId,
        ConditionalStyleWrite write,
        CancellationToken cancellationToken = default);

    Task<LayerStyleDefinition?> SetStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string drawingInfoJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    Task<LayerStyleDefinition?> SetDrawingInfoAsync(int layerId, string drawingInfoJson, CancellationToken cancellationToken = default);
}
