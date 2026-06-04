// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Utility methods for FeatureServer endpoints
/// </summary>
internal static partial class FeatureServerEndpoints
{
    private static readonly string[] _queryAcceptMediaTypes =
    [
        "application/json",
        "text/json",
        "application/geo+json",
        "application/x-protobuf",
        "application/vnd.google.protobuf",
        "application/vnd.flatgeobuf",
        "application/x-flatgeobuf",
        "application/flatgeobuf",
        "application/geobuf",
        "application/vnd.apache.parquet",
        "application/vnd.apache.arrow.stream"
    ];

    private static readonly Dictionary<string, string> _queryAcceptFormatByMediaType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/json"] = "json",
            ["text/json"] = "json",
            ["application/geo+json"] = "geojson",
            ["application/x-protobuf"] = "pbf",
            ["application/vnd.google.protobuf"] = "pbf",
            ["application/vnd.flatgeobuf"] = "fgb",
            ["application/x-flatgeobuf"] = "fgb",
            ["application/flatgeobuf"] = "fgb",
            ["application/geobuf"] = "geobuf",
            ["application/vnd.apache.parquet"] = "parquet",
            ["application/vnd.apache.arrow.stream"] = "arrow"
        };

    /// <summary>
    /// Allowed query parameters for each endpoint
    /// </summary>
    private static class AllowedQueryParameters
    {
        // Standard Esri metadata-request parameters that ArcGIS clients (notably
        // ArcGIS Pro / arcpy) append by default when resolving a FeatureServer
        // service or layer by URL. They do not change the metadata document, but a
        // strict allowlist that rejects them returns 400 and makes ArcGIS Pro report
        // the layer as "does not exist or is not supported" (MakeFeatureLayer /
        // Add Data fails). Accept and ignore them, mirroring the layer-query
        // treatment of unsupported-but-harmless ArcGIS client parameters (#1276).
        private static readonly string[] EsriMetadataClientParameters =
        [
            "returnFieldGroups",
            "returnPbfFeatureEncodings",
            // The ArcGIS Maps SDK for .NET appends returnAdvancedSymbols to the
            // layer/service metadata GET during ServiceFeatureTable.LoadAsync. #1455
            // accepted it on the layer-query endpoint but not here, so LoadAsync
            // returned 400 and the entire .NET FeatureServer client was blocked.
            "returnAdvancedSymbols"
        ];

        public static readonly FrozenSet<string> ServiceMetadata =
            new[] { "f" }
                .Concat(EsriMetadataClientParameters)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> LayerMetadata =
            new[] { "f" }
                .Concat(EsriMetadataClientParameters)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Query = GeoServicesRequestValueHelpers.LayerQueryAllowedParameters;

        public static readonly FrozenSet<string> ServiceQuery = GeoServicesRequestValueHelpers.ServiceQueryAllowedParameters;

        public static readonly FrozenSet<string> GenerateRenderer = new[]
            {
                "classificationDef",
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> ApplyEdits = new[]
            {
                "f",
                "rollbackOnFailure",
                "useGlobalIds",
                "gdbVersion",
                "returnEditMoment",
                "attachments"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> DeleteFeatures = new[]
            {
                "f",
                "rollbackOnFailure",
                "useGlobalIds",
                "gdbVersion",
                "returnEditMoment",
                "objectIds",
                "where",
                "geometry",
                "geometryType",
                "spatialRel",
                "inSR"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> QueryRelatedRecords = new[]
            {
                "objectIds",
                "relationshipId",
                "outFields",
                "where",
                "definitionExpression",
                "returnGeometry",
                "resultOffset",
                "resultRecordCount",
                "orderByFields",
                "returnCountOnly",
                "outSR",
                "returnZ",
                "returnM",
                "geometryPrecision",
                "maxAllowableOffset",
                "gdbVersion",
                "sqlFormat",
                "returnTrueCurves",
                "historicMoment",
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> GetEstimates =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> ServiceGetEstimates =
            new[] { "f", "layers", "layerId" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> QueryDomains =
            new[] { "f", "layers", "layerId" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Relationships =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> QueryTopFeatures = new[]
            {
                "topFilter",
                "where",
                "outFields",
                "orderByFields",
                "geometry",
                "inSR",
                "outSR",
                "geometryType",
                "spatialRel",
                "returnGeometry",
                "returnZ",
                "returnM",
                "resultOffset",
                "resultRecordCount",
                "time",
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> QueryDateBins = new[]
            {
                "binField",
                "bin",
                "where",
                "outStatistics",
                "geometry",
                "inSR",
                "geometryType",
                "spatialRel",
                "time",
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> QueryBins = new[]
            {
                "bin",
                "where",
                "outStatistics",
                "geometry",
                "inSR",
                "geometryType",
                "spatialRel",
                "time",
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Tiles =
            new[] { "where", "time" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> H3Tiles =
            new[] { "where", "resolution" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> QueryH3 = new[]
            {
                "resolution",
                "where",
                "kRingDistance",
                "outStatistics",
                "summaries",
                "groupBy",
                "include",
                "page",
                "requestId",
                "sourceId",
                "schemaVersion",
                "index",
                "metadata",
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static FrozenSet<string> FeatureServerQueryAllowedParameters => AllowedQueryParameters.Query;
    internal static FrozenSet<string> FeatureServerServiceQueryAllowedParameters => AllowedQueryParameters.ServiceQuery;
    internal static FrozenSet<string> FeatureServerQueryFormats => SupportedFormats.Query;
    internal static FrozenSet<string> JsonOnlyFormats => SupportedFormats.JsonOnly;

    private static class SupportedFormats
    {
        public static readonly FrozenSet<string> Query =
            new[] { "json", "pjson", "geojson", "pbf", "fgb", "geobuf", "parquet", "arrow" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> JsonOnly =
            new[] { "json", "pjson" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets timeout-aware cancellation token
    /// </summary>
    internal static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
        => GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);

    private static AdvancedQueryCapabilities BuildAdvancedQueryCapabilities(
        bool supportsAdvancedQueries,
        bool supportsStatistics,
        bool supportsOrderBy,
        bool supportsDistinct,
        bool supportsPagination,
        bool supportsQueryAttachments = false)
    {
        return new AdvancedQueryCapabilities
        {
            UseStandardizedQueries = true,
            SupportsStatistics = supportsStatistics,
            SupportsHavingClause = supportsStatistics,
            SupportsOrderBy = supportsOrderBy,
            SupportsDistinct = supportsDistinct,
            SupportsCountDistinct = supportsStatistics,
            SupportsPagination = supportsPagination,
            SupportsReturningQueryExtent = supportsAdvancedQueries,
            SupportsQueryWithDistance = supportsAdvancedQueries,
            SupportsSqlExpression = supportsAdvancedQueries,
            // queryTopFeatures is served unconditionally by HandleQueryTopFeatures;
            // advertise it whenever the layer supports advanced queries so Esri
            // clients (arcgis query_top_features) discover the operation.
            SupportsTopFeaturesQuery = supportsAdvancedQueries,
            SupportsBatchEditing = supportsAdvancedQueries,
            // Mirror the layer-root supportsQueryAttachments flag into the nested
            // operations/advanced-query-capabilities block. The @arcgis/core JS SDK
            // gates queryAttachments({where}) on this nested flag, so it must stay
            // consistent with the root flag (true when the layer has attachments),
            // otherwise a layer that advertises attachments at the root is refused the
            // operation because the nested flag reported false (#1453).
            SupportsQueryAttachments = supportsQueryAttachments
        };
    }

    /// <summary>
    /// Validates allowed parameters
    /// </summary>
    internal static bool TryValidateAllowedParameters(
        IQueryCollection query,
        ICommonQueryValidator queryValidator,
        FrozenSet<string> allowedParameters,
        out string? error)
        => GeoServicesRequestValueHelpers.TryValidateAllowedParameters(query, queryValidator, allowedParameters, out error);

    internal static bool TryValidateAllowedParameters(
        IReadOnlyDictionary<string, StringValues> values,
        ICommonQueryValidator queryValidator,
        FrozenSet<string> allowedParameters,
        out string? error)
        => GeoServicesRequestValueHelpers.TryValidateAllowedParameters(values, queryValidator, allowedParameters, out error);

    private static string[] NormalizeSupportedQueryFormats(string[]? formats, bool supportsGeobufOutput)
    {
        var normalizedFormats = new List<string>();

        if (formats != null)
        {
            foreach (var format in formats)
            {
                AddSupportedFormat(normalizedFormats, format);
            }
        }

        AddSupportedFormat(normalizedFormats, "JSON");
        AddSupportedFormat(normalizedFormats, "GEOJSON");
        AddSupportedFormat(normalizedFormats, "PBF");
        AddSupportedFormat(normalizedFormats, "FGB");
        AddSupportedFormat(normalizedFormats, "PARQUET");

        if (supportsGeobufOutput)
        {
            AddSupportedFormat(normalizedFormats, "GEOBUF");
        }

        AddSupportedFormat(normalizedFormats, "ARROW");

        return [.. normalizedFormats];
    }

    private static void AddSupportedFormat(List<string> formats, string format)
    {
        if (formats.Any(existing => existing.Equals(format, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        formats.Add(format.ToUpperInvariant());
    }

    internal static bool HasAttachmentSurface(IServiceProvider services)
        => services.GetService<IAttachmentStore>() != null;

    internal static ServiceQueryLayerResponse MapServiceQueryLayerResponse(int layerId, QueryResponse response)
    {
        return new ServiceQueryLayerResponse
        {
            Id = layerId,
            GeometryType = response.GeometryType,
            SpatialReference = response.SpatialReference,
            DisplayFieldName = response.DisplayFieldName,
            Fields = response.Fields,
            HasZ = response.HasZ,
            HasM = response.HasM,
            ObjectIdFieldName = response.ObjectIdFieldName,
            ObjectIds = response.ObjectIds,
            Count = response.Count,
            Extent = response.Extent,
            UniqueIdField = response.UniqueIdField,
            GlobalIdFieldName = response.GlobalIdFieldName,
            Features = response.Features,
            ExceededTransferLimit = response.ExceededTransferLimit
        };
    }

    /// <summary>
    /// Normalizes the GeoServices <c>layers</c> parameter to its inner token list,
    /// accepting both the comma-separated form (<c>0</c> / <c>0,1</c>) and the Esri
    /// JSON-array form (<c>[0]</c> / <c>[0,1]</c>) the ArcGIS API for Python sends.
    /// </summary>
    internal static string StripLayerListBrackets(string rawValue)
    {
        var normalized = rawValue.Trim();
        if (normalized.Length >= 2 &&
            normalized.StartsWith('[') &&
            normalized.EndsWith(']'))
        {
            normalized = normalized[1..^1];
        }

        return normalized;
    }

    internal static bool TryParseLayerIdList(string rawValue, out int[] layerIds, out string? error)
    {
        layerIds = [];
        error = null;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            error = "layers parameter must contain at least one layer ID.";
            return false;
        }

        var tokens = StripLayerListBrackets(rawValue).Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Any(static token => token.Length == 0))
        {
            error = "layers parameter must contain only numeric layer IDs.";
            return false;
        }

        var ids = new HashSet<int>();
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                error = "layers parameter must contain only numeric layer IDs.";
                return false;
            }

            ids.Add(layerId);
        }

        if (ids.Count == 0)
        {
            error = "layers parameter must contain at least one layer ID.";
            return false;
        }

        layerIds = ids.ToArray();
        return true;
    }

    internal static string ResolveRequestedQueryFormat(QueryParameters queryParams, StringValues acceptHeader)
    {
        if (queryParams.FormatSpecified)
        {
            return queryParams.F;
        }

        return TryResolveQueryFormatFromAcceptHeader(acceptHeader, out var format)
            ? format
            : queryParams.F;
    }

    private static bool TryResolveQueryFormatFromAcceptHeader(StringValues acceptHeader, out string format)
    {
        format = "json";
        if (!ContentNegotiationHelpers.TrySelectBestMediaType(_queryAcceptMediaTypes, acceptHeader, out var selectedMediaType) ||
            !_queryAcceptFormatByMediaType.TryGetValue(selectedMediaType, out var resolvedFormat))
        {
            return false;
        }

        format = resolvedFormat;
        return true;
    }

    internal static bool TryValidateOutputFormat(
        string? format,
        FrozenSet<string> supportedFormats,
        out string normalizedFormat,
        out string? error)
    {
        error = null;
        normalizedFormat = "json";

        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        var trimmed = format.Trim();
        if (!supportedFormats.Contains(trimmed))
        {
            error = $"Output format '{trimmed}' is not supported. Supported formats: {string.Join(", ", supportedFormats)}.";
            return false;
        }

        normalizedFormat = string.Equals(trimmed, "pjson", StringComparison.OrdinalIgnoreCase)
            ? "json"
            : trimmed.ToLowerInvariant();
        return true;
    }
}
