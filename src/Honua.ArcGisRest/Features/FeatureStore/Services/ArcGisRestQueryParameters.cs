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
    /// </summary>
    public static string BuildFeatureQueryUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token)
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
        AppendToken(builder, token);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>/query?returnCountOnly=true</c> URL.
    /// </summary>
    public static string BuildCountUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("/query?f=json&returnCountOnly=true");
        AppendCommonQueryParameters(builder, query);
        AppendToken(builder, token);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>/query?returnExtentOnly=true</c> URL.
    /// </summary>
    public static string BuildExtentUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("/query?f=json&returnExtentOnly=true");
        AppendCommonQueryParameters(builder, query);
        AppendOutSr(builder, query);
        AppendToken(builder, token);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>/query?returnIdsOnly=true</c> URL.
    /// </summary>
    public static string BuildObjectIdsUrl(
        string serviceUrl,
        int layerId,
        FeatureQuery query,
        string? token)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("/query?f=json&returnIdsOnly=true");
        AppendCommonQueryParameters(builder, query);
        AppendOrderBy(builder, query);
        AppendToken(builder, token);
        return builder.ToString();
    }

    /// <summary>
    /// Builds the layer-metadata URL used to discover ObjectId field name and
    /// extent at startup time.
    /// </summary>
    /// <remarks>
    /// Reserved for future use alongside
    /// <see cref="IArcGisRestFeatureClient.GetLayerMetadataAsync"/>. The feature
    /// store resolves the object-id field name and geometry type from the
    /// canonical Metadata v2 resource today, so this is not yet on a live path.
    /// </remarks>
    public static string BuildLayerMetadataUrl(string serviceUrl, int layerId, string? token)
    {
        var builder = new StringBuilder();
        builder.Append(serviceUrl).Append('/').Append(layerId).Append("?f=json");
        AppendToken(builder, token);
        return builder.ToString();
    }

    private static void AppendCommonQueryParameters(StringBuilder builder, FeatureQuery query)
    {
        var whereClause = ResolveWhereClause(query);
        AppendEncoded(builder, "where", whereClause);

        if (query.ObjectIds is { Length: > 0 } objectIds)
        {
            // Bound the in-line list to keep the URL well under typical 8KB caps.
            // Callers that need huge batches should switch to the paged path.
            var joined = string.Join(',', objectIds.Take(2000).Select(id => id.ToString(CultureInfo.InvariantCulture)));
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
        // outSR drives the upstream reprojection: when the caller requests an
        // OutputSrid the server returns geometry/envelopes already projected into
        // that SRID. On the extent path, ArcGisRestFeatureStore.ResolveSrid simply
        // echoes the spatialReference the server reports for the (already
        // reprojected) extent, so the requested SRID and the reported SRID cannot
        // diverge. The count path deliberately omits outSR because a count carries
        // no geometry to reproject.
        if (query.OutputSrid is int outSrid && outSrid > 0)
        {
            AppendEncoded(builder, "outSR", outSrid.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendToken(StringBuilder builder, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            AppendEncoded(builder, "token", token);
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
