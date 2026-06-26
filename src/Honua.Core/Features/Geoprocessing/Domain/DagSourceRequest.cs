// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// The resolved upstream query a DAG source connector executes against a remote
/// source. Built by the geoprocessing source executor from the durable step
/// inputs and handed to the matching <c>IDagFeatureSource</c>, which translates it
/// into the source-native query (an ArcGIS REST <c>query</c>, a WFS
/// <c>GetFeature</c>, an OGC API Features <c>items</c> request, a catalog
/// <c>FeatureQuery</c>, or an external PostGIS query) and streams the matching
/// features back page-by-page.
/// </summary>
public sealed record DagSourceRequest
{
    /// <summary>
    /// Honua catalog layer identifier for <c>source.honua-layer</c>. Ignored by the
    /// remote-endpoint sources.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Remote service root URL for the endpoint sources (ArcGIS FeatureServer/MapServer
    /// root, OGC API landing/collection URL, or WFS service URL). Ignored by
    /// <c>source.honua-layer</c> and <c>source.postgis</c>.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// ArcGIS layer index within the FeatureServer for <c>source.esri-featureserver</c>.
    /// </summary>
    public int? EsriLayerId { get; init; }

    /// <summary>
    /// OGC API Features collection id, or WFS feature type name, for the OGC/WFS sources.
    /// </summary>
    public string? CollectionId { get; init; }

    /// <summary>
    /// Optional attribute filter. For <c>source.honua-layer</c> and
    /// <c>source.esri-featureserver</c> this is a GeoServices-style SQL <c>where</c>
    /// clause; for <c>source.ogc-features</c> a CQL2-text filter; for
    /// <c>source.postgis</c> a parameter-free SQL predicate appended after WHERE.
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    /// Optional bounding-box filter as <c>minX,minY,maxX,maxY</c> in the source CRS.
    /// </summary>
    public string? Bbox { get; init; }

    /// <summary>
    /// Optional comma-separated output field allow-list. <see langword="null"/> selects all.
    /// </summary>
    public string? OutFields { get; init; }

    /// <summary>
    /// Optional output SRID; the reader requests this CRS from the source where the
    /// protocol supports server-side reprojection.
    /// </summary>
    public int? OutputSrid { get; init; }

    /// <summary>
    /// Page size hint for the underlying paginated reader.
    /// </summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>
    /// Optional incremental-extract watermark. When supplied the reader narrows the
    /// upstream query to features changed at or after this value (an ISO-8601 instant
    /// or a monotonic cursor understood by the source). The persistence of the next
    /// watermark is owned by the incremental-extract orchestration, not the reader.
    /// </summary>
    public string? Since { get; init; }

    /// <summary>
    /// Optional name of the attribute the <see cref="Since"/> watermark filters on
    /// (for example an <c>updated_at</c> / <c>last_edited_date</c> column). Required
    /// for <c>source.postgis</c> and <c>source.honua-layer</c> incremental reads.
    /// </summary>
    public string? WatermarkField { get; init; }

    /// <summary>
    /// Hard ceiling on the number of pages the reader will fetch, guarding against a
    /// non-advancing or pathological source. <see langword="null"/> uses the reader default.
    /// </summary>
    public int? MaxPages { get; init; }

    /// <summary>
    /// Per-request timeout in seconds for the remote-endpoint sources.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Optional ArcGIS credential descriptor for <c>source.esri-featureserver</c>,
    /// reusing the import path's portal-token / secret-reference mechanism.
    /// </summary>
    public GeoservicesCredentialDescriptor? EsriCredentials { get; init; }

    /// <summary>
    /// Optional HTTP Basic username for the OGC/WFS sources.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional HTTP Basic password for the OGC/WFS sources (resolved from a secret
    /// reference by the executor before the request reaches the reader).
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Resolved connection string for <c>source.postgis</c>. The executor resolves this
    /// from a registered secure connection (name/id) using the same secret-handling as
    /// <c>sink.external-postgis</c>; raw connection strings never cross the DAG.
    /// </summary>
    public string? PostgisConnectionString { get; init; }

    /// <summary>
    /// Schema-qualified table or view for <c>source.postgis</c>.
    /// </summary>
    public string? PostgisSchema { get; init; }

    /// <summary>Table or view name for <c>source.postgis</c>.</summary>
    public string? PostgisTable { get; init; }

    /// <summary>Geometry column for <c>source.postgis</c>. Defaults to <c>geom</c>.</summary>
    public string? PostgisGeometryColumn { get; init; }
}
