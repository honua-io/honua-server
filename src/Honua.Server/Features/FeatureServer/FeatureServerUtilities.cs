// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
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
                "time",
                "timeRelation"
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
        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var timeout = limits.Query.QueryTimeout;
        var timeoutCts = new CancellationTokenSource(timeout);
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, timeoutCts.Token);
        return combinedCts.Token;
    }

    /// <summary>
    /// Maps service definition to FeatureServer response
    /// </summary>
    private static FeatureServerResponse MapServiceToResponse(ServiceDefinition service, QueryLimits queryLimits)
    {
        return new FeatureServerResponse
        {
            ServiceName = service.Name,
            ServiceDescription = service.Description,
            Layers = [.. service.Layers.Select(MapLayerInfo)],
            SpatialReference = service.SpatialReference.ToSpatialReferenceInfo(),
            InitialExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            FullExtent = service.EffectiveExtent.HasValue ? MapExtent(service.EffectiveExtent.Value) : null,
            MaxRecordCount = queryLimits.MaxRecordCount,
            SupportedQueryFormats = service.SupportedFormats,
            Capabilities = string.Join(",", service.Capabilities),
            Fields = [.. service.AllFields.Select(MapFieldInfo)]
        };
    }

    /// <summary>
    /// Maps layer definition to layer response
    /// </summary>
    private static LayerResponse MapLayerToResponse(LayerDefinition layer, QueryLimits queryLimits)
    {
        var objectIdField = layer.PrimaryKeyField?.Name ?? "objectid";

        return new LayerResponse
        {
            Id = layer.Id,
            Name = layer.Name,
            Description = layer.Description,
            Type = "Feature Layer",
            GeometryType = MapGeometryType(layer.GeometryType),
            SpatialReference = layer.SpatialReference.ToSpatialReferenceInfo(),
            Extent = layer.Extent.HasValue ? MapExtent(layer.Extent.Value) : null,
            Fields = [.. layer.Fields.Select(MapFieldInfo)],
            MaxRecordCount = queryLimits.MaxRecordCount,
            ObjectIdField = objectIdField
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
    /// Maps extent to FeatureServer extent format
    /// </summary>
    private static ExtentInfo MapExtent(FeatureExtent extent)
    {
        return new ExtentInfo
        {
            Xmin = extent.MinX,
            Ymin = extent.MinY,
            Xmax = extent.MaxX,
            Ymax = extent.MaxY,
            SpatialReference = new SpatialReferenceInfo { Wkid = extent.SpatialReference }
        };
    }

    /// <summary>
    /// Maps spatial reference to FeatureServer format
    /// </summary>
    private static SpatialReferenceInfo MapSpatialReference(SpatialReference spatialReference)
        => spatialReference.ToSpatialReferenceInfo();

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
            GeometryType.GeometryCollection => "esriGeometryPolygon",
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
        var validationResult = queryValidator.ValidateAllowedParameters(query.Keys.ToArray(), allowedParameters);
        if (!validationResult.IsValid)
        {
            error = validationResult.ErrorMessage;
            return false;
        }

        error = null;
        return true;
    }
}
