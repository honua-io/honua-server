// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Utility methods for FeatureServer endpoints
/// </summary>
internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Allowed query parameters for each endpoint
    /// </summary>
    private static class AllowedQueryParameters
    {
        public static readonly FrozenSet<string> ServiceMetadata =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> LayerMetadata =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Query = new[]
            {
                "where",
                "objectIds",
                "outFields",
                "orderByFields",
                "geometry",
                "inSR",
                "outSR",
                "geometryType",
                "spatialRel",
                "units",
                "f",
                "resultOffset",
                "resultRecordCount",
                "nearestCount",
                "distance",
                "returnGeometry",
                "returnIdsOnly",
                "returnCountOnly",
                "returnExtentOnly",
                "returnDistance",
                "returnCentroid",
                "returnDistinctValues",
                "returnZ",
                "returnM",
                "returnTrueCurves",
                "returnExceededLimitFeatures",
                "time",
                "timeRelation",
                "geometryPrecision",
                "maxAllowableOffset",
                "resultType",
                "outStatistics",
                "groupByFieldsForStatistics",
                "having",
                "sqlFormat",
                "gdbVersion",
                "quantizationParameters",
                "datumTransformation"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> ServiceQuery =
            Query
                .Append("layerId")
                .Append("layers")
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            new[] { "where" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static FrozenSet<string> FeatureServerQueryAllowedParameters => AllowedQueryParameters.Query;
    internal static FrozenSet<string> FeatureServerServiceQueryAllowedParameters => AllowedQueryParameters.ServiceQuery;
    internal static FrozenSet<string> FeatureServerQueryFormats => SupportedFormats.Query;
    internal static FrozenSet<string> JsonOnlyFormats => SupportedFormats.JsonOnly;

    private static class SupportedFormats
    {
        public static readonly FrozenSet<string> Query =
            new[] { "json", "pjson", "geojson", "pbf" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> JsonOnly =
            new[] { "json", "pjson" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets timeout-aware cancellation token
    /// </summary>
    internal static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        const string tokenKey = "FeatureServerQueryTimeoutToken";

        if (context.Items.TryGetValue(tokenKey, out var existing) && existing is CancellationToken cachedToken)
        {
            return cachedToken;
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var queryTimeout = limits.Query.QueryTimeout;

        var baseToken = context.RequestAborted;
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            baseToken = timeoutToken;
        }

        if (queryTimeout <= TimeSpan.Zero)
        {
            context.Items[tokenKey] = baseToken;
            return baseToken;
        }

        var timeoutCts = new CancellationTokenSource(queryTimeout);
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(baseToken, timeoutCts.Token);

        context.Response.RegisterForDispose(timeoutCts);
        context.Response.RegisterForDispose(combinedCts);

        context.Items[tokenKey] = combinedCts.Token;
        return combinedCts.Token;
    }

    /// <summary>
    /// Maps service definition to FeatureServer response
    /// </summary>
    private static FeatureServerResponse MapServiceToResponse(ServiceDefinition service, QueryLimits queryLimits)
    {
        var objectIdField = ResolveServiceObjectIdField(service);
        var supportsStatistics = true;
        var supportsAdvancedQueries = service.SupportsAdvancedQueries;
        var hasGeometry = service.Layers.Any(layer => layer.HasGeometry);

        return new FeatureServerResponse
        {
            ServiceName = service.Name,
            ServiceDescription = service.Description,
            Layers = [.. service.Layers.Select(MapLayerInfo)],
            SpatialReference = service.SpatialReference.ToSpatialReferenceInfo(),
            InitialExtent = service.EffectiveExtent.HasValue ? service.EffectiveExtent.Value.ToExtentInfo() : null,
            FullExtent = service.EffectiveExtent.HasValue ? service.EffectiveExtent.Value.ToExtentInfo() : null,
            MaxRecordCount = queryLimits.MaxRecordCount,
            SupportedQueryFormats = NormalizeSupportedQueryFormats(service.SupportedFormats),
            Capabilities = BuildServiceCapabilities(service),
            Fields = [.. service.AllFields.Select(MapFieldInfo)],
            ObjectIdField = objectIdField,
            SupportsAdvancedQueries = supportsAdvancedQueries,
            SupportsStatistics = supportsStatistics,
            HasGeometryProperties = hasGeometry,
            AllowGeometryUpdates = service.SupportsEditing
        };
    }

    /// <summary>
    /// Resolves the display field name from layer field definitions.
    /// Prefers a field named "name", then falls back to the first string-type field, then objectIdField.
    /// </summary>
    private static string ResolveDisplayFieldFromLayer(LayerDefinition layer, string objectIdField)
    {
        var preferredNameField = layer.Fields.FirstOrDefault(
            field => field.Name.Equals("name", StringComparison.OrdinalIgnoreCase));
        if (preferredNameField != null)
        {
            return preferredNameField.Name;
        }

        var firstStringField = layer.Fields.FirstOrDefault(
            field => field.GeoServicesType.Equals("esriFieldTypeString", StringComparison.OrdinalIgnoreCase));
        return firstStringField?.Name ?? objectIdField;
    }

    private static string ResolveServiceObjectIdField(ServiceDefinition service)
    {
        var candidate = service.Layers
            .Select(layer => layer.PrimaryKeyField?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidate.Length == 1 ? candidate[0]! : FieldNames.ObjectId;
    }

    /// <summary>
    /// Maps layer definition to layer response
    /// </summary>
    private static LayerResponse MapLayerToResponse(
        ServiceDefinition service,
        LayerDefinition layer,
        QueryLimits queryLimits,
        FeatureServerTimeInfo? timeInfo,
        JsonElement? drawingInfo)
    {
        var objectIdField = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        var displayField = ResolveDisplayFieldFromLayer(layer, objectIdField);
        var supportsStatistics = true;
        var supportsAdvancedQueries = service.SupportsAdvancedQueries;
        var supportsRelated = layer.HasRelationships;

        return new LayerResponse
        {
            Id = layer.Id,
            Name = layer.Name,
            Description = layer.Description,
            Type = "Feature Layer",
            GeometryType = MapGeometryType(layer.GeometryType),
            SpatialReference = layer.SpatialReference.ToSpatialReferenceInfo(),
            Extent = layer.Extent.HasValue ? layer.Extent.Value.ToExtentInfo() : null,
            TimeInfo = timeInfo,
            Fields = [.. layer.Fields.Select(MapFieldInfo)],
            MaxRecordCount = queryLimits.MaxRecordCount,
            ObjectIdField = objectIdField,
            DisplayField = displayField,
            UniqueIdField = new UniqueIdFieldInfo { Name = objectIdField, IsSystemMaintained = true },
            DrawingInfo = drawingInfo.HasValue ? drawingInfo.Value : null,
            Capabilities = BuildLayerCapabilities(service, layer),
            SupportsAdvancedQueries = supportsAdvancedQueries,
            SupportsStatistics = supportsStatistics,
            SupportsCountDistinct = supportsStatistics,
            SupportsOrderBy = true,
            SupportsDistinct = true,
            SupportsPagination = true,
            SupportsTrueCurve = false,
            SupportsRollbackOnFailureParameter = service.SupportsEditing,
            SupportsApplyEditsWithGlobalIds = false,
            HasAttachments = layer.SupportsAttachments,
            SupportsQueryRelated = supportsRelated,
            SupportedQueryFormats = NormalizeSupportedQueryFormats(service.SupportedFormats),
            SupportsCoordinatesQuantization = false,
            Relationships = BuildRelationshipResponse(layer),
            AllowGeometryUpdates = service.SupportsEditing,
            EditFieldsInfo = null,
            EditingInfo = service.SupportsEditing ? new EditingInfo() : null,
            Templates = []
        };
    }

    private static async Task<FeatureServerTimeInfo?> BuildTimeInfoAsync(
        LayerDefinition layer,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        var temporalRange = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer,
            featureReader,
            cancellationToken).ConfigureAwait(false);
        if (temporalRange == null)
        {
            return null;
        }

        var range = temporalRange.Value;
        long? minMs = range.Min?.ToUnixTimeMilliseconds();
        long? maxMs = range.Max?.ToUnixTimeMilliseconds();

        return new FeatureServerTimeInfo
        {
            StartTimeField = range.StartField.Name,
            EndTimeField = range.EndField?.Name,
            TrackIdField = layer.Metadata?.TimeInfo?.TrackIdField,
            TimeExtent = new long?[] { minMs, maxMs }
        };
    }

    /// <summary>
    /// Maps layer definition to layer info
    /// </summary>
    private static LayerInfo MapLayerInfo(LayerDefinition layer)
    {
        return new LayerInfo
        {
            Id = layer.Id,
            Name = layer.Name,
            DefaultVisibility = layer.DefaultVisibility,
            SubLayerIds = null,
            MinScale = layer.MinScale,
            MaxScale = layer.MaxScale,
            GeometryType = MapGeometryType(layer.GeometryType)
        };
    }

    /// <summary>
    /// Maps field definition to field info
    /// </summary>
    private static GeoServicesFieldInfo MapFieldInfo(FieldDefinition field)
    {
        return new GeoServicesFieldInfo
        {
            Name = field.Name,
            Type = field.GeoServicesType,
            SqlType = field.SqlType,
            Alias = field.DisplayName,
            Length = field.Length,
            Nullable = field.Nullable,
            Editable = !field.IsGeometry,
            DefaultValue = field.DefaultValue
        };
    }

    /// <summary>
    /// Maps geometry type to string
    /// </summary>
    private static string MapGeometryType(GeometryType geometryType)
    {
        return geometryType switch
        {
            GeometryType.Point => "esriGeometryPoint",
            GeometryType.LineString => "esriGeometryPolyline",
            GeometryType.Polygon => "esriGeometryPolygon",
            GeometryType.MultiPoint => "esriGeometryMultipoint",
            GeometryType.MultiLineString => "esriGeometryPolyline",
            GeometryType.MultiPolygon => "esriGeometryPolygon",
            GeometryType.GeometryCollection => "esriGeometryNull",
            GeometryType.None => "esriGeometryNull",
            _ => "esriGeometryNull"
        };
    }

    /// <summary>
    /// Maps field type to FeatureServer field type
    /// </summary>
    /// <summary>
    /// Validates allowed parameters
    /// </summary>
    internal static bool TryValidateAllowedParameters(
        IQueryCollection query,
        ICommonQueryValidator queryValidator,
        FrozenSet<string> allowedParameters,
        out string? error)
    {
        error = QueryParameterValidationHelpers.GetValidationError(
            queryValidator,
            query.Keys.ToArray(),
            allowedParameters);
        return error == null;
    }

    internal static bool TryValidateAllowedParameters(
        IReadOnlyDictionary<string, StringValues> values,
        ICommonQueryValidator queryValidator,
        FrozenSet<string> allowedParameters,
        out string? error)
    {
        error = QueryParameterValidationHelpers.GetValidationError(
            queryValidator,
            values.Keys.ToArray(),
            allowedParameters);
        return error == null;
    }

    private static string[] NormalizeSupportedQueryFormats(string[]? formats)
    {
        if (formats == null || formats.Length == 0)
        {
            return ["JSON"];
        }

        return [.. formats.Select(static format => format.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string BuildServiceCapabilities(ServiceDefinition service)
    {
        var capabilities = new List<string>();
        if (service.Capabilities.Any(capability => capability.Equals("Query", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Query");
        }

        if (service.SupportsEditing)
        {
            capabilities.Add("Create");
            capabilities.Add("Update");
            capabilities.Add("Delete");
            capabilities.Add("Editing");
        }

        if (service.Capabilities.Any(capability => capability.Equals("Extract", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Extract");
        }

        if (service.Layers.Any(layer => layer.SupportsAttachments))
        {
            capabilities.Add("Uploads");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildLayerCapabilities(ServiceDefinition service, LayerDefinition layer)
    {
        var capabilities = new List<string>();
        if (service.Capabilities.Any(capability => capability.Equals("Query", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Query");
        }

        if (service.SupportsEditing)
        {
            capabilities.Add("Create");
            capabilities.Add("Update");
            capabilities.Add("Delete");
            capabilities.Add("Editing");
        }

        if (service.Capabilities.Any(capability => capability.Equals("Extract", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities.Add("Extract");
        }

        if (layer.SupportsAttachments)
        {
            capabilities.Add("Uploads");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static LayerRelationshipInfo[] BuildRelationshipResponse(LayerDefinition layer)
    {
        if (!layer.HasRelationships)
        {
            return [];
        }

        return
        [
            ..layer.LayerRelationships.Select(relationship => new LayerRelationshipInfo
            {
                Id = relationship.RelationshipId,
                Name = relationship.Name,
                RelatedTableId = relationship.RelatedLayerId,
                Role = relationship.RelationshipType,
                KeyField = relationship.DestinationForeignKeyField,
                OriginKeyField = relationship.OriginForeignKeyField,
                DestinationKeyField = relationship.DestinationForeignKeyField,
                Description = relationship.Description
            })
        ];
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
