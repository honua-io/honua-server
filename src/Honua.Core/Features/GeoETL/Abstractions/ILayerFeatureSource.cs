// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Abstractions;

/// <summary>
/// How a layer-scope geoprocessing input is supplied. A GP process invocation
/// accepts an input layer reference that is one of: an inline GeoJSON
/// FeatureCollection, a catalog layer id, or a stored query result.
/// </summary>
public enum LayerFeatureSourceKind
{
    /// <summary>
    /// The features are supplied inline as a GeoJSON FeatureCollection document.
    /// </summary>
    InlineGeoJson,

    /// <summary>
    /// The features come from a catalog layer identified by id, optionally filtered.
    /// </summary>
    CatalogLayer,

    /// <summary>
    /// The features come from a previously materialized query result.
    /// </summary>
    QueryResult
}

/// <summary>
/// A reference to the input feature collection a layer-scope GP process operates on.
/// Exactly one of <see cref="InlineGeoJson"/> / <see cref="LayerId"/> /
/// <see cref="QueryResultId"/> is populated according to <see cref="Kind"/>.
/// </summary>
public sealed record LayerFeatureReference
{
    /// <summary>
    /// Discriminator selecting which member carries the reference.
    /// </summary>
    public required LayerFeatureSourceKind Kind { get; init; }

    /// <summary>
    /// Inline GeoJSON FeatureCollection document (for <see cref="LayerFeatureSourceKind.InlineGeoJson"/>).
    /// </summary>
    public string? InlineGeoJson { get; init; }

    /// <summary>
    /// Catalog layer identifier (for <see cref="LayerFeatureSourceKind.CatalogLayer"/>).
    /// </summary>
    public string? LayerId { get; init; }

    /// <summary>
    /// Stored query-result identifier (for <see cref="LayerFeatureSourceKind.QueryResult"/>).
    /// </summary>
    public string? QueryResultId { get; init; }

    /// <summary>
    /// Optional ArcGIS-style SQL filter applied to a catalog layer or query result.
    /// </summary>
    public string? Where { get; init; }
}

/// <summary>
/// Resolves a <see cref="LayerFeatureReference"/> into a streaming feature set so a
/// layer-scope geoprocessing process can apply a geometry operation across the
/// features and emit an output layer / artifact. This is the read-side reuse seam
/// that mirrors <see cref="IFeatureSinkWriter"/> on the write side: the inline-GeoJSON
/// case is fully managed and lives in Honua.Core, while the catalog-layer and
/// query-result cases are provided by a storage provider (Honua.Postgres) so
/// Honua.Core stays free of Npgsql and the serving image stays on the lean path.
/// </summary>
public interface ILayerFeatureSource
{
    /// <summary>
    /// Whether this source can resolve the supplied reference kind.
    /// </summary>
    /// <param name="kind">Reference kind to test.</param>
    bool CanResolve(LayerFeatureSourceKind kind);

    /// <summary>
    /// Streams the features identified by <paramref name="reference"/> one at a time.
    /// </summary>
    /// <param name="reference">The input layer reference.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>The resolved feature stream.</returns>
    IAsyncEnumerable<IFeature> ReadAsync(
        LayerFeatureReference reference,
        CancellationToken cancellationToken = default);
}
