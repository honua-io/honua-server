// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Threading;
using System.Threading.Tasks;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Translates the GeoServices-style layer selection bundle (<c>where</c>, <c>objectIds</c>,
/// <c>geometry</c>/<c>geometryType</c>/<c>inSR</c>/<c>spatialRel</c>,
/// <c>time</c>/<c>timeRelation</c>) into the canonical <see cref="FeatureQuery"/> selection
/// for a catalog layer. The synchronous spatial analytics endpoints and the
/// <c>source.honua-layer</c> geoprocessing connector share ONE implementation, so an
/// advertised selector means the same thing on both surfaces (#4624). The translated query
/// carries only caller selectors: permanent filters, row-level security and field masks stay
/// with the feature store, which ANDs them ahead of the caller's selection.
/// </summary>
public interface ILayerSelectionFilterTranslator
{
    /// <summary>
    /// Translates <paramref name="selection"/> against <paramref name="resource"/>.
    /// </summary>
    /// <returns>The translated selection, or a caller-facing error when a selector is invalid.</returns>
    Task<LayerSelectionTranslation> TranslateAsync(
        LayerSelectionFilter selection,
        MetadataV2Resource resource,
        CancellationToken cancellationToken = default);
}

/// <summary>The raw, protocol-neutral layer selection inputs.</summary>
public sealed record LayerSelectionFilter
{
    /// <summary>ArcGIS SQL <c>where</c> clause.</summary>
    public string? Where { get; init; }

    /// <summary>Comma-separated feature identifiers.</summary>
    public string? ObjectIds { get; init; }

    /// <summary>GeoServices geometry filter.</summary>
    public string? Geometry { get; init; }

    /// <summary>GeoServices geometry type of <see cref="Geometry"/>.</summary>
    public string? GeometryType { get; init; }

    /// <summary>Spatial reference of <see cref="Geometry"/>.</summary>
    public string? InSr { get; init; }

    /// <summary>GeoServices spatial relationship.</summary>
    public string? SpatialRel { get; init; }

    /// <summary>FeatureServer temporal filter.</summary>
    public string? Time { get; init; }

    /// <summary>FeatureServer temporal relationship.</summary>
    public string? TimeRelation { get; init; }
}

/// <summary>
/// The result of <see cref="ILayerSelectionFilterTranslator.TranslateAsync"/>: either a
/// <see cref="Query"/> carrying <c>Where</c>/<c>SqlFilter</c>/<c>ObjectIds</c>/<c>SpatialFilter</c>,
/// or an <see cref="ErrorTitle"/>/<see cref="ErrorDetail"/> pair safe to return to the caller.
/// </summary>
public sealed record LayerSelectionTranslation
{
    /// <summary>The translated selection; null when <see cref="ErrorDetail"/> is set.</summary>
    public FeatureQuery? Query { get; init; }

    /// <summary>Short error title (for example <c>Invalid geometry parameter</c>).</summary>
    public string? ErrorTitle { get; init; }

    /// <summary>Caller-facing error detail.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>Creates a successful translation.</summary>
    public static LayerSelectionTranslation Success(FeatureQuery query) => new() { Query = query };

    /// <summary>Creates a failed translation.</summary>
    public static LayerSelectionTranslation Failure(string title, string detail)
        => new() { ErrorTitle = title, ErrorDetail = detail };
}

/// <summary>
/// Raised by a DAG source when a caller-supplied selector cannot be honored. The message is
/// caller-facing, so executors surface it verbatim as an input error instead of collapsing it
/// to an exception type name.
/// </summary>
public sealed class DagSourceSelectionException : Exception
{
    /// <summary>Creates the exception with a caller-facing message.</summary>
    public DagSourceSelectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a caller-facing message and inner cause.</summary>
    public DagSourceSelectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message.</summary>
    public DagSourceSelectionException()
    {
    }
}
