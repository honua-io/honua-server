// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Honua.Server.Features.Wfs20.Models;
using Honua.ServiceDefaults;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Core handler for WFS 2.0 operations backed by the shared catalog and feature stores.
/// </summary>
internal sealed class Wfs20Handler
{
    private const string FeatureNamespacePrefix = "honua";
    private const string FeatureNamespaceUri = "http://honua.io/wfs";
    private static readonly WKBWriter BboxWkbWriter = new();

    private readonly ILogger<Wfs20Handler> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureReader _featureReader;
    private readonly IGmlFeatureStore _gmlFeatureStore;
    private readonly IFilterExpressionService _filterExpressionService;
    private readonly OgcFeaturesGeometryServices _geometryServices;
    private readonly Wfs20Options _wfs20Options;

    public Wfs20Handler(
        ILogger<Wfs20Handler> logger,
        Wfs20QueryServices queryServices)
    {
        _logger = logger;
        _layerCatalog = queryServices.LayerCatalog;
        _featureReader = queryServices.FeatureReader;
        _gmlFeatureStore = queryServices.GmlFeatureStore;
        _filterExpressionService = queryServices.FilterExpressionService;
        _geometryServices = queryServices.GeometryServices;
        _wfs20Options = queryServices.Wfs20Options;
    }

    public async Task<WfsCapabilities> HandleGetCapabilitiesAsync(
        HttpContext context,
        string? acceptVersions,
        string? sections,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_capabilities", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetCapabilities);

        Wfs20Log.GetCapabilitiesRequested(_logger);

        try
        {
            var featureTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var wfsUrl = $"{baseUrl}/wfs";

            var capabilities = new WfsCapabilities
            {
                ServiceIdentification = new ServiceIdentification(),
                ServiceProvider = new Models.ServiceProvider(),
                OperationsMetadata = BuildOperationsMetadata(wfsUrl),
                FeatureTypeList = new FeatureTypeList
                {
                    FeatureTypes = featureTypes.Select(BuildFeatureType).ToArray()
                },
                FilterCapabilities = BuildFilterCapabilities()
            };

            Wfs20Log.GetCapabilitiesReturned(_logger);
            return capabilities;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }

    public async Task<string> HandleDescribeFeatureTypeAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.describe_feature_type", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.DescribeFeatureType);
        activity?.SetTag("wfs.type_names", typeNames ?? "ALL");

        Wfs20Log.DescribeFeatureTypeRequested(_logger, typeNames ?? "ALL");

        try
        {
            var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);

            var schema = selectedTypes.Length == 0 && requestedTypes.Length > 0
                ? GenerateEmptySchemaForTypes(requestedTypes)
                : GenerateSchemaForTypes(selectedTypes);

            Wfs20Log.DescribeFeatureTypeReturned(_logger, selectedTypes.Length == 0 ? requestedTypes.Length : selectedTypes.Length);
            return schema;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }

    public async Task<IResult> HandleGetFeatureAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        string? count,
        string? startIndex,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_feature", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetFeature);
        activity?.SetTag("wfs.type_names", typeNames ?? "ALL");
        activity?.SetTag("wfs.output_format", outputFormat ?? Wfs20Utilities.OutputFormats.Default);

        var normalizedFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        var maxFeatures = Wfs20Utilities.ParseCount(count);
        var offset = Wfs20Utilities.ParseStartIndex(startIndex);
        var isHitsRequest = string.Equals(resultType, "hits", StringComparison.OrdinalIgnoreCase);

        Wfs20Log.GetFeatureRequested(_logger, typeNames ?? "ALL", normalizedFormat);

        try
        {
            if (!IsSupportedFeatureOutputFormat(normalizedFormat))
            {
                Wfs20Log.UnsupportedOutputFormatRequested(_logger, outputFormat ?? normalizedFormat);
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Unsupported output format '{outputFormat}'. Supported formats: {string.Join(", ", Wfs20Utilities.OutputFormats.All)}");
            }

            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (selectedTypes.Length == 0)
            {
                return isHitsRequest
                    ? CreateHitsFeatureCollectionResult(0)
                    : CreateEmptyFeatureCollectionResult(normalizedFormat);
            }

            if (ShouldUsePagedGetFeatureFastPath(selectedTypes, normalizedFormat, isHitsRequest, maxFeatures))
            {
                var descriptor = selectedTypes[0];
                var query = BuildFeatureQuery(
                    descriptor.Layer,
                    propertyName,
                    sortBy,
                    bbox,
                    filter,
                    resourceId,
                    srsName) with
                {
                    Offset = offset,
                    Limit = maxFeatures
                };

                var pagedResult = await BuildPagedGetFeatureResultAsync(
                    descriptor,
                    query,
                    normalizedFormat,
                    cancellationToken);

                Wfs20Log.GetFeatureReturned(
                    _logger,
                    pagedResult.ReturnedCount,
                    pagedResult.NumberMatchedSummary);

                return pagedResult.Result;
            }

            var planSet = await BuildLayerQueryPlansAsync(
                selectedTypes,
                propertyName,
                sortBy,
                bbox,
                filter,
                resourceId,
                srsName,
                offset,
                maxFeatures,
                cancellationToken);

            if (planSet.TotalMatched == 0)
            {
                return isHitsRequest
                    ? CreateHitsFeatureCollectionResult(0)
                    : CreateEmptyFeatureCollectionResult(normalizedFormat);
            }

            if (isHitsRequest)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var matchedSummary = planSet.TotalMatched.ToString(CultureInfo.InvariantCulture);
                    Wfs20Log.GetFeatureReturned(_logger, 0, matchedSummary);
                }
                return CreateHitsFeatureCollectionResult(planSet.TotalMatched);
            }

            var (result, returnedCount) = normalizedFormat switch
            {
                Wfs20Utilities.OutputFormats.Csv => await BuildCsvResultAsync(planSet, cancellationToken),
                Wfs20Utilities.OutputFormats.GeoJson or Wfs20Utilities.OutputFormats.Json => await BuildJsonResultAsync(planSet, normalizedFormat, cancellationToken),
                _ => await BuildGmlResultAsync(planSet, cancellationToken)
            };

            if (_logger.IsEnabled(LogLevel.Information))
            {
                var matchedSummary = planSet.TotalMatched.ToString(CultureInfo.InvariantCulture);
                Wfs20Log.GetFeatureReturned(
                    _logger,
                    returnedCount,
                    matchedSummary);
            }

            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Fes20ParseException)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.GetFeature, ex.Message);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process GetFeature request.");
        }
    }

    public async Task<IResult> HandleGetPropertyValueAsync(
        HttpContext context,
        string? typeNames,
        string? valueReference,
        string? outputFormat,
        string? count,
        string? startIndex,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_property_value", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.GetPropertyValue);
        activity?.SetTag("wfs.type_names", typeNames ?? "ALL");
        activity?.SetTag("wfs.value_reference", valueReference ?? "unknown");

        if (string.IsNullOrWhiteSpace(valueReference))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Missing required 'valueReference' parameter for GetPropertyValue.");
        }

        var normalizedOutputFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        if (!string.Equals(normalizedOutputFormat, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported output format '{outputFormat}'. GetPropertyValue only supports GML/XML responses.");
        }

        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        var maxFeatures = Wfs20Utilities.ParseCount(count);
        var offset = Wfs20Utilities.ParseStartIndex(startIndex);

        Wfs20Log.GetPropertyValueRequested(_logger, valueReference, typeNames ?? "ALL");

        try
        {
            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (selectedTypes.Length == 0)
            {
                return CreateEmptyValueCollectionResult();
            }

            var planSet = await BuildLayerValuePlansAsync(
                selectedTypes,
                valueReference,
                bbox,
                filter,
                resourceId,
                srsName,
                offset,
                maxFeatures,
                cancellationToken);

            if (planSet.TotalMatched == 0)
            {
                return CreateEmptyValueCollectionResult();
            }

            var (result, returnedCount) = await BuildValueCollectionResultAsync(planSet, cancellationToken);
            Wfs20Log.GetPropertyValueReturned(_logger, returnedCount);
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Fes20ParseException)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.GetPropertyValue, ex.Message);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process GetPropertyValue request.");
        }
    }

    private async Task<ImmutableArray<WfsFeatureTypeDescriptor>> GetPublishedFeatureTypesAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var layers = await _layerCatalog.ListLayersAsync(cancellationToken);
        var services = await _layerCatalog.ListServicesAsync(cancellationToken);
        var visibleLayers = layers
            .Where(layer => IsPublishedForWfs(context, layer, services))
            .OrderBy(layer => layer.Id)
            .ToArray();

        var descriptors = ImmutableArray.CreateBuilder<WfsFeatureTypeDescriptor>(visibleLayers.Length);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in visibleLayers)
        {
            var baseName = BuildTypeLocalName(layer);
            var localName = baseName;
            if (!usedNames.Add(localName))
            {
                localName = $"{baseName}_{layer.Id}";
                usedNames.Add(localName);
            }

            descriptors.Add(new WfsFeatureTypeDescriptor(
                layer,
                $"{FeatureNamespacePrefix}:{localName}",
                localName));
        }

        return descriptors.ToImmutable();
    }

    private async Task<LayerQueryPlanSet> BuildLayerQueryPlansAsync(
        IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes,
        string? propertyName,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var plans = ImmutableArray.CreateBuilder<LayerQueryPlan>(featureTypes.Count);
        var remainingOffset = offset;
        var remainingCount = count;
        long totalMatched = 0;

        foreach (var featureType in featureTypes)
        {
            var query = BuildFeatureQuery(
                featureType.Layer,
                propertyName,
                sortBy,
                bbox,
                filter,
                resourceId,
                srsName);

            var layerMatched = await _featureReader.CountAsync(featureType.Layer.Id, query, cancellationToken);
            totalMatched += layerMatched;

            if (layerMatched == 0)
            {
                continue;
            }

            if (remainingOffset >= layerMatched)
            {
                remainingOffset -= (int)Math.Min(remainingOffset, layerMatched);
                continue;
            }

            if (remainingCount <= 0)
            {
                continue;
            }

            var layerOffset = remainingOffset;
            remainingOffset = 0;
            var availableCount = layerMatched - layerOffset;
            var layerLimit = (int)Math.Min(remainingCount, Math.Min(availableCount, int.MaxValue));

            if (layerLimit <= 0)
            {
                continue;
            }

            plans.Add(new LayerQueryPlan(
                featureType,
                query with { Offset = layerOffset, Limit = layerLimit },
                layerMatched));

            remainingCount -= layerLimit;
        }

        return new LayerQueryPlanSet(plans.ToImmutable(), totalMatched);
    }

    private async Task<LayerValuePlanSet> BuildLayerValuePlansAsync(
        IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes,
        string valueReference,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var plans = ImmutableArray.CreateBuilder<LayerValuePlan>(featureTypes.Count);
        var remainingOffset = offset;
        var remainingCount = count;
        long totalMatched = 0;

        foreach (var featureType in featureTypes)
        {
            var resolvedValueReference = ResolveValueReference(featureType.Layer, valueReference);
            var query = BuildValueQuery(
                featureType.Layer,
                resolvedValueReference,
                bbox,
                filter,
                resourceId,
                srsName);

            var layerMatched = await _featureReader.CountAsync(featureType.Layer.Id, query, cancellationToken);
            totalMatched += layerMatched;

            if (layerMatched == 0)
            {
                continue;
            }

            if (remainingOffset >= layerMatched)
            {
                remainingOffset -= (int)Math.Min(remainingOffset, layerMatched);
                continue;
            }

            if (remainingCount <= 0)
            {
                continue;
            }

            var layerOffset = remainingOffset;
            remainingOffset = 0;
            var availableCount = layerMatched - layerOffset;
            var layerLimit = (int)Math.Min(remainingCount, Math.Min(availableCount, int.MaxValue));

            if (layerLimit <= 0)
            {
                continue;
            }

            plans.Add(new LayerValuePlan(
                featureType,
                query with { Offset = layerOffset, Limit = layerLimit },
                layerMatched,
                resolvedValueReference));

            remainingCount -= layerLimit;
        }

        return new LayerValuePlanSet(plans.ToImmutable(), totalMatched);
    }

    private FeatureQuery BuildFeatureQuery(
        LayerDefinition layer,
        string? propertyName,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName)
    {
        var projectedFields = ResolveProjectedFields(layer, propertyName);
        var sqlFilter = TranslateFesFilter(layer, filter);
        var spatialFilter = ParseBboxFilter(bbox, layer);
        var objectIds = ParseResourceIds(resourceId);
        var orderBy = ParseSortBy(layer, sortBy);
        var outputSrid = ParseSrid(srsName) ?? layer.SpatialReference.ToSrid();

        return new FeatureQuery
        {
            SqlFilter = sqlFilter,
            ObjectIds = objectIds,
            OutFields = projectedFields,
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OutputSrid = outputSrid,
            OrderBy = orderBy
        };
    }

    private FeatureQuery BuildValueQuery(
        LayerDefinition layer,
        ValueReferenceResolution valueReference,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName)
    {
        ImmutableArray<string>? outFields = valueReference.IsGeometry || valueReference.IsFeatureId
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(valueReference.CanonicalName);

        var sqlFilter = TranslateFesFilter(layer, filter);
        var spatialFilter = ParseBboxFilter(bbox, layer);
        var objectIds = ParseResourceIds(resourceId);
        var outputSrid = ParseSrid(srsName) ?? layer.SpatialReference.ToSrid();

        return new FeatureQuery
        {
            SqlFilter = sqlFilter,
            ObjectIds = objectIds,
            OutFields = outFields,
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OutputSrid = outputSrid
        };
    }

    private SqlFragment? TranslateFesFilter(LayerDefinition layer, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var expression = Fes20Parser.ParseFilter(filter);
        expression = NormalizeFilterPropertyReferences(expression, layer);

        var translation = _filterExpressionService.Translate(expression, layer);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid filter expression.");
        }

        return translation.SqlFilter;
    }

    private static FilterExpression NormalizeFilterPropertyReferences(FilterExpression expression, LayerDefinition layer)
    {
        return expression switch
        {
            PropertyReference property => new PropertyReference(
                ResolveFieldName(layer, property.PropertyName, allowGeometryAlias: true) ??
                NormalizeIdentifier(property.PropertyName)),
            BinaryExpression binary => new BinaryExpression(
                NormalizeFilterPropertyReferences(binary.Left, layer),
                binary.Operator,
                NormalizeFilterPropertyReferences(binary.Right, layer)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                NormalizeFilterPropertyReferences(unary.Operand, layer)),
            SpatialPredicate spatial => new SpatialPredicate(
                spatial.Operator,
                NormalizeFilterPropertyReferences(spatial.Left, layer),
                NormalizeFilterPropertyReferences(spatial.Right, layer)),
            SpatialDistancePredicate distance => new SpatialDistancePredicate(
                distance.Operator,
                NormalizeFilterPropertyReferences(distance.Left, layer),
                NormalizeFilterPropertyReferences(distance.Right, layer),
                NormalizeFilterPropertyReferences(distance.Distance, layer)),
            TemporalPredicate temporal => new TemporalPredicate(
                temporal.Operator,
                NormalizeFilterPropertyReferences(temporal.Left, layer),
                NormalizeFilterPropertyReferences(temporal.Right, layer)),
            ArrayPredicate array => new ArrayPredicate(
                array.Operator,
                NormalizeFilterPropertyReferences(array.Left, layer),
                NormalizeFilterPropertyReferences(array.Right, layer)),
            FunctionCall function => new FunctionCall(
                function.FunctionName,
                function.Arguments.Select(argument => NormalizeFilterPropertyReferences(argument, layer)).ToArray()),
            ArrayLiteral arrayLiteral => new ArrayLiteral(
                arrayLiteral.Elements.Select(element => NormalizeFilterPropertyReferences(element, layer)).ToArray()),
            ValueList valueList => new ValueList(
                valueList.Values.Select(value => NormalizeFilterPropertyReferences(value, layer)).ToArray()),
            _ => expression
        };
    }

    private static ImmutableArray<string>? ResolveProjectedFields(LayerDefinition layer, string? propertyName)
    {
        var requestedProperties = ParseQualifiedList(propertyName);
        if (requestedProperties.Length == 0)
        {
            return null;
        }

        var resolved = ImmutableArray.CreateBuilder<string>();
        foreach (var requestedProperty in requestedProperties)
        {
            var fieldName = ResolveFieldName(layer, requestedProperty, allowGeometryAlias: true)
                ?? throw new ArgumentException($"Unknown property '{requestedProperty}' for feature type '{layer.Name}'.");

            if (layer.GeometryField != null &&
                fieldName.Equals(layer.GeometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!resolved.Any(existing => existing.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
            {
                resolved.Add(fieldName);
            }
        }

        return resolved.ToImmutable();
    }

    private static ImmutableArray<OrderByClause>? ParseSortBy(LayerDefinition layer, string? sortBy)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return null;
        }

        var clauses = ImmutableArray.CreateBuilder<OrderByClause>();

        foreach (var rawClause in sortBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = rawClause.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var fieldName = ResolveFieldName(layer, tokens[0], allowGeometryAlias: false)
                ?? throw new ArgumentException($"Unknown sort field '{tokens[0]}' for feature type '{layer.Name}'.");

            var fieldDefinition = layer.Fields.FirstOrDefault(field =>
                field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

            var ascending = true;
            if (tokens.Length > 1)
            {
                ascending = tokens[1].ToUpperInvariant() switch
                {
                    "A" or "ASC" => true,
                    "D" or "DESC" => false,
                    _ => throw new ArgumentException($"Unsupported sort direction '{tokens[1]}' in sortBy parameter.")
                };
            }

            clauses.Add(new OrderByClause(fieldName, ascending, fieldDefinition?.Type));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutable();
    }

    private static SpatialFilter? ParseBboxFilter(string? bbox, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return null;
        }

        var parts = bbox.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not 4 and not 5)
        {
            throw new ArgumentException("BBOX must contain 4 coordinates and an optional CRS identifier.");
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
        {
            throw new ArgumentException("BBOX contains invalid numeric coordinates.");
        }

        var srid = parts.Length == 5
            ? ParseSrid(parts[4]) ?? throw new ArgumentException($"Unsupported BBOX CRS '{parts[4]}'.")
            : layer.SpatialReference.ToSrid();

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
        var polygon = geometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        ]);

        return new SpatialFilter
        {
            Geometry = BboxWkbWriter.Write(polygon),
            Srid = srid,
            SpatialRelationship = SpatialRelationship.Intersects
        };
    }

    private static ImmutableArray<long>? ParseResourceIds(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var ids = ImmutableArray.CreateBuilder<long>();
        foreach (var rawResourceId in resourceId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = rawResourceId;
            var lastDot = candidate.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < candidate.Length - 1)
            {
                candidate = candidate[(lastDot + 1)..];
            }

            if (!long.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new ArgumentException($"Invalid resourceId '{rawResourceId}'. Expected numeric feature identifiers or qualified ids like 'type.123'.");
            }

            ids.Add(parsed);
        }

        return ids.ToImmutable();
    }

    private static int? ParseSrid(string? srsName)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return null;
        }

        if (srsName.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(srsName[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epsg))
        {
            return epsg;
        }

        if (srsName.StartsWith("urn:ogc:def:crs:EPSG::", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(srsName["urn:ogc:def:crs:EPSG::".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var urn))
        {
            return urn;
        }

        var lastSlash = srsName.LastIndexOf('/');
        if (lastSlash >= 0 &&
            int.TryParse(srsName[(lastSlash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uri))
        {
            return uri;
        }

        return null;
    }

    private static ImmutableArray<WfsFeatureTypeDescriptor> ResolveRequestedFeatureTypes(
        ImmutableArray<WfsFeatureTypeDescriptor> publishedTypes,
        string[] requestedTypes)
    {
        if (requestedTypes.Length == 0)
        {
            return publishedTypes;
        }

        var matches = ImmutableArray.CreateBuilder<WfsFeatureTypeDescriptor>();
        var seenLayerIds = new HashSet<int>();

        foreach (var requestedType in requestedTypes)
        {
            foreach (var featureType in publishedTypes)
            {
                if (!MatchesRequestedType(featureType, requestedType) ||
                    !seenLayerIds.Add(featureType.Layer.Id))
                {
                    continue;
                }

                matches.Add(featureType);
            }
        }

        return matches.ToImmutable();
    }

    private static bool MatchesRequestedType(WfsFeatureTypeDescriptor featureType, string requestedType)
    {
        var normalizedRequested = NormalizeIdentifier(requestedType);
        return string.Equals(requestedType, featureType.QualifiedName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.LocalName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.Layer.Name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRequested, featureType.Layer.Id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublishedForWfs(HttpContext context, LayerDefinition layer, ServiceDefinition[] services)
    {
        if (!ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.Wfs20))
        {
            return false;
        }

        var relatedServices = services
            .Where(service => service.Layers.Any(candidate => candidate.Id == layer.Id))
            .ToArray();

        if (relatedServices.Length == 0)
        {
            return AccessPolicyHelpers.IsLayerAccessible(context, layer);
        }

        return relatedServices
            .Where(service => ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Wfs20))
            .Any(service => AccessPolicyHelpers.IsLayerAccessible(context, layer, service));
    }

    private static FeatureType BuildFeatureType(WfsFeatureTypeDescriptor featureType)
    {
        var layer = featureType.Layer;

        return new FeatureType
        {
            Name = featureType.QualifiedName,
            Title = layer.Name,
            Abstract = layer.Description,
            Keywords = BuildKeywords(layer),
            DefaultCRS = FormatCrs(layer.SpatialReference.ToSrid()),
            OutputFormats = new OutputFormats
            {
                Formats = Wfs20Utilities.OutputFormats.All.ToArray()
            },
            WGS84BoundingBox = BuildWgs84BoundingBox(layer)
        };
    }

    private static string[] BuildKeywords(LayerDefinition layer)
    {
        var keywords = new List<string>
        {
            "wfs",
            "feature"
        };

        if (!string.IsNullOrWhiteSpace(layer.Name))
        {
            keywords.AddRange(layer.Name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.ToLowerInvariant()));
        }

        if (layer.HasGeometry)
        {
            keywords.Add(layer.GeometryType.ToString().ToLowerInvariant());
        }

        return keywords
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static WGS84BoundingBox? BuildWgs84BoundingBox(LayerDefinition layer)
    {
        if (layer.Extent == null)
        {
            return null;
        }

        var extent = layer.Extent.Value;
        if (!OgcExtentTransformer.TryTransformToCrs84(extent.MinX, extent.MinY, extent.SpatialReference, out var min) ||
            !OgcExtentTransformer.TryTransformToCrs84(extent.MaxX, extent.MaxY, extent.SpatialReference, out var max))
        {
            return null;
        }

        return new WGS84BoundingBox
        {
            LowerCorner = $"{FormatCoordinate(min.Lon)} {FormatCoordinate(min.Lat)}",
            UpperCorner = $"{FormatCoordinate(max.Lon)} {FormatCoordinate(max.Lat)}"
        };
    }

    private static string FormatCoordinate(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    private static string FormatCrs(int srid) => $"urn:ogc:def:crs:EPSG::{srid}";

    private static OperationsMetadata BuildOperationsMetadata(string wfsUrl)
    {
        return new OperationsMetadata
        {
            Operations =
            [
                CreateOperation(Wfs20Utilities.Operations.GetCapabilities, wfsUrl,
                [
                    CreateParameter("AcceptVersions", Wfs20Utilities.Version),
                    CreateParameter("Sections", "ServiceIdentification", "ServiceProvider", "OperationsMetadata", "FeatureTypeList", "Filter_Capabilities"),
                    CreateParameter("AcceptFormats", "application/xml")
                ]),
                CreateOperation(Wfs20Utilities.Operations.DescribeFeatureType, wfsUrl,
                [
                    CreateParameter("outputFormat", "application/xml"),
                    CreateParameter("typeNames", allowAnyValue: true)
                ]),
                CreateOperation(Wfs20Utilities.Operations.GetFeature, wfsUrl,
                [
                    CreateParameter("outputFormat", Wfs20Utilities.OutputFormats.All.ToArray()),
                    CreateParameter("typeNames", allowAnyValue: true),
                    CreateParameter("count", allowAnyValue: true),
                    CreateParameter("startIndex", allowAnyValue: true),
                    CreateParameter("sortBy", allowAnyValue: true),
                    CreateParameter("filter", allowAnyValue: true),
                    CreateParameter("bbox", allowAnyValue: true),
                    CreateParameter("resourceId", allowAnyValue: true),
                    CreateParameter("propertyName", allowAnyValue: true),
                    CreateParameter("srsName", allowAnyValue: true)
                ]),
                CreateOperation(Wfs20Utilities.Operations.GetPropertyValue, wfsUrl,
                [
                    CreateParameter("outputFormat", Wfs20Utilities.OutputFormats.Gml32),
                    CreateParameter("typeNames", allowAnyValue: true),
                    CreateParameter("valueReference", allowAnyValue: true),
                    CreateParameter("count", allowAnyValue: true),
                    CreateParameter("startIndex", allowAnyValue: true),
                    CreateParameter("filter", allowAnyValue: true),
                    CreateParameter("bbox", allowAnyValue: true),
                    CreateParameter("resourceId", allowAnyValue: true),
                    CreateParameter("srsName", allowAnyValue: true)
                ])
            ],
            Parameters =
            [
                CreateParameter("version", Wfs20Utilities.Version),
                CreateParameter("service", Wfs20Utilities.ServiceType)
            ],
            Constraints =
            [
                new Constraint
                {
                    Name = "DefaultMaxFeatures",
                    DefaultValue = Wfs20Utilities.DefaultMaxFeatures.ToString(CultureInfo.InvariantCulture)
                },
                new Constraint
                {
                    Name = "CountDefault",
                    DefaultValue = Wfs20Utilities.DefaultMaxFeatures.ToString(CultureInfo.InvariantCulture)
                }
            ]
        };
    }

    private static Operation CreateOperation(string name, string url, Parameter[]? parameters = null)
    {
        return new Operation
        {
            Name = name,
            DCP =
            [
                new DCP
                {
                    Http = new Http
                    {
                        Get = [new Models.HttpMethod { Href = url }],
                        Post = [new Models.HttpMethod { Href = url }]
                    }
                }
            ],
            Parameters = parameters
        };
    }

    private static Parameter CreateParameter(string name, params string[] allowedValues)
    {
        return new Parameter
        {
            Name = name,
            AllowedValues = allowedValues.Length > 0 ? new AllowedValues { Values = allowedValues } : null,
            AnyValue = allowedValues.Length == 0 ? new object() : null
        };
    }

    private static Parameter CreateParameter(string name, bool allowAnyValue)
    {
        return new Parameter
        {
            Name = name,
            AnyValue = allowAnyValue ? new object() : null
        };
    }

    private static FilterCapabilities BuildFilterCapabilities()
    {
        return new FilterCapabilities
        {
            Conformance = new FesConformance
            {
                Constraints =
                [
                    new FesConstraint { Name = "ImplementsQuery", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsAdHocQuery", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsResourceId", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsMinStandardFilter", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsStandardFilter", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsMinSpatialFilter", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsSpatialFilter", DefaultValue = "FALSE" },
                    new FesConstraint { Name = "ImplementsMinTemporalFilter", DefaultValue = "FALSE" },
                    new FesConstraint { Name = "ImplementsTemporalFilter", DefaultValue = "FALSE" },
                    new FesConstraint { Name = "ImplementsVersionNav", DefaultValue = "FALSE" },
                    new FesConstraint { Name = "ImplementsSorting", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsExtendedOperators", DefaultValue = "FALSE" }
                ]
            },
            IdCapabilities = new IdCapabilities
            {
                ResourceIdentifiers =
                [
                    new ResourceIdentifier { Name = "id" },
                    new ResourceIdentifier { Name = "objectid" },
                    new ResourceIdentifier { Name = "fid" }
                ]
            },
            ScalarCapabilities = new ScalarCapabilities
            {
                LogicalOperators = new object(),
                ComparisonOperators = new ComparisonOperators
                {
                    Operators =
                    [
                        new ComparisonOperator { Name = "PropertyIsEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsNotEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsLessThan" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThan" },
                        new ComparisonOperator { Name = "PropertyIsLessThanOrEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThanOrEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsLike" },
                        new ComparisonOperator { Name = "PropertyIsNull" },
                        new ComparisonOperator { Name = "PropertyIsBetween" }
                    ]
                }
            },
            SpatialCapabilities = new SpatialCapabilities
            {
                GeometryOperands = new GeometryOperands
                {
                    Operands =
                    [
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:Envelope" }
                    ]
                },
                SpatialOperators = new SpatialOperators
                {
                    Operators =
                    [
                        new Models.SpatialOperator { Name = "BBOX" }
                    ]
                }
            }
        };
    }

    private static string GenerateSchemaForTypes(IReadOnlyList<WfsFeatureTypeDescriptor> featureTypes)
    {
        if (featureTypes.Count == 0)
        {
            return GenerateEmptySchema();
        }

        var schema = new StringBuilder();
        schema.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        schema.AppendLine($"""<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:gml="{Wfs20Utilities.GmlNamespace}" xmlns:{FeatureNamespacePrefix}="{FeatureNamespaceUri}" targetNamespace="{FeatureNamespaceUri}" elementFormDefault="qualified" version="1.0.0">""");
        schema.AppendLine($"""  <xsd:import namespace="{Wfs20Utilities.GmlNamespace}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>""");
        schema.AppendLine();

        foreach (var featureType in featureTypes)
        {
            var typeName = XmlConvert.EncodeLocalName(featureType.LocalName);
            schema.AppendLine($"""  <xsd:element name="{typeName}" type="{FeatureNamespacePrefix}:{typeName}Type" substitutionGroup="gml:AbstractFeature"/>""");
            schema.AppendLine($"""  <xsd:complexType name="{typeName}Type">""");
            schema.AppendLine("""    <xsd:complexContent>""");
            schema.AppendLine("""      <xsd:extension base="gml:AbstractFeatureType">""");
            schema.AppendLine("""        <xsd:sequence>""");

            if (featureType.Layer.GeometryField is not null)
            {
                var geometryFieldName = XmlConvert.EncodeLocalName(featureType.Layer.GeometryField.Name);
                schema.AppendLine($"""          <xsd:element name="{geometryFieldName}" type="{MapGeometryPropertyType(featureType.Layer.GeometryType)}" minOccurs="0" nillable="true"/>""");
            }

            foreach (var field in featureType.Layer.AttributeFields)
            {
                var fieldName = XmlConvert.EncodeLocalName(field.Name);
                var minOccurs = field.Nullable ? " minOccurs=\"0\"" : string.Empty;
                var nillable = field.Nullable ? " nillable=\"true\"" : string.Empty;
                schema.AppendLine($"""          <xsd:element name="{fieldName}" type="{MapXsdType(field.Type)}"{minOccurs}{nillable}/>""");
            }

            schema.AppendLine("""        </xsd:sequence>""");
            schema.AppendLine("""      </xsd:extension>""");
            schema.AppendLine("""    </xsd:complexContent>""");
            schema.AppendLine("""  </xsd:complexType>""");
            schema.AppendLine();
        }

        schema.AppendLine("""</xsd:schema>""");
        return schema.ToString();
    }

    private static string GenerateEmptySchema()
    {
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xsd:schema
                xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                xmlns:gml="{{Wfs20Utilities.GmlNamespace}}"
                xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}"
                targetNamespace="{{FeatureNamespaceUri}}"
                elementFormDefault="qualified"
                version="1.0.0">
              <xsd:import namespace="{{Wfs20Utilities.GmlNamespace}}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>
            </xsd:schema>
            """;
    }

    private static string GenerateEmptySchemaForTypes(IReadOnlyList<string> requestedTypes)
    {
        var requested = string.Join(", ", requestedTypes);
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xsd:schema
                xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                xmlns:gml="{{Wfs20Utilities.GmlNamespace}}"
                xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}"
                targetNamespace="{{FeatureNamespaceUri}}"
                elementFormDefault="qualified"
                version="1.0.0">
              <xsd:import namespace="{{Wfs20Utilities.GmlNamespace}}" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>
              <xsd:annotation>
                <xsd:documentation>Requested feature types were not found: {{XmlEscape(requested)}}</xsd:documentation>
              </xsd:annotation>
            </xsd:schema>
            """;
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildGmlResultAsync(
        LayerQueryPlanSet planSet,
        CancellationToken cancellationToken)
    {
        var queryResults = new List<(LayerQueryPlan Plan, ImmutableArray<GmlFeature> Features)>(planSet.Plans.Length);

        foreach (var plan in planSet.Plans)
        {
            var result = await _gmlFeatureStore.QueryGmlAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            queryResults.Add((plan, result.Items));
        }

        var returnedCount = queryResults.Sum(entry => entry.Features.Length);
        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "FeatureCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", planSet.TotalMatched.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberReturned", returnedCount.ToString(CultureInfo.InvariantCulture));

            foreach (var queryResult in queryResults)
            {
                foreach (var feature in queryResult.Features)
                {
                    WriteFeatureMember(writer, queryResult.Plan, feature);
                }
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return (Results.Content(xml, MediaTypes.Gml, Encoding.UTF8), returnedCount);
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildJsonResultAsync(
        LayerQueryPlanSet planSet,
        string normalizedFormat,
        CancellationToken cancellationToken)
    {
        var features = new List<GeoJsonFeature>();
        var geoJsonFeatureStore = _featureReader as IGeoJsonFeatureStore;

        foreach (var plan in planSet.Plans)
        {
            if (geoJsonFeatureStore is not null)
            {
                var result = await geoJsonFeatureStore.QueryGeoJsonAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
                foreach (var feature in result.Items)
                {
                    features.Add(new GeoJsonFeature
                    {
                        Id = BuildFeatureId(plan.Descriptor, feature.Id),
                        Geometry = _geometryServices.ConvertGeoJsonToSimpleGeometry(feature.GeometryGeoJson, AxisOrder.EastNorth),
                        Properties = BuildGeoJsonProperties(feature.Attributes, plan.Descriptor.Layer, plan.Query)
                    });
                }

                continue;
            }

            var fallbackResult = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            foreach (var feature in fallbackResult.Items)
            {
                var geometry = _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, AxisOrder.EastNorth);
                features.Add(new GeoJsonFeature
                {
                    Id = BuildFeatureId(plan.Descriptor, feature.Id),
                    Geometry = geometry,
                    Properties = BuildGeoJsonProperties(feature.Attributes, plan.Descriptor.Layer, plan.Query)
                });
            }
        }

        var payload = new FeatureCollection
        {
            Features = features.ToArray(),
            NumberMatched = planSet.TotalMatched,
            NumberReturned = features.Count,
            TimeStamp = DateTimeOffset.UtcNow
        };

        var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.Json
            : MediaTypes.GeoJson;

        return (Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType), features.Count);
    }

    private async Task<PagedGetFeatureResult> BuildPagedGetFeatureResultAsync(
        WfsFeatureTypeDescriptor descriptor,
        FeatureQuery query,
        string normalizedFormat,
        CancellationToken cancellationToken)
    {
        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase))
        {
            if (_featureReader is not IPagedFeatureReader pagedFeatureReader)
            {
                throw new InvalidOperationException("Paged feature queries are not supported by the configured feature store.");
            }

            var result = await pagedFeatureReader.QueryPageAsync(descriptor.Layer.Id, query, cancellationToken);
            var rows = new List<Dictionary<string, string?>>();
            var attributeHeaders = GetProjectedAttributeFields(descriptor.Layer, query)
                .Select(field => field.Name)
                .ToArray();

            foreach (var feature in result.Items)
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["typeName"] = descriptor.QualifiedName,
                    ["id"] = BuildFeatureId(descriptor, feature.Id)
                };

                foreach (var field in GetProjectedAttributeFields(descriptor.Layer, query))
                {
                    row[field.Name] = feature.Attributes.TryGetValue(field.Name, out var value)
                        ? ConvertToInvariantString(value)
                        : null;
                }

                if (descriptor.Layer.HasGeometry)
                {
                    row["geometry"] = SerializeGeometryAsJson(feature.Geometry);
                }

                rows.Add(row);
            }

            var headers = new List<string> { "typeName", "id" };
            headers.AddRange(attributeHeaders);
            if (descriptor.Layer.HasGeometry)
            {
                headers.Add("geometry");
            }

            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
            foreach (var row in rows)
            {
                csv.AppendLine(string.Join(",", headers.Select(header =>
                    EscapeCsv(row.TryGetValue(header, out var value) ? value : null))));
            }

            return new PagedGetFeatureResult(
                Results.Content(csv.ToString(), MediaTypes.Csv, Encoding.UTF8),
                rows.Count,
                FormatNumberMatched(result.TotalCount));
        }

        var features = new List<GeoJsonFeature>();
        long? totalCount = null;

        if (_featureReader is IPagedGeoJsonFeatureStore pagedGeoJsonFeatureStore)
        {
            var result = await pagedGeoJsonFeatureStore.QueryGeoJsonPageAsync(descriptor.Layer.Id, query, cancellationToken);
            totalCount = result.TotalCount;
            foreach (var feature in result.Items)
            {
                features.Add(new GeoJsonFeature
                {
                    Id = BuildFeatureId(descriptor, feature.Id),
                    Geometry = _geometryServices.ConvertGeoJsonToSimpleGeometry(feature.GeometryGeoJson, AxisOrder.EastNorth),
                    Properties = BuildGeoJsonProperties(feature.Attributes, descriptor.Layer, query)
                });
            }
        }
        else if (_featureReader is IPagedFeatureReader pagedFeatureReader)
        {
            var result = await pagedFeatureReader.QueryPageAsync(descriptor.Layer.Id, query, cancellationToken);
            totalCount = result.TotalCount;
            foreach (var feature in result.Items)
            {
                var geometry = _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, AxisOrder.EastNorth);
                features.Add(new GeoJsonFeature
                {
                    Id = BuildFeatureId(descriptor, feature.Id),
                    Geometry = geometry,
                    Properties = BuildGeoJsonProperties(feature.Attributes, descriptor.Layer, query)
                });
            }
        }
        else
        {
            throw new InvalidOperationException("Paged feature queries are not supported by the configured feature store.");
        }

        var payload = new FeatureCollection
        {
            Features = features.ToArray(),
            NumberMatched = totalCount,
            NumberReturned = features.Count,
            TimeStamp = DateTimeOffset.UtcNow
        };

        var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.Json
            : MediaTypes.GeoJson;

        return new PagedGetFeatureResult(
            Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType),
            features.Count,
            FormatNumberMatched(totalCount));
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildCsvResultAsync(
        LayerQueryPlanSet planSet,
        CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, string?>>();
        var attributeHeaders = new List<string>();
        var geometryRequested = false;

        foreach (var plan in planSet.Plans)
        {
            geometryRequested |= plan.Descriptor.Layer.HasGeometry;

            foreach (var field in GetProjectedAttributeFields(plan.Descriptor.Layer, plan.Query))
            {
                if (!attributeHeaders.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                {
                    attributeHeaders.Add(field.Name);
                }
            }

            var result = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            foreach (var feature in result.Items)
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["typeName"] = plan.Descriptor.QualifiedName,
                    ["id"] = BuildFeatureId(plan.Descriptor, feature.Id)
                };

                foreach (var field in GetProjectedAttributeFields(plan.Descriptor.Layer, plan.Query))
                {
                    row[field.Name] = feature.Attributes.TryGetValue(field.Name, out var value)
                        ? ConvertToInvariantString(value)
                        : null;
                }

                if (geometryRequested)
                {
                    row["geometry"] = SerializeGeometryAsJson(feature.Geometry);
                }

                rows.Add(row);
            }
        }

        var headers = new List<string> { "typeName", "id" };
        headers.AddRange(attributeHeaders);
        if (geometryRequested)
        {
            headers.Add("geometry");
        }

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            csv.AppendLine(string.Join(",", headers.Select(header =>
                EscapeCsv(row.TryGetValue(header, out var value) ? value : null))));
        }

        return (Results.Content(csv.ToString(), MediaTypes.Csv, Encoding.UTF8), rows.Count);
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildValueCollectionResultAsync(
        LayerValuePlanSet planSet,
        CancellationToken cancellationToken)
    {
        var queryResults = new List<(LayerValuePlan Plan, ImmutableArray<GmlFeature>? GmlFeatures, ImmutableArray<Feature>? Features)>(planSet.Plans.Length);
        var returnedCount = 0;

        foreach (var plan in planSet.Plans)
        {
            if (plan.ValueReference.IsGeometry)
            {
                var result = await _gmlFeatureStore.QueryGmlAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
                queryResults.Add((plan, result.Items, null));
                returnedCount += result.Items.Length;
                continue;
            }

            var features = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            queryResults.Add((plan, null, features.Items));
            returnedCount += features.Items.Length;
        }

        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "ValueCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", planSet.TotalMatched.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberReturned", returnedCount.ToString(CultureInfo.InvariantCulture));

            foreach (var queryResult in queryResults)
            {
                if (queryResult.Plan.ValueReference.IsGeometry)
                {
                    foreach (var feature in queryResult.GmlFeatures ?? ImmutableArray<GmlFeature>.Empty)
                    {
                        writer.WriteStartElement("wfs", "member", Wfs20Utilities.WfsNamespace);
                        if (!string.IsNullOrWhiteSpace(feature.GeometryGml))
                        {
                            writer.WriteRaw(feature.GeometryGml);
                        }
                        writer.WriteEndElement();
                    }

                    continue;
                }

                foreach (var feature in queryResult.Features ?? ImmutableArray<Feature>.Empty)
                {
                    writer.WriteStartElement("wfs", "member", Wfs20Utilities.WfsNamespace);
                    var value = ExtractValue(feature, queryResult.Plan.ValueReference);
                    if (value is not null)
                    {
                        writer.WriteString(ConvertToInvariantString(value));
                    }
                    writer.WriteEndElement();
                }
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return (Results.Content(xml, MediaTypes.Gml, Encoding.UTF8), returnedCount);
    }

    private static void WriteFeatureMember(XmlWriter writer, LayerQueryPlan plan, GmlFeature feature)
    {
        writer.WriteStartElement("wfs", "member", Wfs20Utilities.WfsNamespace);
        writer.WriteStartElement(FeatureNamespacePrefix, plan.Descriptor.LocalName, FeatureNamespaceUri);
        writer.WriteAttributeString("gml", "id", Wfs20Utilities.GmlNamespace, BuildFeatureId(plan.Descriptor, feature.Id));

        if (plan.Descriptor.Layer.GeometryField is not null &&
            !string.IsNullOrWhiteSpace(feature.GeometryGml))
        {
            writer.WriteStartElement(
                FeatureNamespacePrefix,
                XmlConvert.EncodeLocalName(plan.Descriptor.Layer.GeometryField.Name),
                FeatureNamespaceUri);
            writer.WriteRaw(feature.GeometryGml);
            writer.WriteEndElement();
        }

        foreach (var field in GetProjectedAttributeFields(plan.Descriptor.Layer, plan.Query))
        {
            if (!feature.Attributes.TryGetValue(field.Name, out var value) || value is null)
            {
                continue;
            }

            writer.WriteStartElement(
                FeatureNamespacePrefix,
                XmlConvert.EncodeLocalName(field.Name),
                FeatureNamespaceUri);
            writer.WriteString(ConvertToInvariantString(value));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string BuildFeatureId(WfsFeatureTypeDescriptor descriptor, long featureId)
        => $"{descriptor.LocalName}.{featureId.ToString(CultureInfo.InvariantCulture)}";

    private static Dictionary<string, object?> BuildGeoJsonProperties(
        ImmutableDictionary<string, object?> attributes,
        LayerDefinition layer,
        FeatureQuery query)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var objectIdFieldName = layer.ObjectIdFieldName;
        foreach (var field in GetProjectedAttributeFields(layer, query))
        {
            if (field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (attributes.TryGetValue(field.Name, out var value))
            {
                properties[field.Name] = value;
            }
        }

        return properties;
    }

    private string? SerializeGeometryAsJson(byte[]? geometry)
    {
        var simpleGeometry = _geometryServices.ConvertWkbToSimpleGeometry(geometry, AxisOrder.EastNorth);
        return simpleGeometry is null
            ? null
            : JsonSerializer.Serialize(simpleGeometry, OgcJsonContext.Default.SimpleGeoJsonGeometry);
    }

    private static IEnumerable<FieldDefinition> GetProjectedAttributeFields(LayerDefinition layer, FeatureQuery query)
    {
        if (query.OutFields is not { } outFields)
        {
            return layer.AttributeFields;
        }

        if (outFields.IsDefaultOrEmpty)
        {
            return Array.Empty<FieldDefinition>();
        }

        return layer.AttributeFields.Where(field =>
            outFields.Any(candidate => candidate.Equals(field.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static ValueReferenceResolution ResolveValueReference(LayerDefinition layer, string valueReference)
    {
        var resolvedName = ResolveFieldName(layer, valueReference, allowGeometryAlias: true)
            ?? throw new ArgumentException($"Unknown valueReference '{valueReference}' for feature type '{layer.Name}'.");

        var isGeometry = layer.GeometryField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true;
        var isFeatureId = layer.PrimaryKeyField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true ||
                          resolvedName.Equals("objectid", StringComparison.OrdinalIgnoreCase);

        return new ValueReferenceResolution(valueReference, resolvedName, isGeometry, isFeatureId);
    }

    private static object? ExtractValue(Feature feature, ValueReferenceResolution valueReference)
    {
        if (valueReference.IsFeatureId)
        {
            return feature.Id;
        }

        return feature.Attributes.TryGetValue(valueReference.CanonicalName, out var value)
            ? value
            : null;
    }

    private static bool IsSupportedFeatureOutputFormat(string format)
    {
        return string.Equals(format, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Gml32Simplified, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult CreateEmptyFeatureCollectionResult(string normalizedFormat)
    {
        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase))
        {
            var payload = new FeatureCollection
            {
                Features = [],
                NumberMatched = 0,
                NumberReturned = 0,
                TimeStamp = DateTimeOffset.UtcNow
            };

            var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
                ? MediaTypes.Json
                : MediaTypes.GeoJson;

            return Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType);
        }

        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Content("typeName,id\n", MediaTypes.Csv, Encoding.UTF8);
        }

        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection xmlns:wfs="{{Wfs20Utilities.WfsNamespace}}" xmlns:gml="{{Wfs20Utilities.GmlNamespace}}" xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}" timeStamp="{{DateTimeOffset.UtcNow:O}}" numberMatched="0" numberReturned="0" />
            """;

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private static IResult CreateHitsFeatureCollectionResult(long totalMatched)
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection xmlns:wfs="{{Wfs20Utilities.WfsNamespace}}" xmlns:gml="{{Wfs20Utilities.GmlNamespace}}" xmlns:{{FeatureNamespacePrefix}}="{{FeatureNamespaceUri}}" timeStamp="{{DateTimeOffset.UtcNow:O}}" numberMatched="{{totalMatched.ToString(CultureInfo.InvariantCulture)}}" numberReturned="0" />
            """;

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private static IResult CreateEmptyValueCollectionResult()
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:ValueCollection xmlns:wfs="{{Wfs20Utilities.WfsNamespace}}" xmlns:gml="{{Wfs20Utilities.GmlNamespace}}" timeStamp="{{DateTimeOffset.UtcNow:O}}" numberMatched="0" numberReturned="0" />
            """;

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private static string WriteXmlDocument(Action<XmlWriter> writeAction)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stream = new MemoryStream();
        using var writer = XmlWriter.Create(stream, settings);
        writeAction(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildTypeLocalName(LayerDefinition layer)
    {
        var input = string.IsNullOrWhiteSpace(layer.Name)
            ? $"layer_{layer.Id}"
            : layer.Name;

        var builder = new StringBuilder(input.Length);
        var lastWasUnderscore = false;

        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasUnderscore = false;
                continue;
            }

            if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }

        var result = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = $"layer_{layer.Id}";
        }

        if (char.IsDigit(result[0]))
        {
            result = $"layer_{result}";
        }

        return result;
    }

    private static string NormalizeIdentifier(string identifier)
    {
        var normalized = identifier.Trim();
        if (normalized.Length == 0)
        {
            return normalized;
        }

        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..];
        }

        var colonIndex = normalized.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < normalized.Length - 1)
        {
            normalized = normalized[(colonIndex + 1)..];
        }

        return normalized;
    }

    private static string[] ParseQualifiedList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? ResolveFieldName(LayerDefinition layer, string requestedName, bool allowGeometryAlias)
    {
        var normalized = NormalizeIdentifier(requestedName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (layer.PrimaryKeyField is not null &&
            (normalized.Equals("id", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("fid", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals(layer.PrimaryKeyField.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return layer.PrimaryKeyField.Name;
        }

        if (allowGeometryAlias &&
            layer.GeometryField is not null &&
            (normalized.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
             normalized.Equals(layer.GeometryField.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return layer.GeometryField.Name;
        }

        return layer.Fields
            .FirstOrDefault(field => field.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    private static string MapGeometryPropertyType(Honua.Core.Features.Catalog.Domain.GeometryType geometryType)
    {
        return geometryType switch
        {
            Honua.Core.Features.Catalog.Domain.GeometryType.Point => "gml:PointPropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPoint => "gml:MultiPointPropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.LineString => "gml:CurvePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiLineString => "gml:MultiCurvePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.Polygon => "gml:SurfacePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPolygon => "gml:MultiSurfacePropertyType",
            Honua.Core.Features.Catalog.Domain.GeometryType.GeometryCollection => "gml:GeometryPropertyType",
            _ => "gml:GeometryPropertyType"
        };
    }

    private static string MapXsdType(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.Integer => "xsd:int",
            FieldType.BigInteger => "xsd:long",
            FieldType.Double => "xsd:double",
            FieldType.Float => "xsd:float",
            FieldType.Boolean => "xsd:boolean",
            FieldType.DateTime => "xsd:dateTime",
            FieldType.Date => "xsd:date",
            FieldType.Time => "xsd:time",
            FieldType.Binary => "xsd:base64Binary",
            FieldType.Json => "xsd:anyType",
            _ => "xsd:string"
        };
    }

    private static string ConvertToInvariantString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            bool boolean => XmlConvert.ToString(boolean),
            DateTimeOffset dateTimeOffset => XmlConvert.ToString(dateTimeOffset),
            DateTime dateTime => XmlConvert.ToString(dateTime, XmlDateTimeSerializationMode.RoundtripKind),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => ConvertStructuredValueToJson(value)
        };
    }

    private static string ConvertStructuredValueToJson(object value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteStructuredJsonValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStructuredJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                return;
            case sbyte signedByteValue:
                writer.WriteNumberValue(signedByteValue);
                return;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                return;
            case ushort ushortValue:
                writer.WriteNumberValue(ushortValue);
                return;
            case int intValue:
                writer.WriteNumberValue(intValue);
                return;
            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                return;
            case long longValue:
                writer.WriteNumberValue(longValue);
                return;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                return;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                return;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                return;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                return;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                return;
            case DateOnly dateOnly:
                writer.WriteStringValue(dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return;
            case TimeOnly timeOnly:
                writer.WriteStringValue(timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                return;
            case Guid guid:
                writer.WriteStringValue(guid);
                return;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                writer.WriteStartObject();
                foreach (var (key, nestedValue) in pairs)
                {
                    writer.WritePropertyName(key);
                    WriteStructuredJsonValue(writer, nestedValue);
                }
                writer.WriteEndObject();
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    writer.WritePropertyName(key);
                    WriteStructuredJsonValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    WriteStructuredJsonValue(writer, item);
                }
                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    private static string EscapeCsv(string? value)
    {
        var safeValue = value ?? string.Empty;
        if (!safeValue.Contains(',') &&
            !safeValue.Contains('"') &&
            !safeValue.Contains('\n') &&
            !safeValue.Contains('\r'))
        {
            return safeValue;
        }

        return $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string XmlEscape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private bool ShouldUsePagedGetFeatureFastPath(
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string normalizedFormat,
        bool isHitsRequest,
        int maxFeatures)
    {
        if (_wfs20Options.NumberMatchedPolicy != Wfs20NumberMatchedPolicy.UnknownWhenExpensive)
        {
            return false;
        }

        if (isHitsRequest || selectedTypes.Count != 1 || maxFeatures <= 0)
        {
            return false;
        }

        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase))
        {
            return _featureReader is IPagedFeatureReader;
        }

        var isJsonFormat =
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase);

        if (!isJsonFormat)
        {
            return false;
        }

        return _featureReader is IPagedGeoJsonFeatureStore || _featureReader is IPagedFeatureReader;
    }

    private static string FormatNumberMatched(long? totalCount)
        => totalCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private sealed record WfsFeatureTypeDescriptor(
        LayerDefinition Layer,
        string QualifiedName,
        string LocalName);

    private sealed record LayerQueryPlan(
        WfsFeatureTypeDescriptor Descriptor,
        FeatureQuery Query,
        long MatchedCount);

    private sealed record LayerValuePlan(
        WfsFeatureTypeDescriptor Descriptor,
        FeatureQuery Query,
        long MatchedCount,
        ValueReferenceResolution ValueReference);

    private readonly record struct LayerQueryPlanSet(
        ImmutableArray<LayerQueryPlan> Plans,
        long TotalMatched);

    private readonly record struct LayerValuePlanSet(
        ImmutableArray<LayerValuePlan> Plans,
        long TotalMatched);

    private readonly record struct PagedGetFeatureResult(
        IResult Result,
        int ReturnedCount,
        string NumberMatchedSummary);

    private readonly record struct ValueReferenceResolution(
        string RequestedName,
        string CanonicalName,
        bool IsGeometry,
        bool IsFeatureId);
}
