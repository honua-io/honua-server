// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Server.Features.Styling;

internal interface ILayerStyleService
{
    Task<LayerStyleSnapshot?> GetStyleAsync(
        MetadataV2Resource resource,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<JsonElement?> GetDrawingInfoAsync(
        MetadataV2Resource resource,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<LayerStyleUpdateResult> UpdateStyleAsync(
        MetadataV2Resource resource,
        int layerId,
        JsonElement? mapLibreStyle,
        JsonElement? drawingInfo,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);
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
    Invalid
}

internal sealed record LayerStyleUpdateResult(
    LayerStyleUpdateStatus Status,
    LayerStyleSnapshot? Style,
    string? ErrorMessage,
    IReadOnlyList<UnsupportedSymbolizerInfo>? UnsupportedSymbolizers = null);
