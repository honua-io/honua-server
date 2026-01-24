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

        public static readonly FrozenSet<string> GenerateRenderer = new[]
            {
                "classificationDef",
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> ApplyEdits =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
                "f"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Tiles =
            new[] { "where" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets timeout-aware cancellation token
    /// </summary>
    private static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
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

        return new FeatureServerResponse
        {
            ServiceName = service.Name,
            ServiceDescription = service.Description,
            Layers = [.. service.Layers.Select(MapLayerInfo)],
            SpatialReference = service.SpatialReference.ToSpatialReferenceInfo(),
            InitialExtent = service.EffectiveExtent.HasValue ? service.EffectiveExtent.Value.ToExtentInfo() : null,
            FullExtent = service.EffectiveExtent.HasValue ? service.EffectiveExtent.Value.ToExtentInfo() : null,
            MaxRecordCount = queryLimits.MaxRecordCount,
            SupportedQueryFormats = service.SupportedFormats,
            Capabilities = string.Join(",", service.Capabilities),
            Fields = [.. service.AllFields.Select(MapFieldInfo)],
            ObjectIdField = objectIdField
        };
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
        LayerDefinition layer,
        QueryLimits queryLimits,
        FeatureServerTimeInfo? timeInfo,
        JsonElement? drawingInfo)
    {
        var objectIdField = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;

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
            DrawingInfo = drawingInfo.HasValue ? drawingInfo.Value : null
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
    private static bool TryValidateAllowedParameters(
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
}
