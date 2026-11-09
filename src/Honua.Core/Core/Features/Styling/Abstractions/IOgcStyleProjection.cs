// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Abstractions;

public interface IOgcStyleProjection
{
    Task<IReadOnlyList<OgcStyleSummary>> ListStylesAsync(CancellationToken cancellationToken = default);

    // MapLibre is served from storage; SLD and Esri drawingInfo are derived on demand from that canonical style.
    Task<OgcStylesheet?> GetStylesheetAsync(
        string styleId,
        OgcStyleEncoding encoding,
        CancellationToken cancellationToken = default);

    // A feature collection with no stored style serves the layer default, so every style the list advertises resolves.
    Task<OgcStylesheet?> GetPublishedStylesheetAsync(
        string styleId,
        OgcStyleEncoding encoding,
        CancellationToken cancellationToken = default);

    Task<OgcStylesheet?> GetAssociatedStylesheetAsync(
        string resourceId,
        string styleId,
        OgcStyleEncoding encoding,
        CancellationToken cancellationToken = default);

    Task<OgcStyleMetadata?> GetStyleMetadataAsync(
        string styleId,
        CancellationToken cancellationToken = default);

    // An update never creates a style.
    Task<OgcStyleUpdateResult> UpdateStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        bool strict,
        CancellationToken cancellationToken = default);

    // DrawingInfo is converted to MapLibre on the server; the console does not convert it.
    Task<OgcStyleUpdateResult> UpdateStyleFromDrawingInfoAsync(
        string styleId,
        string drawingInfoJson,
        bool strict,
        CancellationToken cancellationToken = default);

    Task<OgcStyleCreateResult> CreateStyleAsync(
        string? styleId,
        string mapLibreStyleJson,
        bool strict,
        CancellationToken cancellationToken = default);

    // A Phase 1 per-layer default style cannot be deleted through the standalone catalog.
    Task<OgcStyleDeleteResult> DeleteStyleAsync(
        string styleId,
        CancellationToken cancellationToken = default);
}

public enum OgcStyleEncoding
{
    MapboxStyle,

    Sld10,

    Sld11,

    GeoServicesDrawingInfo
}

public sealed record OgcStyleSummary(string StyleId, string Title);

public sealed record OgcStylesheet(string Content, string MediaType, OgcStyleEncoding Encoding);

public sealed record OgcStyleMetadata(
    string StyleId,
    string Title,
    string? Description,
    IReadOnlyList<string> Keywords,
    string? License,
    string? Version);

public enum OgcStyleUpdateStatus
{
    Updated,

    NotFound,

    Invalid
}

public sealed record OgcStyleUpdateResult(
    OgcStyleUpdateStatus Status,
    string? ErrorMessage,
    IReadOnlyList<string>? UnsupportedSymbolizers = null);

public enum OgcStyleCreateStatus
{
    Created,

    Conflict,

    Invalid
}

public sealed record OgcStyleCreateResult(OgcStyleCreateStatus Status, string? StyleId, string? ErrorMessage);

public enum OgcStyleDeleteStatus
{
    Deleted,

    NotFound,

    Forbidden
}

public sealed record OgcStyleDeleteResult(OgcStyleDeleteStatus Status, string? ErrorMessage);
