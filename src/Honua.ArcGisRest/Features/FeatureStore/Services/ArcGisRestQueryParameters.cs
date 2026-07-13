// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// Translates a canonical <see cref="FeatureQuery"/> into ArcGIS REST <c>/query</c>
/// request parameters. The provider issues GET requests with these parameters
/// URL-encoded; structured edits and write-through paths are out of scope for
/// the federated read-through provider.
/// </summary>
internal static class ArcGisRestQueryParameters
{
    /// <summary>
    /// Builds the <c>/query</c> URL for a feature page request.
    /// The API token is not included in the URL; callers must pass it as the
    /// <c>X-Esri-Authorization</c> request header via
    /// <see cref="BuildAuthorizationHeader"/> so it does not appear in HTTP logs,
    /// telemetry traces, or upstream server access logs.
    /// </summary>
    /// <param name="serviceUrl">Canonical FeatureServer/MapServer service root URL.</param>
    /// <param name="layerId">Zero-based ArcGIS layer index.</param>
    /// <param name="query">Feature query specification.</param>
    /// <param name="token">Ignored; reserved for backward compatibility. Pass <see langword="null"/>.</param>
    public static string BuildFeatureQueryUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token = null)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("/query?f=json");
        AppendCommonQueryParameters(builder, query);
        AppendOutFields(builder, query);
        AppendReturnGeometry(builder, returnGeometry: true);
        AppendOutSr(builder, query);
        AppendOrderBy(builder, query);
        AppendPaging(builder, query);
        AppendDistinct(builder, query);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>/query?returnCountOnly=true</c> URL.
    /// The API token is not included in the URL; callers must pass it as the
    /// <c>X-Esri-Authorization</c> request header via <see cref="BuildAuthorizationHeader"/>.
    /// </summary>
    /// <param name="serviceUrl">Canonical FeatureServer/MapServer service root URL.</param>
    /// <param name="layerId">Zero-based ArcGIS layer index.</param>
    /// <param name="query">Feature query specification.</param>
    /// <param name="token">Ignored; reserved for backward compatibility. Pass <see langword="null"/>.</param>
    public static string BuildCountUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token = null)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("/query?f=json&returnCountOnly=true");
        AppendCommonQueryParameters(builder, query);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>/query?returnExtentOnly=true</c> URL.
    /// The API token is not included in the URL; callers must pass it as the
    /// <c>X-Esri-Authorization</c> request header via <see cref="BuildAuthorizationHeader"/>.
    /// </summary>
    /// <param name="serviceUrl">Canonical FeatureServer/MapServer service root URL.</param>
    /// <param name="layerId">Zero-based ArcGIS layer index.</param>
    /// <param name="query">Feature query specification.</param>
    /// <param name="token">Ignored; reserved for backward compatibility. Pass <see langword="null"/>.</param>
    public static string BuildExtentUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token = null)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("/query?f=json&returnExtentOnly=true");
        AppendCommonQueryParameters(builder, query);
        AppendOutSr(builder, query);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>/query?returnIdsOnly=true</c> URL.
    /// The API token is not included in the URL; callers must pass it as the
    /// <c>X-Esri-Authorization</c> request header via <see cref="BuildAuthorizationHeader"/>.
    /// </summary>
    /// <param name="serviceUrl">Canonical FeatureServer/MapServer service root URL.</param>
    /// <param name="layerId">Zero-based ArcGIS layer index.</param>
    /// <param name="query">Feature query specification.</param>
    /// <param name="token">Ignored; reserved for backward compatibility. Pass <see langword="null"/>.</param>
    public static string BuildObjectIdsUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token = null)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("/query?f=json&returnIdsOnly=true");
        AppendCommonQueryParameters(builder, query);
        AppendOrderBy(builder, query);
        // ArcGIS honors resultOffset/resultRecordCount on returnIdsOnly requests,
        // so paging must be applied here to match the feature-query path.
        AppendPaging(builder, query);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the layer-metadata URL used to discover ObjectId field name and
    /// extent at startup time.
    /// The API token is not included in the URL; callers must pass it as the
    /// <c>X-Esri-Authorization</c> request header via <see cref="BuildAuthorizationHeader"/>.
    /// </summary>
    /// <param name="serviceUrl">Canonical FeatureServer/MapServer service root URL.</param>
    /// <param name="layerId">Zero-based ArcGIS layer index.</param>
    /// <param name="token">Ignored; reserved for backward compatibility. Pass <see langword="null"/>.</param>
    public static string BuildLayerMetadataUrl(string serviceUrl, int layerId, string? token = null)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("?f=json");
        return builder.ToString();
    }

    /// <summary>
    /// Returns the value for the <c>X-Esri-Authorization</c> request header that
    /// carries the ArcGIS API token, or <see langword="null"/> when no token is
    /// configured. Sending the token as a request header rather than a URL query
    /// parameter prevents it from appearing in HTTP access logs, OpenTelemetry
    /// traces (<c>url.full</c>), and upstream server access logs.
    /// </summary>
    public static string? BuildAuthorizationHeader(string? token)
        => string.IsNullOrWhiteSpace(token) ? null : $"Bearer {token}";

    private static void AppendCommonQueryParameters(StringBuilder builder, FeatureQuery query)
    {
        // Unsupported query aspects must fail loudly rather than return over-broad results.
        // The federated provider cannot translate parameterized SQL fragments or temporal
        // filters into the ArcGIS REST wire format; returning unfiltered data would expose
        // rows the caller expected to be filtered.
        if (query.EnforcedSqlFilter is not null)
        {
            throw new NotSupportedException(
                "ArcGIS REST federated provider does not support EnforcedSqlFilter. " +
                "Layers with server-enforced definition or security filters cannot be federated via this provider.");
        }

        if (query.SqlFilter is not null)
        {
            throw new NotSupportedException(
                "ArcGIS REST federated provider does not support parameterized SqlFilter. " +
                "Use the Where property for GeoServices REST SQL expressions.");
        }

        if (query.TemporalFilter is not null)
        {
            throw new NotSupportedException(
                "ArcGIS REST federated provider does not support TemporalFilter on this path. " +
                "Temporal queries against federated ArcGIS layers are not yet implemented.");
        }

        if (query.SpatialFilter is { IsSimpleEnvelope: false } unsupportedSpatial)
        {
            // Non-envelope geometries (polygons, polylines, points) and non-intersects
            // spatial relationships cannot be silently dropped — the caller would receive
            // results as if no spatial filter were applied, returning over-broad data.
            // Distance-based (WithinDistance/BeyondDistance) and KNN filters are also
            // not forwarded. Throw so the caller gets a clear error rather than wrong results.
            throw new NotSupportedException(
                $"ArcGIS REST federated provider does not support '{unsupportedSpatial.SpatialRelationship}' " +
                "with a non-envelope geometry. Only axis-aligned envelope filters (IsSimpleEnvelope=true) are " +
                "forwarded to the upstream service on this path.");
        }

        var whereClause = ResolveWhereClause(query);
        AppendEncoded(builder, "where", whereClause);

        if (query.ObjectIds is { Length: > 0 } objectIds)
        {
            if (objectIds.Length > 2000)
            {
                throw new NotSupportedException(
                    $"ArcGIS REST federated provider cannot forward more than 2000 object IDs in a single GET request " +
                    $"(requested {objectIds.Length}). Chunk the request into batches of at most 2000 object IDs.");
            }

            var joined = string.Join(',', objectIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            AppendEncoded(builder, "objectIds", joined);
        }

        if (query.SpatialFilter is { } spatial && spatial.IsSimpleEnvelope)
        {
            AppendSpatialEnvelope(builder, spatial);
        }
    }

    private static void AppendSpatialEnvelope(StringBuilder builder, SpatialFilter spatial)
    {
        if (spatial.EnvelopeMinX is not double minX
            || spatial.EnvelopeMinY is not double minY
            || spatial.EnvelopeMaxX is not double maxX
            || spatial.EnvelopeMaxY is not double maxY)
        {
            return;
        }

        var geometry = string.Create(CultureInfo.InvariantCulture, $"{minX},{minY},{maxX},{maxY}");
        AppendEncoded(builder, "geometry", geometry);
        AppendEncoded(builder, "geometryType", "esriGeometryEnvelope");
        AppendEncoded(builder, "spatialRel", "esriSpatialRelIntersects");

        if (spatial.Srid is int srid && srid > 0)
        {
            AppendEncoded(builder, "inSR", srid.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendOutFields(StringBuilder builder, FeatureQuery query)
    {
        if (query.OutFields is { Length: > 0 } fields)
        {
            var joined = string.Join(',', fields);
            AppendEncoded(builder, "outFields", joined);
        }
        else
        {
            AppendEncoded(builder, "outFields", "*");
        }
    }

    private static void AppendOrderBy(StringBuilder builder, FeatureQuery query)
    {
        if (query.OrderBy is not { Length: > 0 } orderBy)
        {
            return;
        }

        // OrderBy field identifiers must remain SQL identifiers and cannot be
        // URL-escaped into safety, so a blank field would emit a malformed
        // " ASC" token that the upstream server rejects with an opaque 400.
        // Reject it here with a clear message, mirroring the explicit validation
        // applied to spatial filters and distinct.
        foreach (var clause in orderBy.Where(clause => string.IsNullOrWhiteSpace(clause.Field)))
        {
            throw new NotSupportedException(
                "ArcGIS REST provider cannot forward an order-by clause with an empty or whitespace field name.");
        }

        var clauses = string.Join(',', orderBy.Select(c => $"{c.Field} {(c.Ascending ? "ASC" : "DESC")}"));
        AppendEncoded(builder, "orderByFields", clauses);
    }

    private static void AppendPaging(StringBuilder builder, FeatureQuery query)
    {
        if (query.Offset is int offset && offset > 0)
        {
            AppendEncoded(builder, "resultOffset", offset.ToString(CultureInfo.InvariantCulture));
        }

        if (query.Limit is int limit && limit > 0)
        {
            AppendEncoded(builder, "resultRecordCount", limit.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendDistinct(StringBuilder builder, FeatureQuery query)
    {
        if (query.Distinct)
        {
            AppendEncoded(builder, "returnDistinctValues", "true");
        }
    }

    private static void AppendReturnGeometry(StringBuilder builder, bool returnGeometry)
    {
        AppendEncoded(builder, "returnGeometry", returnGeometry ? "true" : "false");
    }

    private static void AppendOutSr(StringBuilder builder, FeatureQuery query)
    {
        if (query.OutputSrid is int outSrid && outSrid > 0)
        {
            AppendEncoded(builder, "outSR", outSrid.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendEncoded(StringBuilder builder, string key, string value)
    {
        builder.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static string ResolveWhereClause(FeatureQuery query)
    {
        // The federated provider intentionally forwards the canonical WHERE
        // clause as-is. The GeoServices REST WHERE syntax is the same lingua
        // franca on both sides of the call.
        if (!string.IsNullOrWhiteSpace(query.Where))
        {
            return query.Where!;
        }

        return "1=1";
    }
}
