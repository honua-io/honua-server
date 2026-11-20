// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Server.Features.Styling;

internal interface ILayerStyleService
{
    Task<LayerStyleSnapshot?> GetStyleAsync(
        MetadataV2Object resource,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetDrawingInfoAsync(
        MetadataV2Object resource,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<LayerStyleUpdateResult> UpdateStyleAsync(
        MetadataV2Object resource,
        int layerId,
        JsonElement? mapLibreStyle,
        JsonElement? drawingInfo,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    // Stores the MapLibre style only while the layer's style_version still equals
    // update.ExpectedVersion; PreconditionFailed (nothing stored) when another save landed first.
    Task<LayerStyleUpdateResult> UpdateMapLibreStyleIfVersionAsync(
        MetadataV2Object resource,
        ConditionalLayerStyle update,
        CancellationToken cancellationToken = default);
}

internal sealed record ConditionalLayerStyle
{
    public required int LayerId { get; init; }

    public required JsonElement MapLibreStyle { get; init; }

    public required int ExpectedVersion { get; init; }
}

internal sealed record LayerStyleSnapshot(
    JsonElement? MapLibreStyle,
    JsonElement? DrawingInfo,
    int StyleVersion = 0,
    DateTimeOffset? RevisedAt = null,
    string? RevisedBy = null,
    string? ChangeSummary = null);

internal enum LayerStyleUpdateStatus
{
    Updated,
    NotFound,
    Invalid,
    PreconditionFailed
}

internal sealed record LayerStyleUpdateResult(
    LayerStyleUpdateStatus Status,
    LayerStyleSnapshot? Style,
    string? ErrorMessage,
    IReadOnlyList<UnsupportedSymbolizerInfo>? UnsupportedSymbolizers = null);
