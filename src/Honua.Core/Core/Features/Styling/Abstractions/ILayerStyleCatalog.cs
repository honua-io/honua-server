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

    Task<LayerStyleDefinition?> SetStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string drawingInfoJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    Task<LayerStyleDefinition?> SetDrawingInfoAsync(int layerId, string drawingInfoJson, CancellationToken cancellationToken = default);
}
