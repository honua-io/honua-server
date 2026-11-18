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

    // The review the Console requires before it enables Save on a shared style.
    Task<SharedStyleImpact?> GetSharedStyleImpactAsync(
        SharedStyleImpactRequest request,
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

public sealed record OgcStylesheet(string Content, string MediaType, OgcStyleEncoding Encoding)
{
    // Stored revision. Zero means the style has not been saved through PUT yet.
    public int StoredVersion { get; init; }

    // Canonical MapLibre document the revision identifies, before endpoint URLs are rewritten.
    public string? CanonicalMapLibreJson { get; init; }
}

public sealed record SharedStyleImpact
{
    public required string StyleId { get; init; }

    public required int StoredVersion { get; init; }

    public required int ReportedVersion { get; init; }

    public required string EntityTag { get; init; }

    public required string ContentHash { get; init; }

    public required string ImpactHash { get; init; }

    public required bool ImpactComplete { get; init; }

    public required IReadOnlyList<SharedStyleAssociation> Associations { get; init; }

    public required IReadOnlyList<SharedStyleConsumerRow> Consumers { get; init; }
}

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
    IReadOnlyList<string>? UnsupportedSymbolizers = null)
{
    // The storage layer whose style was written, when the style belongs to a layer.
    public int? StorageLayerId { get; init; }

    // Layers that present a catalog style through their associations.
    public IReadOnlyList<int> AssociatedLayerIds { get; init; } = [];
}

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

public sealed record OgcStyleDeleteResult(OgcStyleDeleteStatus Status, string? ErrorMessage)
{
    // Layers that presented the deleted catalog style through their associations.
    public IReadOnlyList<int> AssociatedLayerIds { get; init; } = [];
}
