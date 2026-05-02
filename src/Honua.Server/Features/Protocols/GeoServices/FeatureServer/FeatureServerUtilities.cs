// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Protocols.GeoServices;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

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
        public static readonly FrozenSet<string> ServiceMetadata =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> LayerMetadata =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            new[] { "where" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> H3Tiles =
            new[] { "where", "resolution" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> QueryH3 = new[]
            {
                "resolution",
                "where",
                "kRingDistance",
                "outStatistics",
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

    /// <summary>
    /// Maps service definition to FeatureServer response
    /// </summary>
    private static FeatureServerResponse MapServiceToResponse(
        ServiceDefinition service,
        QueryLimits queryLimits,
        bool supportsGeobufOutput,
        bool supportsAttachmentUploads)
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
            SupportedQueryFormats = NormalizeSupportedQueryFormats(service.SupportedFormats, supportsGeobufOutput),
            Capabilities = BuildServiceCapabilities(service, supportsAttachmentUploads),
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
        => GeoServicesObjectIdFieldResolver.ResolveServiceObjectIdFieldName(service);

    /// <summary>
    /// Maps layer definition to layer response
    /// </summary>
    private static LayerResponse MapLayerToResponse(
        ServiceDefinition service,
        LayerDefinition layer,
        QueryLimits queryLimits,
        FeatureServerTimeInfo? timeInfo,
        JsonElement? drawingInfo,
        FeatureServerExtrusionInfo? extrusionInfo,
        bool supportsGeobufOutput,
        bool supportsAttachmentUploads)
    {
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(layer);
        var displayField = ResolveDisplayFieldFromLayer(layer, objectIdField);
        var supportsStatistics = true;
        var supportsAdvancedQueries = service.SupportsAdvancedQueries;
        var supportsRelated = layer.HasRelationships;
        var supportsOrderBy = supportsAdvancedQueries;
        var supportsDistinct = supportsAdvancedQueries;
        var supportsPagination = supportsAdvancedQueries;
        var advancedQueryCapabilities = BuildAdvancedQueryCapabilities(
            supportsAdvancedQueries,
            supportsStatistics,
            supportsOrderBy,
            supportsDistinct,
            supportsPagination);

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
            ExtrusionInfo = extrusionInfo,
            Fields = [.. layer.Fields.Select(MapFieldInfo)],
            MaxRecordCount = queryLimits.MaxRecordCount,
            ObjectIdField = objectIdField,
            DisplayField = displayField,
            UniqueIdField = new UniqueIdFieldInfo { Name = objectIdField, IsSystemMaintained = true },
            DrawingInfo = drawingInfo.HasValue ? drawingInfo.Value : null,
            Capabilities = BuildLayerCapabilities(service, layer, supportsAttachmentUploads),
            SupportsAdvancedQueries = supportsAdvancedQueries,
            SupportsStatistics = supportsStatistics,
            SupportsCountDistinct = supportsStatistics,
            SupportsOrderBy = supportsOrderBy,
            SupportsDistinct = supportsDistinct,
            SupportsPagination = supportsPagination,
            SupportsTrueCurve = false,
            SupportsRollbackOnFailureParameter = service.SupportsEditing,
            SupportsApplyEditsWithGlobalIds = false,
            HasAttachments = layer.SupportsAttachments && supportsAttachmentUploads,
            SupportsQueryRelated = supportsRelated,
            SupportedQueryFormats = NormalizeSupportedQueryFormats(service.SupportedFormats, supportsGeobufOutput),
            SupportsCoordinatesQuantization = false,
            Relationships = BuildRelationshipResponse(layer),
            AllowGeometryUpdates = service.SupportsEditing,
            EditFieldsInfo = null,
            EditingInfo = service.SupportsEditing ? new EditingInfo() : null,
            Templates = [],
            AdvancedQueryCapabilities = advancedQueryCapabilities
        };
    }

    private static AdvancedQueryCapabilities BuildAdvancedQueryCapabilities(
        bool supportsAdvancedQueries,
        bool supportsStatistics,
        bool supportsOrderBy,
        bool supportsDistinct,
        bool supportsPagination)
    {
        return new AdvancedQueryCapabilities
        {
            UseStandardizedQueries = true,
            SupportsStatistics = supportsStatistics,
            SupportsOrderBy = supportsOrderBy,
            SupportsDistinct = supportsDistinct,
            SupportsCountDistinct = supportsStatistics,
            SupportsPagination = supportsPagination,
            SupportsReturningQueryExtent = supportsAdvancedQueries,
            SupportsQueryWithDistance = supportsAdvancedQueries,
            SupportsSqlExpression = supportsAdvancedQueries,
            SupportsBatchEditing = supportsAdvancedQueries
        };
    }

    private static FeatureServerExtrusionInfo? BuildExtrusionInfo(LayerDefinition layer)
    {
        if (layer.Metadata?.Extrusion is not { } extrusion)
        {
            return null;
        }

        return new FeatureServerExtrusionInfo
        {
            Enabled = true,
            HeightField = extrusion.HeightField,
            BaseHeightField = extrusion.BaseHeightField,
            Unit = MapVerticalUnitWire(extrusion.Unit),
            DefaultHeight = extrusion.DefaultHeight,
            MaterialHint = extrusion.MaterialHint
        };
    }

    private static string MapVerticalUnitWire(VerticalUnit unit) => unit switch
    {
        VerticalUnit.Meters => "meters",
        VerticalUnit.Feet => "feet",
        VerticalUnit.UsSurveyFeet => "usSurveyFeet",
        _ => "meters"
    };

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
            DefaultValue = field.DefaultValue,
            Domain = MapFieldDomainInfo(field.Domain, field.GeoServicesType)
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

    private static string BuildServiceCapabilities(ServiceDefinition service, bool supportsAttachmentUploads)
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

        if (supportsAttachmentUploads && service.Layers.Any(layer => layer.SupportsAttachments))
        {
            capabilities.Add("Uploads");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildLayerCapabilities(ServiceDefinition service, LayerDefinition layer, bool supportsAttachmentUploads)
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

        if (supportsAttachmentUploads && layer.SupportsAttachments)
        {
            capabilities.Add("Uploads");
        }

        return string.Join(',', capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal static bool HasAttachmentSurface(IServiceProvider services)
        => services.GetService<IAttachmentStore>() != null;

    internal static bool TryResolveRequestedServiceLayers(
        ServiceDefinition service,
        IReadOnlyDictionary<string, StringValues> values,
        out LayerDefinition[] layers,
        out bool selectorSpecified,
        out string? error)
    {
        layers = service.Layers;
        selectorSpecified = false;
        error = null;

        HashSet<int>? requestedLayerIds = null;

        if (TryGetValue(values, "layerId", out var layerIdRaw) && !StringValues.IsNullOrEmpty(layerIdRaw))
        {
            if (!int.TryParse(layerIdRaw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                error = "layerId must be an integer.";
                return false;
            }

            requestedLayerIds ??= [];
            requestedLayerIds.Add(layerId);
        }

        if (TryGetValue(values, "layers", out var layersRaw) && !StringValues.IsNullOrEmpty(layersRaw))
        {
            if (!TryParseLayerIdList(layersRaw.ToString(), out var parsedLayerIds, out error))
            {
                return false;
            }

            requestedLayerIds ??= [];
            foreach (var layerId in parsedLayerIds)
            {
                requestedLayerIds.Add(layerId);
            }
        }

        if (requestedLayerIds is null or { Count: 0 })
        {
            return true;
        }

        selectorSpecified = true;
        layers = [.. service.Layers.Where(layer => requestedLayerIds.Contains(layer.Id))];
        if (layers.Length != requestedLayerIds.Count)
        {
            error = "layers must reference valid layer identifiers in the service.";
            return false;
        }

        return true;
    }

    internal static LayerDefinition[] FilterAccessibleLayers(
        HttpContext context,
        ServiceDefinition service,
        IEnumerable<LayerDefinition> layers)
        => [.. layers.Where(layer => AccessPolicyHelpers.IsLayerAccessible(context, layer, service))];

    internal static DomainInfo? MapQueryDomainInfo(LayerDefinition layer, FieldDefinition field)
    {
        if (field.Domain == null)
        {
            return null;
        }

        return new DomainInfo
        {
            Type = field.Domain.Type,
            Name = field.Domain.Name,
            FieldName = field.Name,
            LayerId = layer.Id,
            CodedValues = field.Domain.CodedValues?
                .Select(static codedValue => new DomainCodedValueInfo
                {
                    Name = codedValue.Name,
                    Code = Convert.ToString(codedValue.Code, CultureInfo.InvariantCulture) ?? string.Empty
                })
                .ToArray()
        };
    }

    internal static object? MapFieldDomainInfo(FieldDomainDefinition? domain, string fieldType)
    {
        if (domain == null)
        {
            return null;
        }

        var fieldDomain = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = domain.Type,
            ["name"] = domain.Name,
            ["fieldType"] = fieldType
        };

        if (domain.CodedValues is { Length: > 0 })
        {
            fieldDomain["codedValues"] = domain.CodedValues
                .Select(static codedValue => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = codedValue.Name,
                    ["code"] = codedValue.Code
                })
                .ToArray();
        }

        if (domain.Range != null)
        {
            fieldDomain["range"] = new object[] { domain.Range.MinValue, domain.Range.MaxValue };
        }

        if (!string.IsNullOrWhiteSpace(domain.MergePolicy))
        {
            fieldDomain["mergePolicy"] = domain.MergePolicy;
        }

        if (!string.IsNullOrWhiteSpace(domain.SplitPolicy))
        {
            fieldDomain["splitPolicy"] = domain.SplitPolicy;
        }

        return fieldDomain;
    }

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

    internal static bool TryParseLayerIdList(string rawValue, out int[] layerIds, out string? error)
    {
        layerIds = [];
        error = null;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            error = "layers parameter must contain at least one layer ID.";
            return false;
        }

        var normalized = rawValue.Trim();
        if (normalized.StartsWith('[') &&
            normalized.EndsWith(']') &&
            normalized.Length >= 2)
        {
            normalized = normalized[1..^1];
        }

        var tokens = normalized.Split(',', StringSplitOptions.TrimEntries);
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
