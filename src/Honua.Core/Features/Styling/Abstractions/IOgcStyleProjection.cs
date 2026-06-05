// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Projects Honua's per-layer style storage as OGC API - Styles resources (ADR-0048,
/// Phase 1). Exposed in <c>Honua.Core</c> so the OGC API protocol slice in
/// <c>Honua.Protocols.OgcApi</c> can read, derive, validate, and update styles without
/// taking a hard dependency on the internal style services, converters, and validator
/// that live in <c>Honua.Server</c>. This mirrors the cross-project pattern used by
/// <see cref="ISldStyleConverter"/> and <see cref="IGeoServicesStyleConverter"/>.
/// </summary>
public interface IOgcStyleProjection
{
    /// <summary>
    /// Lists the styles available through the OGC API - Styles surface. In Phase 1 this
    /// enumerates the collections (data resources) that have a stored, non-null MapLibre
    /// style; each styled collection projects to exactly one OGC style.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The styled-collection summaries, ordered by style identifier.</returns>
    Task<IReadOnlyList<OgcStyleSummary>> ListStylesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the stylesheet for the requested style in the requested encoding. MapLibre
    /// is served from canonical storage; SLD 1.0/1.1 are derived on demand from the
    /// canonical MapLibre style.
    /// </summary>
    /// <param name="styleId">Stable style identifier (the collection's resource name).</param>
    /// <param name="encoding">Requested stylesheet encoding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stylesheet document, or <c>null</c> when no such styled collection exists.</returns>
    Task<OgcStylesheet?> GetStylesheetAsync(
        string styleId,
        OgcStyleEncoding encoding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets descriptive metadata for the requested style.
    /// </summary>
    /// <param name="styleId">Stable style identifier (the collection's resource name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The style metadata, or <c>null</c> when no such styled collection exists.</returns>
    Task<OgcStyleMetadata?> GetStyleMetadataAsync(
        string styleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stores a MapLibre stylesheet for an existing style (OGC
    /// <c>manage-styles</c>, PUT). Phase 1 only updates an existing styled collection's
    /// per-layer style; it cannot create a standalone style.
    /// </summary>
    /// <param name="styleId">Stable style identifier (the collection's resource name).</param>
    /// <param name="mapLibreStyleJson">Candidate MapLibre style JSON document.</param>
    /// <param name="strict">When <c>true</c>, validation failures reject the request (OGC <c>style-validation</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The update outcome.</returns>
    Task<OgcStyleUpdateResult> UpdateStyleAsync(
        string styleId,
        string mapLibreStyleJson,
        bool strict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stores a style supplied as an Esri GeoServices <c>drawingInfo</c> renderer (ADR-0002).
    /// The drawingInfo is converted server-side to the canonical MapLibre style and persisted; the console
    /// never converts. Lossy conversions are reported in <see cref="OgcStyleUpdateResult.UnsupportedSymbolizers"/>
    /// (and reject the request when <paramref name="strict"/> is set).
    /// </summary>
    Task<OgcStyleUpdateResult> UpdateStyleFromDrawingInfoAsync(
        string styleId,
        string drawingInfoJson,
        bool strict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new standalone style in the independent style catalog (OGC
    /// <c>manage-styles</c>, POST). Available from Phase 2 (ADR-0048, issue #1389).
    /// The new style is decoupled from any layer; it can later be associated with
    /// data resources to render them.
    /// </summary>
    /// <param name="styleId">
    /// Requested stable style identifier. When <c>null</c> or blank the server assigns one.
    /// </param>
    /// <param name="mapLibreStyleJson">Candidate MapLibre style JSON document.</param>
    /// <param name="strict">When <c>true</c>, validation failures reject the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The create outcome, including the assigned style identifier on success.</returns>
    Task<OgcStyleCreateResult> CreateStyleAsync(
        string? styleId,
        string mapLibreStyleJson,
        bool strict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a style from the independent style catalog (OGC <c>manage-styles</c>,
    /// DELETE). Available from Phase 2. Default per-layer styles (those still backing a
    /// Phase 1 collection) cannot be deleted through this surface.
    /// </summary>
    /// <param name="styleId">Stable style identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The delete outcome.</returns>
    Task<OgcStyleDeleteResult> DeleteStyleAsync(
        string styleId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stylesheet encodings the OGC API - Styles surface can serve.
/// </summary>
public enum OgcStyleEncoding
{
    /// <summary>Canonical MapLibre / Mapbox style JSON.</summary>
    MapboxStyle,

    /// <summary>OGC Styled Layer Descriptor 1.0, derived from the MapLibre style.</summary>
    Sld10,

    /// <summary>OGC Styled Layer Descriptor 1.1, derived from the MapLibre style.</summary>
    Sld11,

    /// <summary>Esri GeoServices <c>drawingInfo</c> renderer, derived from the MapLibre style (ADR-0002).</summary>
    EsriDrawingInfo
}

/// <summary>
/// Summary of a single OGC style entry for the styles list.
/// </summary>
/// <param name="StyleId">Stable style identifier (the collection's resource name).</param>
/// <param name="Title">Human-readable title for the style.</param>
public sealed record OgcStyleSummary(string StyleId, string Title);

/// <summary>
/// A serialized stylesheet plus the media type to return for it.
/// </summary>
/// <param name="Content">Serialized stylesheet content (MapLibre JSON or SLD XML).</param>
/// <param name="MediaType">IANA media type for <paramref name="Content"/>.</param>
/// <param name="Encoding">Encoding that was produced.</param>
public sealed record OgcStylesheet(string Content, string MediaType, OgcStyleEncoding Encoding);

/// <summary>
/// Descriptive metadata for an OGC style.
/// </summary>
/// <param name="StyleId">Stable style identifier.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Keywords">Keywords describing the style.</param>
/// <param name="License">Optional license identifier.</param>
/// <param name="Version">Optional style revision number, as a string.</param>
public sealed record OgcStyleMetadata(
    string StyleId,
    string Title,
    string? Description,
    IReadOnlyList<string> Keywords,
    string? License,
    string? Version);

/// <summary>
/// Status of an OGC style update attempt.
/// </summary>
public enum OgcStyleUpdateStatus
{
    /// <summary>The style was updated.</summary>
    Updated,

    /// <summary>No style with the requested identifier exists.</summary>
    NotFound,

    /// <summary>The supplied stylesheet was invalid.</summary>
    Invalid
}

/// <summary>
/// Outcome of an OGC style update attempt.
/// </summary>
/// <param name="Status">Outcome status.</param>
/// <param name="ErrorMessage">Human-readable error message when <paramref name="Status"/> is not <see cref="OgcStyleUpdateStatus.Updated"/>.</param>
/// <param name="UnsupportedSymbolizers">
/// Non-fatal lossy-conversion warnings (e.g. an Esri renderer feature the canonical MapLibre style can't
/// represent), each a short "code: guidance" string. Empty/null when the conversion was lossless.
/// </param>
public sealed record OgcStyleUpdateResult(
    OgcStyleUpdateStatus Status,
    string? ErrorMessage,
    IReadOnlyList<string>? UnsupportedSymbolizers = null);

/// <summary>
/// Status of an OGC style create attempt.
/// </summary>
public enum OgcStyleCreateStatus
{
    /// <summary>The style was created.</summary>
    Created,

    /// <summary>A style with the requested identifier already exists.</summary>
    Conflict,

    /// <summary>The supplied stylesheet was invalid.</summary>
    Invalid
}

/// <summary>
/// Outcome of an OGC style create attempt.
/// </summary>
/// <param name="Status">Outcome status.</param>
/// <param name="StyleId">Assigned style identifier when <paramref name="Status"/> is <see cref="OgcStyleCreateStatus.Created"/>.</param>
/// <param name="ErrorMessage">Human-readable error message when creation did not succeed.</param>
public sealed record OgcStyleCreateResult(OgcStyleCreateStatus Status, string? StyleId, string? ErrorMessage);

/// <summary>
/// Status of an OGC style delete attempt.
/// </summary>
public enum OgcStyleDeleteStatus
{
    /// <summary>The style was deleted.</summary>
    Deleted,

    /// <summary>No style with the requested identifier exists in the catalog.</summary>
    NotFound,

    /// <summary>The style cannot be deleted through this surface (e.g. a Phase 1 default per-layer style).</summary>
    Forbidden
}

/// <summary>
/// Outcome of an OGC style delete attempt.
/// </summary>
/// <param name="Status">Outcome status.</param>
/// <param name="ErrorMessage">Human-readable error message when deletion did not succeed.</param>
public sealed record OgcStyleDeleteResult(OgcStyleDeleteStatus Status, string? ErrorMessage);
