// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.ServiceDefaults;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Core handler for WFS 2.0 operations backed by the shared catalog and feature stores.
/// </summary>
internal sealed partial class Wfs20Handler
{
    private const string FeatureNamespacePrefix = "honua";
    private const string FeatureNamespaceUri = "http://honua.io/wfs";
    private const string GetFeatureByIdStoredQueryId = "urn:ogc:def:query:OGC-WFS::GetFeatureById";
    private const string GetFeatureByIdStoredQueryUri = "http://www.opengis.net/def/query/OGC-WFS/0/GetFeatureById";
    private const string WfsQueryExpressionLanguage = "urn:ogc:def:queryLanguage:OGC-WFS::WFSQueryExpression";
    private const string LegacyWfsQueryExpressionLanguage = "urn:ogc:def:queryLanguage:OGC-WFS::WFS_QueryExpression";
    private static readonly WKBWriter BboxWkbWriter = new();
    private static readonly WKBWriter GeometryWkbWriter = new();
    private static readonly SqlFragment FalseSqlFilter = new("FALSE", Array.Empty<object?>());

    private readonly ILogger<Wfs20Handler> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IGmlFeatureStore _gmlFeatureStore;
    private readonly IFilterExpressionService _filterExpressionService;
    private readonly IQueryParameterAdapter<Wfs20QueryRequest> _queryParameterAdapter;
    private readonly IQueryProcessor _queryProcessor;
    private readonly IEditParameterAdapter<Wfs20EditRequest> _editParameterAdapter;
    private readonly IEditProcessor _editProcessor;
    private readonly OgcFeaturesGeometryServices _geometryServices;
    private readonly Wfs20Options _wfs20Options;
    private readonly ICoordinateTransformService _coordinateTransformService;
    private readonly ICrsRegistry _crsRegistry;
    private readonly FeatureMutationValidator _mutationValidator;
    private readonly FeatureMutationEventService _mutationEventService;
    private readonly EditLimits _editLimits;

    public Wfs20Handler(
        ILogger<Wfs20Handler> logger,
        Wfs20QueryServices queryServices)
    {
        _logger = logger;
        _layerCatalog = queryServices.LayerCatalog;
        _featureReader = queryServices.FeatureReader;
        _featureWriter = queryServices.FeatureWriter;
        _gmlFeatureStore = queryServices.GmlFeatureStore;
        _filterExpressionService = queryServices.FilterExpressionService;
        _queryParameterAdapter = queryServices.QueryParameterAdapter;
        _queryProcessor = queryServices.QueryProcessor;
        _editParameterAdapter = queryServices.EditParameterAdapter;
        _editProcessor = queryServices.EditProcessor;
        _geometryServices = queryServices.GeometryServices;
        _coordinateTransformService = queryServices.CoordinateTransformService;
        _crsRegistry = queryServices.CrsRegistry;
        _wfs20Options = queryServices.Wfs20Options;
        _mutationValidator = queryServices.MutationValidator;
        _mutationEventService = queryServices.MutationEventService;
        _editLimits = queryServices.EditLimits;
    }

    private static IResult CreateStoredQueryFeatureNotFoundResult(HttpContext context, string featureId)
    {
        return Wfs20ErrorResults.CreateNotFound(
            context,
            "NotFound",
            $"Feature '{featureId}' was not found.",
            "id");
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
        if (!Wfs20Utilities.TryNormalizeResultType(resultType, out var normalizedResultType))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid RESULTTYPE parameter '{resultType}'. Supported values are 'results' and 'hits'.",
                "resultType");
        }

        if (!Wfs20Utilities.TryParseCount(count, out var maxFeatures))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid COUNT parameter '{count}'. COUNT must be a non-negative integer.",
                "count");
        }

        if (!Wfs20Utilities.TryParseStartIndex(startIndex, out var offset))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid STARTINDEX parameter '{startIndex}'. STARTINDEX must be a non-negative integer.",
                "startIndex");
        }

        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        var isHitsRequest = string.Equals(normalizedResultType, "hits", StringComparison.OrdinalIgnoreCase);

        Wfs20Log.GetFeatureRequested(_logger, typeNames ?? "ALL", normalizedFormat);

        try
        {
            if (!IsSupportedFeatureOutputFormat(normalizedFormat))
            {
                Wfs20Log.UnsupportedOutputFormatRequested(_logger, outputFormat ?? normalizedFormat);
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Unsupported output format '{outputFormat}'. Supported formats: {string.Join(", ", Wfs20Utilities.OutputFormats.All)}",
                    "outputFormat");
            }

            if (TryGetMultiQueryXmlRequest(context, out var xmlQueries))
            {
                return await HandleGetFeatureXmlQueriesAsync(
                    context,
                    xmlQueries,
                    normalizedFormat,
                    outputFormat,
                    maxFeatures,
                    offset,
                    sortBy,
                    bbox,
                    resourceId,
                    srsName,
                    normalizedResultType,
                    isHitsRequest,
                    cancellationToken);
            }

            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var unknownTypes = GetUnknownRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (unknownTypes.Length > 0)
            {
                var requestedTypeMessage = unknownTypes.Length == 1
                    ? $"Unknown feature type '{unknownTypes[0]}'."
                    : $"Unknown feature types: {string.Join(", ", unknownTypes.Select(type => $"'{type}'"))}.";
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    requestedTypeMessage,
                    "typeNames");
            }

            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            var wfsUrl = $"{BaseUrlResolver.GetBaseUrl(context)}/wfs";
            if (selectedTypes.Length == 0)
            {
                return isHitsRequest
                    ? CreateHitsFeatureCollectionResult(0)
                    : CreateEmptyFeatureCollectionResult(normalizedFormat);
            }

            if (ShouldUsePagedGetFeatureFastPath(selectedTypes, normalizedFormat, isHitsRequest, maxFeatures))
            {
                var descriptor = selectedTypes[0];
                var query = (await BuildFeatureQueryAsync(
                    descriptor.Layer,
                    propertyName,
                    sortBy,
                    bbox,
                    filter,
                    resourceId,
                    srsName,
                    enforceResourceIdTypeMatch: true,
                    requireResourceIdQualifier: selectedTypes.Length > 1,
                    cancellationToken: cancellationToken).ConfigureAwait(false)) with
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
                var emptyMetadata = BuildFeatureCollectionResponseMetadata(
                    wfsUrl,
                    selectedTypes,
                    outputFormat,
                    count,
                    sortBy,
                    bbox,
                    filter,
                    resourceId,
                    propertyName,
                    srsName,
                    normalizedResultType,
                    offset,
                    maxFeatures,
                    0,
                    0);
                return isHitsRequest
                    ? CreateHitsFeatureCollectionResult(0, emptyMetadata.SchemaLocation, emptyMetadata.PagingLinks.Next, emptyMetadata.PagingLinks.Previous)
                    : CreateEmptyFeatureCollectionResult(normalizedFormat, emptyMetadata.SchemaLocation, emptyMetadata.PagingLinks.Next, emptyMetadata.PagingLinks.Previous);
            }

            if (isHitsRequest)
            {
                var hitsMetadata = BuildFeatureCollectionResponseMetadata(
                    wfsUrl,
                    selectedTypes,
                    outputFormat,
                    count,
                    sortBy,
                    bbox,
                    filter,
                    resourceId,
                    propertyName,
                    srsName,
                    normalizedResultType,
                    offset,
                    maxFeatures,
                    planSet.TotalMatched,
                    0);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var matchedSummary = planSet.TotalMatched.ToString(CultureInfo.InvariantCulture);
                    Wfs20Log.GetFeatureReturned(_logger, 0, matchedSummary);
                }
                return CreateHitsFeatureCollectionResult(
                    planSet.TotalMatched,
                    hitsMetadata.SchemaLocation,
                    hitsMetadata.PagingLinks.Next,
                    hitsMetadata.PagingLinks.Previous);
            }

            var expectedReturnedCount = planSet.Plans.Sum(plan => plan.Query.Limit ?? 0);
            var responseMetadata = BuildFeatureCollectionResponseMetadata(
                wfsUrl,
                selectedTypes,
                outputFormat,
                count,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                normalizedResultType,
                offset,
                maxFeatures,
                planSet.TotalMatched,
                expectedReturnedCount);

            var (result, returnedCount) = normalizedFormat switch
            {
                Wfs20Utilities.OutputFormats.Csv => await BuildCsvResultAsync(planSet, cancellationToken),
                Wfs20Utilities.OutputFormats.GeoJson or Wfs20Utilities.OutputFormats.Json => await BuildJsonResultAsync(planSet, normalizedFormat, cancellationToken),
                _ => await BuildGmlResultAsync(
                    planSet,
                    responseMetadata.SchemaLocation,
                    responseMetadata.PagingLinks.Next,
                    responseMetadata.PagingLinks.Previous,
                    cancellationToken)
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
        catch (WfsQueryException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                ex.ExceptionCode,
                ex.Message,
                ex.Locator);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Fes20ParseException)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            var exceptionCode = ex.Message.Contains("boundedBy", StringComparison.OrdinalIgnoreCase)
                ? "OperationProcessingFailed"
                : "InvalidParameterValue";
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                exceptionCode,
                "Invalid WFS parameter value; see logs for details.");
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
        bool valueReferenceSpecified,
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

        if (!valueReferenceSpecified)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Missing required 'valueReference' parameter for GetPropertyValue.",
                "valueReference");
        }

        if (string.IsNullOrWhiteSpace(valueReference))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                "The 'valueReference' parameter must not be empty for GetPropertyValue.",
                "valueReference");
        }

        var normalizedOutputFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        if (!IsSupportedValueOutputFormat(normalizedOutputFormat))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Unsupported output format '{outputFormat}'. GetPropertyValue supports application/gml+xml, application/json, and application/geo+json.",
                "outputFormat");
        }

        var requestedTypes = Wfs20Utilities.ParseTypeNames(typeNames);
        if (!Wfs20Utilities.TryParseCount(count, out var maxFeatures))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid COUNT parameter '{count}'. COUNT must be a non-negative integer.",
                "count");
        }

        if (!Wfs20Utilities.TryParseStartIndex(startIndex, out var offset))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Invalid STARTINDEX parameter '{startIndex}'. STARTINDEX must be a non-negative integer.",
                "startIndex");
        }

        Wfs20Log.GetPropertyValueRequested(_logger, valueReference, typeNames ?? "ALL");

        try
        {
            var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
            var unknownTypes = GetUnknownRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (unknownTypes.Length > 0)
            {
                var requestedTypeMessage = unknownTypes.Length == 1
                    ? $"Unknown feature type '{unknownTypes[0]}'."
                    : $"Unknown feature types: {string.Join(", ", unknownTypes.Select(type => $"'{type}'"))}.";
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    requestedTypeMessage,
                    "typeNames");
            }

            var selectedTypes = ResolveRequestedFeatureTypes(publishedTypes, requestedTypes);
            if (selectedTypes.Length == 0)
            {
                return CreateEmptyValueCollectionResult(normalizedOutputFormat);
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
                return CreateEmptyValueCollectionResult(normalizedOutputFormat);
            }

            var (result, returnedCount) = string.Equals(normalizedOutputFormat, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase)
                ? await BuildValueCollectionResultAsync(planSet, cancellationToken)
                : await BuildValueJsonResultAsync(planSet, normalizedOutputFormat, cancellationToken);
            Wfs20Log.GetPropertyValueReturned(_logger, returnedCount);
            return result;
        }
        catch (WfsQueryException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                ex.ExceptionCode,
                ex.Message,
                ex.Locator);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Fes20ParseException)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                "Invalid WFS parameter value; see logs for details.");
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.GetPropertyValue, ex.Message);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process GetPropertyValue request.");
        }
    }

    /// <summary>
    /// Processes WFS 2.0 Transaction requests by translating XML actions into the shared edit pipeline.
    /// Behavior reference: ../Honua.Server/src/domain/ogc/wfs/WfsTransactionHandlers.cs
    /// Preserves request order and carries insert handles into InsertResults.
    /// </summary>

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
            var query = await BuildFeatureQueryAsync(
                featureType.Layer,
                propertyName,
                sortBy,
                bbox,
                filter,
                resourceId,
                srsName,
                enforceResourceIdTypeMatch: true,
                requireResourceIdQualifier: featureTypes.Count > 1,
                cancellationToken: cancellationToken);

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

    private async Task<IResult> HandleGetFeatureXmlQueriesAsync(
        HttpContext context,
        IReadOnlyList<Wfs20XmlQueryParameters> xmlQueries,
        string normalizedFormat,
        string? outputFormat,
        int maxFeatures,
        int offset,
        string? sortBy,
        string? bbox,
        string? resourceId,
        string? fallbackSrsName,
        string? normalizedResultType,
        bool isHitsRequest,
        CancellationToken cancellationToken)
    {
        var publishedTypes = await GetPublishedFeatureTypesAsync(context, cancellationToken);
        var unknownTypes = xmlQueries
            .SelectMany(query => Wfs20Utilities.ParseTypeNames(query.TypeNames))
            .Where(requestedType => GetUnknownRequestedFeatureTypes(publishedTypes, new[] { requestedType }).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownTypes.Length > 0)
        {
            var requestedTypeMessage = unknownTypes.Length == 1
                ? $"Unknown feature type '{unknownTypes[0]}'."
                : $"Unknown feature types: {string.Join(", ", unknownTypes.Select(type => $"'{type}'"))}.";
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                requestedTypeMessage,
                "typeNames");
        }

        var planSet = await BuildXmlQueryPlansAsync(
            publishedTypes,
            xmlQueries,
            sortBy,
            bbox,
            resourceId,
            fallbackSrsName,
            offset,
            maxFeatures,
            cancellationToken);

        var selectedTypes = xmlQueries
            .SelectMany(query => ResolveRequestedFeatureTypes(publishedTypes, Wfs20Utilities.ParseTypeNames(query.TypeNames)))
            .DistinctBy(static descriptor => descriptor.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var wfsUrl = $"{BaseUrlResolver.GetBaseUrl(context)}/wfs";

        if (planSet.TotalMatched == 0)
        {
            var emptyMetadata = BuildFeatureCollectionResponseMetadata(
                wfsUrl,
                selectedTypes,
                outputFormat,
                maxFeatures.ToString(CultureInfo.InvariantCulture),
                sortBy,
                bbox,
                null,
                resourceId,
                null,
                fallbackSrsName,
                normalizedResultType,
                offset,
                maxFeatures,
                0,
                0);
            return isHitsRequest
                ? CreateHitsFeatureCollectionResult(0, emptyMetadata.SchemaLocation, emptyMetadata.PagingLinks.Next, emptyMetadata.PagingLinks.Previous)
                : CreateEmptyFeatureCollectionResult(normalizedFormat, emptyMetadata.SchemaLocation, emptyMetadata.PagingLinks.Next, emptyMetadata.PagingLinks.Previous);
        }

        var responseMetadata = BuildFeatureCollectionResponseMetadata(
            wfsUrl,
            selectedTypes,
            outputFormat,
            maxFeatures.ToString(CultureInfo.InvariantCulture),
            sortBy,
            bbox,
            null,
            resourceId,
            null,
            fallbackSrsName,
            normalizedResultType,
            offset,
            maxFeatures,
            planSet.TotalMatched,
            isHitsRequest ? 0 : planSet.Plans.Sum(plan => plan.Query.Limit ?? 0));

        if (isHitsRequest)
        {
            return CreateHitsFeatureCollectionResult(
                planSet.TotalMatched,
                responseMetadata.SchemaLocation,
                responseMetadata.PagingLinks.Next,
                responseMetadata.PagingLinks.Previous);
        }

        var (result, returnedCount) = normalizedFormat switch
        {
            Wfs20Utilities.OutputFormats.Csv => await BuildCsvResultAsync(planSet, cancellationToken),
            Wfs20Utilities.OutputFormats.GeoJson or Wfs20Utilities.OutputFormats.Json => await BuildJsonResultAsync(planSet, normalizedFormat, cancellationToken),
            _ => await BuildGmlResultAsync(
                planSet,
                responseMetadata.SchemaLocation,
                responseMetadata.PagingLinks.Next,
                responseMetadata.PagingLinks.Previous,
                cancellationToken)
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

    private async Task<LayerQueryPlanSet> BuildXmlQueryPlansAsync(
        ImmutableArray<WfsFeatureTypeDescriptor> publishedTypes,
        IReadOnlyList<Wfs20XmlQueryParameters> xmlQueries,
        string? sortBy,
        string? bbox,
        string? resourceId,
        string? fallbackSrsName,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var plans = ImmutableArray.CreateBuilder<LayerQueryPlan>();
        var remainingOffset = offset;
        var remainingCount = count;
        long totalMatched = 0;

        foreach (var xmlQuery in xmlQueries)
        {
            var selectedTypes = ResolveRequestedFeatureTypes(
                publishedTypes,
                Wfs20Utilities.ParseTypeNames(xmlQuery.TypeNames));
            foreach (var featureType in selectedTypes)
            {
                var query = await BuildFeatureQueryAsync(
                    featureType.Layer,
                    xmlQuery.PropertyName,
                    xmlQuery.SortBy ?? sortBy,
                    bbox,
                    xmlQuery.Filter,
                    resourceId,
                    xmlQuery.SrsName ?? fallbackSrsName,
                    enforceResourceIdTypeMatch: true,
                    requireResourceIdQualifier: selectedTypes.Length > 1,
                    cancellationToken: cancellationToken);

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
        }

        return new LayerQueryPlanSet(plans.ToImmutable(), totalMatched);
    }

    private static bool TryGetMultiQueryXmlRequest(
        HttpContext context,
        out Wfs20XmlQueryParameters[] queries)
    {
        if (context.Items.TryGetValue(Wfs20DispatcherEndpoint.ParsedXmlQueriesItemKey, out var value) &&
            value is Wfs20XmlQueryParameters[] parsedQueries &&
            parsedQueries.Length > 1)
        {
            queries = parsedQueries;
            return true;
        }

        queries = [];
        return false;
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
            var query = await BuildValueQueryAsync(
                featureType.Layer,
                resolvedValueReference,
                bbox,
                filter,
                resourceId,
                srsName,
                enforceResourceIdTypeMatch: true,
                requireResourceIdQualifier: featureTypes.Count > 1,
                cancellationToken: cancellationToken);

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

    private async ValueTask<FeatureQuery> BuildFeatureQueryAsync(
        LayerDefinition layer,
        string? propertyName,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        bool enforceResourceIdTypeMatch,
        bool requireResourceIdQualifier,
        CancellationToken cancellationToken)
    {
        var projectedFields = ResolveProjectedFields(layer, propertyName);
        var (normalizedFilter, normalizedResourceId) = NormalizeFilterInputs(filter, resourceId);
        var sqlFilter = TranslateFesFilter(layer, normalizedFilter);
        var spatialFilter = ParseBboxFilter(bbox, layer);
        var resourceIds = ParseResourceIds(normalizedResourceId, layer, enforceResourceIdTypeMatch, requireResourceIdQualifier);
        sqlFilter = resourceIds.MatchesNothing
            ? CombineSqlFilters(sqlFilter, FalseSqlFilter)
            : sqlFilter;
        var orderBy = ParseSortBy(layer, sortBy);
        var outputSrid = await ResolveRequestedOutputSridAsync(layer, srsName, cancellationToken).ConfigureAwait(false);
        var outputAxisOrder = await ResolveOutputAxisOrderAsync(srsName, outputSrid, cancellationToken).ConfigureAwait(false);
        var queryAdapterResult = await _queryParameterAdapter.ConvertAsync(
            new Wfs20QueryRequest
            {
                SqlFilter = sqlFilter,
                ObjectIds = resourceIds.ObjectIds,
                OutFields = projectedFields,
                SpatialFilter = spatialFilter,
                OutputCrs = QueryCrs.Create(outputSrid, outputAxisOrder),
                OrderBy = orderBy
            },
            layer,
            cancellationToken).ConfigureAwait(false);
        if (!queryAdapterResult.IsSuccess || queryAdapterResult.Query == null)
        {
            throw new ArgumentException(queryAdapterResult.ErrorMessage ?? "Invalid WFS query parameters.");
        }

        var unifiedQuery = _queryProcessor.OptimizeQuery(queryAdapterResult.Query.Value, layer);
        var validation = _queryProcessor.ValidateQuery(unifiedQuery, layer);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage ?? "Invalid WFS query parameters.");
        }

        return _queryProcessor.ToFeatureQuery(unifiedQuery, layer);
    }

    private async ValueTask<FeatureQuery> BuildValueQueryAsync(
        LayerDefinition layer,
        ValueReferenceResolution valueReference,
        string? bbox,
        string? filter,
        string? resourceId,
        string? srsName,
        bool enforceResourceIdTypeMatch,
        bool requireResourceIdQualifier,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string>? outFields = valueReference.IsGeometry || valueReference.IsFeatureId
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(valueReference.CanonicalName);

        var (normalizedFilter, normalizedResourceId) = NormalizeFilterInputs(filter, resourceId);
        var sqlFilter = TranslateFesFilter(layer, normalizedFilter);
        var spatialFilter = ParseBboxFilter(bbox, layer);
        var resourceIds = ParseResourceIds(normalizedResourceId, layer, enforceResourceIdTypeMatch, requireResourceIdQualifier);
        sqlFilter = resourceIds.MatchesNothing
            ? CombineSqlFilters(sqlFilter, FalseSqlFilter)
            : sqlFilter;
        var outputSrid = await ResolveRequestedOutputSridAsync(layer, srsName, cancellationToken).ConfigureAwait(false);
        var outputAxisOrder = await ResolveOutputAxisOrderAsync(srsName, outputSrid, cancellationToken).ConfigureAwait(false);
        var queryAdapterResult = await _queryParameterAdapter.ConvertAsync(
            new Wfs20QueryRequest
            {
                SqlFilter = sqlFilter,
                ObjectIds = resourceIds.ObjectIds,
                OutFields = outFields,
                SpatialFilter = spatialFilter,
                OutputCrs = QueryCrs.Create(outputSrid, outputAxisOrder)
            },
            layer,
            cancellationToken).ConfigureAwait(false);
        if (!queryAdapterResult.IsSuccess || queryAdapterResult.Query == null)
        {
            throw new ArgumentException(queryAdapterResult.ErrorMessage ?? "Invalid WFS query parameters.");
        }

        var unifiedQuery = _queryProcessor.OptimizeQuery(queryAdapterResult.Query.Value, layer);
        var validation = _queryProcessor.ValidateQuery(unifiedQuery, layer);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.ErrorMessage ?? "Invalid WFS query parameters.");
        }

        return _queryProcessor.ToFeatureQuery(unifiedQuery, layer);
    }

    private SqlFragment? TranslateFesFilter(LayerDefinition layer, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var expression = Fes20Parser.ParseFilter(filter);
        expression = FilterExpressionHelpers.NormalizeFilterPropertyReferences(expression, layer);

        if (!FilterExpressionHelpers.IsBooleanFilterExpression(expression))
        {
            throw new ArgumentException("FILTER expression must be a boolean predicate.");
        }

        var translation = _filterExpressionService.Translate(expression, layer);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid filter expression.");
        }

        return translation.SqlFilter;
    }

    private static (string? Filter, string? ResourceId) NormalizeFilterInputs(
        string? filter,
        string? resourceId)
    {
        if (!TryExtractStandaloneResourceIds(filter, out var normalizedFilter, out var filterResourceIds))
        {
            return (filter, resourceId);
        }

        var combinedResourceIds = string.IsNullOrWhiteSpace(resourceId)
            ? filterResourceIds
            : string.IsNullOrWhiteSpace(filterResourceIds)
                ? resourceId
                : $"{resourceId},{filterResourceIds}";

        return (normalizedFilter, combinedResourceIds);
    }

    private static bool TryExtractStandaloneResourceIds(
        string? filter,
        out string? normalizedFilter,
        out string? resourceIds)
    {
        normalizedFilter = filter;
        resourceIds = null;

        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        XDocument document;
        try
        {
            document = SecureXmlDocumentParser.Parse(filter, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return false;
        }

        var root = document.Root;
        if (root == null || !string.Equals(root.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filterChildren = root.Elements().ToArray();
        if (filterChildren.Length == 0)
        {
            return false;
        }

        var hasOnlyResourceIdPredicates = filterChildren.All(element =>
            string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase));
        if (!hasOnlyResourceIdPredicates)
        {
            if (root.Descendants().Any(element =>
                    string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase)))
            {
                throw new NotSupportedException("Filters that combine ResourceId with other predicates are not yet supported.");
            }

            return false;
        }

        var values = filterChildren
            .Select(element => element.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "rid", StringComparison.OrdinalIgnoreCase))
                ?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        if (values.Length == 0)
        {
            return false;
        }

        normalizedFilter = null;
        resourceIds = string.Join(',', values);
        return true;
    }

    private static SqlFragment CombineSqlFilters(SqlFragment? left, SqlFragment right)
    {
        if (left is null)
        {
            return right;
        }

        return new SqlFragment(
            $"({left.Sql}) AND ({right.Sql})",
            left.Parameters.Concat(right.Parameters).ToArray());
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
            var fieldName = FilterExpressionHelpers.ResolveFieldName(layer, requestedProperty, allowGeometryAlias: true)
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

        if (layer.Name.Equals("Other", StringComparison.Ordinal) &&
            IsWfs10CiteLayerName(layer.Name) &&
            !resolved.Any(existing => existing.Equals("string1", StringComparison.OrdinalIgnoreCase)) &&
            layer.AttributeFields.Any(field => field.Name.Equals("string1", StringComparison.OrdinalIgnoreCase)))
        {
            resolved.Insert(0, "string1");
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

            var fieldName = FilterExpressionHelpers.ResolveFieldName(layer, tokens[0], allowGeometryAlias: false)
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

        var crsDefinition = parts.Length == 5
            ? SpatialReferenceHelpers.TryParseCrsDefinition(parts[4], out var bboxCrs)
                ? bboxCrs
                : throw new ArgumentException($"Unsupported BBOX CRS '{parts[4]}'.")
            : SpatialReferenceHelpers.TryParseCrsDefinition(
                layer.SpatialReference.ToSrid().ToString(CultureInfo.InvariantCulture),
                out var layerCrs)
                ? layerCrs
                : throw new ArgumentException($"Unsupported layer spatial reference '{layer.SpatialReference.ToSrid()}'.");
        var axisOrder = crsDefinition.AxisOrder;
        var bboxCoordinates = parts.Length == 5 ? string.Join(',', parts[..4]) : bbox;

        if (!RasterParsingHelpers.TryParseBoundingBox(
                bboxCoordinates,
                axisOrder,
                crsDefinition.IsGeographic,
                out var minX,
                out var minY,
                out var maxX,
                out var maxY))
        {
            throw new ArgumentException("BBOX contains invalid numeric coordinates or is outside supported CRS bounds.");
        }

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(crsDefinition.Srid);
        Geometry geometry = crsDefinition.IsGeographic && minX > maxX
            ? geometryFactory.CreateMultiPolygon(
                [
                    geometryFactory.CreatePolygon(
                    [
                        new Coordinate(minX, minY),
                        new Coordinate(180.0, minY),
                        new Coordinate(180.0, maxY),
                        new Coordinate(minX, maxY),
                        new Coordinate(minX, minY)
                    ]),
                    geometryFactory.CreatePolygon(
                    [
                        new Coordinate(-180.0, minY),
                        new Coordinate(maxX, minY),
                        new Coordinate(maxX, maxY),
                        new Coordinate(-180.0, maxY),
                        new Coordinate(-180.0, minY)
                    ])
                ])
            : geometryFactory.CreatePolygon(
                [
                    new Coordinate(minX, minY),
                    new Coordinate(maxX, minY),
                    new Coordinate(maxX, maxY),
                    new Coordinate(minX, maxY),
                    new Coordinate(minX, minY)
                ]);

        return new SpatialFilter
        {
            Geometry = BboxWkbWriter.Write(geometry),
            Srid = crsDefinition.Srid,
            SpatialRelationship = SpatialRelationship.Intersects,
            IsSimpleEnvelope = minX <= maxX,
            AllowEnvelopeOnly = minX <= maxX,
            EnvelopeMinX = minX <= maxX ? minX : null,
            EnvelopeMinY = minX <= maxX ? minY : null,
            EnvelopeMaxX = minX <= maxX ? maxX : null,
            EnvelopeMaxY = minX <= maxX ? maxY : null
        };
    }

    private static ResourceIdResolution ParseResourceIds(
        string? resourceId,
        LayerDefinition layer,
        bool enforceTypeMatch,
        bool requireTypeQualifier)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return new ResourceIdResolution(null, false);
        }

        var ids = ImmutableArray.CreateBuilder<long>();
        var sawCandidate = false;
        var localTypeName = BuildTypeLocalName(layer);
        foreach (var rawResourceId in resourceId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            sawCandidate = true;
            var candidate = rawResourceId;
            var prefix = string.Empty;
            var lastDot = candidate.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < candidate.Length - 1)
            {
                prefix = candidate[..lastDot];
                candidate = candidate[(lastDot + 1)..];
            }

            if (prefix.Length > 0 &&
                !FilterExpressionHelpers.NormalizeIdentifier(prefix).Equals(localTypeName, StringComparison.OrdinalIgnoreCase) &&
                !FilterExpressionHelpers.NormalizeIdentifier(prefix).Equals(layer.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (enforceTypeMatch)
                {
                    throw new WfsQueryException(
                        "InvalidParameterValue",
                        $"resourceId '{rawResourceId}' does not match queried feature type '{localTypeName}'.",
                        Wfs20Utilities.ParameterNames.ResourceId);
                }

                continue;
            }

            if (prefix.Length == 0 && requireTypeQualifier)
            {
                throw new WfsQueryException(
                    "InvalidParameterValue",
                    $"resourceId '{rawResourceId}' must be qualified when multiple feature types are requested.",
                    Wfs20Utilities.ParameterNames.ResourceId);
            }

            if (!long.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                if (prefix.Length == 0)
                {
                    continue;
                }

                throw new WfsQueryException(
                    "InvalidParameterValue",
                    $"resourceId '{rawResourceId}' is malformed.",
                    Wfs20Utilities.ParameterNames.ResourceId);
            }

            ids.Add(parsed);
        }

        if (ids.Count > 0)
        {
            return new ResourceIdResolution(ids.ToImmutable(), false);
        }

        return new ResourceIdResolution(null, sawCandidate);
    }

    private static int? ParseSrid(string? srsName)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return null;
        }

        if (srsName.Contains("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return SpatialReference.WGS84.Wkid;
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

    private async ValueTask<int> ResolveRequestedOutputSridAsync(
        LayerDefinition layer,
        string? srsName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(srsName))
        {
            return layer.SpatialReference.ToSrid();
        }

        if (srsName.Contains("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return SpatialReference.WGS84.Wkid;
        }

        if (!SpatialReferenceHelpers.TryParseCrsDefinition(srsName, out var parsedDefinition))
        {
            throw new WfsQueryException(
                "InvalidParameterValue",
                $"Unsupported srsName '{srsName}'.",
                Wfs20Utilities.ParameterNames.SrsName);
        }

        var resolvedDefinition = await _crsRegistry.ResolveAsync(srsName, cancellationToken).ConfigureAwait(false);
        if (resolvedDefinition.HasValue)
        {
            return resolvedDefinition.Value.Srid;
        }

        if (parsedDefinition.Srid == SpatialReference.WGS84.Wkid || parsedDefinition.Srid == 3857)
        {
            return parsedDefinition.Srid;
        }

        throw new WfsQueryException(
            "InvalidParameterValue",
            $"Unsupported srsName '{srsName}'.",
            Wfs20Utilities.ParameterNames.SrsName);
    }

    private async ValueTask<AxisOrder> ResolveOutputAxisOrderAsync(
        string? srsName,
        int outputSrid,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(srsName) &&
            srsName.Contains("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return AxisOrder.EastNorth;
        }

        var definition = await _crsRegistry.ResolveBySridAsync(outputSrid, cancellationToken).ConfigureAwait(false);
        if (definition.HasValue)
        {
            return definition.Value.AxisOrder;
        }

        return SpatialReference.Create(outputSrid).IsGeographic
            ? AxisOrder.NorthEast
            : AxisOrder.EastNorth;
    }

    private static Parameter CreateParameter(string name, bool allowAnyValue)
    {
        return new Parameter
        {
            Name = name,
            AnyValue = allowAnyValue ? new object() : null
        };
    }

    private static FeatureCollectionResponseMetadata BuildFeatureCollectionResponseMetadata(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string? outputFormat,
        string? count,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        int offset,
        int pageSize,
        long totalMatched,
        int returnedCount)
    {
        var pagingResultType = string.Equals(resultType, "hits", StringComparison.OrdinalIgnoreCase)
            ? null
            : resultType;

        return new FeatureCollectionResponseMetadata(
            BuildFeatureCollectionSchemaLocation(wfsUrl, selectedTypes),
            BuildPagingLinks(
                wfsUrl,
                selectedTypes,
                outputFormat,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                pagingResultType,
                offset,
                pageSize,
                totalMatched,
                returnedCount));
    }

    private static string BuildFeatureCollectionSchemaLocation(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes)
    {
        var values = new List<string>
        {
            Wfs20Utilities.WfsNamespace,
            "http://schemas.opengis.net/wfs/2.0/wfs.xsd"
        };

        if (selectedTypes.Count > 0)
        {
            var typeNames = string.Join(
                ",",
                selectedTypes.Select(descriptor => descriptor.LocalName));
            values.Add(FeatureNamespaceUri);
            values.Add(
                $"{wfsUrl}?SERVICE=WFS&VERSION={Wfs20Utilities.Version}&REQUEST=DescribeFeatureType&TYPENAMES={Uri.EscapeDataString(typeNames)}");
        }

        return string.Join(" ", values);
    }

    private static PagingLinks BuildPagingLinks(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string? outputFormat,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        int offset,
        int pageSize,
        long totalMatched,
        int returnedCount)
    {
        if (pageSize <= 0 || selectedTypes.Count == 0)
        {
            return new PagingLinks(null, null);
        }

        var next = offset + pageSize < totalMatched
            ? BuildGetFeaturePagingLink(
                wfsUrl,
                selectedTypes,
                outputFormat,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                resultType,
                offset + pageSize,
                pageSize)
            : null;
        var previous = offset > 0
            ? BuildGetFeaturePagingLink(
                wfsUrl,
                selectedTypes,
                outputFormat,
                sortBy,
                bbox,
                filter,
                resourceId,
                propertyName,
                srsName,
                resultType,
                Math.Max(offset - pageSize, 0),
                pageSize)
            : null;

        return new PagingLinks(next, previous);
    }

    private static string BuildGetFeaturePagingLink(
        string wfsUrl,
        IReadOnlyList<WfsFeatureTypeDescriptor> selectedTypes,
        string? outputFormat,
        string? sortBy,
        string? bbox,
        string? filter,
        string? resourceId,
        string? propertyName,
        string? srsName,
        string? resultType,
        int offset,
        int count)
    {
        var queryParts = new List<string>
        {
            $"SERVICE={Uri.EscapeDataString(Wfs20Utilities.ServiceType)}",
            $"VERSION={Uri.EscapeDataString(Wfs20Utilities.Version)}",
            $"REQUEST={Uri.EscapeDataString(Wfs20Utilities.Operations.GetFeature)}",
            $"TYPENAMES={Uri.EscapeDataString(string.Join(',', selectedTypes.Select(descriptor => descriptor.LocalName)))}",
            $"COUNT={count.ToString(CultureInfo.InvariantCulture)}",
            $"STARTINDEX={offset.ToString(CultureInfo.InvariantCulture)}"
        };

        AppendQueryPart(queryParts, "OUTPUTFORMAT", outputFormat);
        AppendQueryPart(queryParts, "SORTBY", sortBy);
        AppendQueryPart(queryParts, "BBOX", bbox);
        AppendQueryPart(queryParts, "FILTER", filter);
        AppendQueryPart(queryParts, "RESOURCEID", resourceId);
        AppendQueryPart(queryParts, "PROPERTYNAME", propertyName);
        AppendQueryPart(queryParts, "SRSNAME", srsName);
        if (!string.IsNullOrWhiteSpace(resultType) &&
            !string.Equals(resultType, "results", StringComparison.OrdinalIgnoreCase))
        {
            AppendQueryPart(queryParts, "RESULTTYPE", resultType);
        }

        return $"{wfsUrl}?{string.Join("&", queryParts)}";
    }

    private static void AppendQueryPart(List<string> queryParts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            queryParts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildGmlResultAsync(
        LayerQueryPlanSet planSet,
        string? schemaLocation,
        string? next,
        string? previous,
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
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", planSet.TotalMatched.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberReturned", returnedCount.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(schemaLocation))
            {
                writer.WriteAttributeString("xsi", "schemaLocation", Wfs20Utilities.XsiNamespace, schemaLocation);
            }

            WritePagingAttributes(writer, next, previous);

            foreach (var queryResult in queryResults)
            {
                foreach (var feature in queryResult.Features)
                {
                    WriteFeature(writer, queryResult.Plan, feature, includeMemberWrapper: true);
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
            var axisOrder = plan.Query.OutputAxisOrder ?? AxisOrder.EastNorth;
            var projectedProperties = GetProjectedProperties(plan.Query);

            if (geoJsonFeatureStore is not null)
            {
                var result = await geoJsonFeatureStore.QueryGeoJsonAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
                foreach (var feature in result.Items)
                {
                    features.Add(OgcGeoJsonFeatureBuilder.Create(
                        feature,
                        plan.Descriptor.Layer,
                        axisOrder,
                        _geometryServices,
                        projectedProperties,
                        featureId => BuildFeatureId(plan.Descriptor, featureId)));
                }

                continue;
            }

            var fallbackResult = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            foreach (var feature in fallbackResult.Items)
            {
                features.Add(OgcGeoJsonFeatureBuilder.Create(
                    feature,
                    plan.Descriptor.Layer,
                    axisOrder,
                    _geometryServices,
                    projectedProperties,
                    featureId => BuildFeatureId(plan.Descriptor, featureId)));
            }
        }

        var payload = OgcGeoJsonFeatureBuilder.CreateCollection(features, planSet.TotalMatched);

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
            var axisOrder = query.OutputAxisOrder ?? AxisOrder.EastNorth;

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
                        ? ConvertFieldValueToInvariantString(value, field)
                        : null;
                }

                if (descriptor.Layer.HasGeometry)
                {
                    row["geometry"] = SerializeGeometryAsJson(feature.Geometry, axisOrder);
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
            var projectedProperties = GetProjectedProperties(query);
            foreach (var feature in result.Items)
            {
                features.Add(OgcGeoJsonFeatureBuilder.Create(
                    feature,
                    descriptor.Layer,
                    AxisOrder.EastNorth,
                    _geometryServices,
                    projectedProperties,
                    featureId => BuildFeatureId(descriptor, featureId)));
            }
        }
        else if (_featureReader is IPagedFeatureReader pagedFeatureReader)
        {
            var result = await pagedFeatureReader.QueryPageAsync(descriptor.Layer.Id, query, cancellationToken);
            totalCount = result.TotalCount;
            var projectedProperties = GetProjectedProperties(query);
            foreach (var feature in result.Items)
            {
                features.Add(OgcGeoJsonFeatureBuilder.Create(
                    feature,
                    descriptor.Layer,
                    AxisOrder.EastNorth,
                    _geometryServices,
                    projectedProperties,
                    featureId => BuildFeatureId(descriptor, featureId)));
            }
        }
        else
        {
            throw new InvalidOperationException("Paged feature queries are not supported by the configured feature store.");
        }

        var payload = OgcGeoJsonFeatureBuilder.CreateCollection(features, totalCount);

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

            var axisOrder = plan.Query.OutputAxisOrder ?? AxisOrder.EastNorth;
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
                        ? ConvertFieldValueToInvariantString(value, field)
                        : null;
                }

                if (geometryRequested)
                {
                    row["geometry"] = SerializeGeometryAsJson(feature.Geometry, axisOrder);
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
                        writer.WriteString(ConvertValueReferenceToInvariantString(value, queryResult.Plan.ValueReference));
                    }
                    writer.WriteEndElement();
                }
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return (Results.Content(xml, MediaTypes.Gml, Encoding.UTF8), returnedCount);
    }

    private async Task<(IResult Result, int ReturnedCount)> BuildValueJsonResultAsync(
        LayerValuePlanSet planSet,
        string normalizedFormat,
        CancellationToken cancellationToken)
    {
        var features = new List<GeoJsonFeature>();

        foreach (var plan in planSet.Plans)
        {
            var axisOrder = plan.Query.OutputAxisOrder ?? AxisOrder.EastNorth;
            var featureResult = await _featureReader.QueryAsync(plan.Descriptor.Layer.Id, plan.Query, cancellationToken);
            foreach (var feature in featureResult.Items)
            {
                var featureId = BuildFeatureId(plan.Descriptor, feature.Id);
                var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                SimpleGeoJsonGeometry? geometry = null;

                if (plan.ValueReference.IsGeometry)
                {
                    geometry = feature.Geometry is null
                        ? null
                        : _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
                }
                else if (plan.ValueReference.IsFeatureId)
                {
                    properties["id"] = featureId;
                }
                else
                {
                    properties[plan.ValueReference.CanonicalName] = ExtractValue(feature, plan.ValueReference);
                }

                features.Add(OgcGeoJsonFeatureBuilder.Create(featureId, properties, geometry));
            }
        }

        var payload = OgcGeoJsonFeatureBuilder.CreateCollection(features, planSet.TotalMatched);

        var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
            ? MediaTypes.Json
            : MediaTypes.GeoJson;

        return (Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType), features.Count);
    }

    private static void WriteFeature(XmlWriter writer, LayerQueryPlan plan, GmlFeature feature, bool includeMemberWrapper)
    {
        if (includeMemberWrapper)
        {
            writer.WriteStartElement("wfs", "member", Wfs20Utilities.WfsNamespace);
        }

        writer.WriteStartElement(FeatureNamespacePrefix, plan.Descriptor.LocalName, FeatureNamespaceUri);
        if (!includeMemberWrapper)
        {
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
        }

        writer.WriteAttributeString("gml", "id", Wfs20Utilities.GmlNamespace, BuildFeatureId(plan.Descriptor, feature.Id));

        // Keep collection members aligned to the application schema; duplicating gml:name/description
        // alongside matching feature properties causes GDAL/OGR to surface list-valued fields.
        if (!includeMemberWrapper &&
            TryGetGmlDescriptionValue(plan.Descriptor.Layer, feature.Attributes, out var gmlDescription))
        {
            writer.WriteElementString("gml", "description", Wfs20Utilities.GmlNamespace, gmlDescription);
        }

        if (TryGetGmlIdentifierValue(feature.Attributes, out var gmlIdentifier))
        {
            writer.WriteElementString("gml", "identifier", Wfs20Utilities.GmlNamespace, gmlIdentifier);
        }

        if (!includeMemberWrapper &&
            TryGetGmlNameValue(plan.Descriptor.Layer, feature.Attributes, out var gmlName))
        {
            writer.WriteElementString("gml", "name", Wfs20Utilities.GmlNamespace, gmlName);
        }

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
                if (field.Nullable)
                {
                    writer.WriteStartElement(
                        FeatureNamespacePrefix,
                        XmlConvert.EncodeLocalName(field.Name),
                        FeatureNamespaceUri);
                    writer.WriteAttributeString("xsi", "nil", Wfs20Utilities.XsiNamespace, XmlConvert.ToString(true));
                    writer.WriteEndElement();
                }

                continue;
            }

            writer.WriteStartElement(
                FeatureNamespacePrefix,
                XmlConvert.EncodeLocalName(field.Name),
                FeatureNamespaceUri);
            writer.WriteString(ConvertFieldValueToInvariantString(value, field));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        if (includeMemberWrapper)
        {
            writer.WriteEndElement();
        }
    }

    private static bool TryGetGmlNameValue(
        LayerDefinition layer,
        ImmutableDictionary<string, object?> attributes,
        out string? gmlName)
    {
        return TryGetTransactionMappedValue(
            layer,
            attributes,
            "name",
            ValidationExtensions.WfsGmlNameAttributeName,
            out gmlName);
    }

    private static bool TryGetGmlDescriptionValue(
        LayerDefinition layer,
        ImmutableDictionary<string, object?> attributes,
        out string? gmlDescription)
    {
        return TryGetTransactionMappedValue(
            layer,
            attributes,
            "description",
            ValidationExtensions.WfsGmlDescriptionAttributeName,
            out gmlDescription);
    }

    private static bool TryGetGmlIdentifierValue(
        ImmutableDictionary<string, object?> attributes,
        out string? gmlIdentifier)
    {
        return TryGetReservedTransactionValue(
            attributes,
            ValidationExtensions.WfsGmlIdentifierAttributeName,
            out gmlIdentifier);
    }

    private static bool TryGetTransactionMappedValue(
        LayerDefinition layer,
        ImmutableDictionary<string, object?> attributes,
        string fieldName,
        string reservedAttributeName,
        out string? valueText)
    {
        var field = layer.AttributeFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field != null &&
            attributes.TryGetValue(field.Name, out var value) &&
            value is not null)
        {
            valueText = ConvertFieldValueToInvariantString(value, field);
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                return true;
            }
        }

        return TryGetReservedTransactionValue(attributes, reservedAttributeName, out valueText);
    }

    private static bool TryGetReservedTransactionValue(
        ImmutableDictionary<string, object?> attributes,
        string reservedAttributeName,
        out string? valueText)
    {
        if (attributes.TryGetValue(reservedAttributeName, out var value) &&
            value is not null)
        {
            valueText = ConvertToInvariantString(value);
            return !string.IsNullOrWhiteSpace(valueText);
        }

        valueText = null;
        return false;
    }

    private static string BuildFeatureId(WfsFeatureTypeDescriptor descriptor, long featureId)
        => $"{descriptor.LocalName}.{featureId.ToString(CultureInfo.InvariantCulture)}";

    private static ImmutableHashSet<string>? GetProjectedProperties(FeatureQuery query)
    {
        if (query.OutFields is not { } outFields)
        {
            return null;
        }

        return outFields.IsDefaultOrEmpty
            ? ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase)
            : outFields.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? SerializeGeometryAsJson(byte[]? geometry, AxisOrder axisOrder)
    {
        var simpleGeometry = _geometryServices.ConvertWkbToSimpleGeometry(geometry, axisOrder);
        return simpleGeometry is null
            ? null
            : JsonSerializer.Serialize(simpleGeometry, OgcJsonContext.Default.SimpleGeoJsonGeometry);
    }

    private static IEnumerable<FieldDefinition> GetProjectedAttributeFields(LayerDefinition layer, FeatureQuery query)
    {
        if (query.OutFields is not { } outFields)
        {
            return layer.VisibleAttributeFields;
        }

        if (outFields.IsDefaultOrEmpty)
        {
            return Array.Empty<FieldDefinition>();
        }

        return layer.VisibleAttributeFields.Where(field =>
            outFields.Any(candidate => candidate.Equals(field.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static ValueReferenceResolution ResolveValueReference(LayerDefinition layer, string valueReference)
    {
        var resolvedName = FilterExpressionHelpers.ResolveFieldName(layer, valueReference, allowGeometryAlias: true)
            ?? throw new ArgumentException($"Unknown valueReference '{valueReference}' for feature type '{layer.Name}'.");

        var isGeometry = layer.GeometryField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true;
        var isFeatureId = layer.PrimaryKeyField?.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase) == true ||
                          resolvedName.Equals("objectid", StringComparison.OrdinalIgnoreCase);
        var field = layer.AttributeFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase));

        return new ValueReferenceResolution(valueReference, resolvedName, isGeometry, isFeatureId, field);
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

    private static bool IsSupportedValueOutputFormat(string format)
    {
        return string.Equals(format, Wfs20Utilities.OutputFormats.Gml32, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Gml32Simplified, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult CreateEmptyFeatureCollectionResult(
        string normalizedFormat,
        string? schemaLocation = null,
        string? next = null,
        string? previous = null)
    {
        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase))
        {
            var payload = OgcGeoJsonFeatureBuilder.CreateCollection(Array.Empty<GeoJsonFeature>(), 0);

            var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
                ? MediaTypes.Json
                : MediaTypes.GeoJson;

            return Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType);
        }

        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Csv, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Content("typeName,id\n", MediaTypes.Csv, Encoding.UTF8);
        }

        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "FeatureCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", "0");
            writer.WriteAttributeString("numberReturned", "0");
            if (!string.IsNullOrWhiteSpace(schemaLocation))
            {
                writer.WriteAttributeString("xsi", "schemaLocation", Wfs20Utilities.XsiNamespace, schemaLocation);
            }

            WritePagingAttributes(writer, next, previous);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private static IResult CreateHitsFeatureCollectionResult(
        long totalMatched,
        string? schemaLocation = null,
        string? next = null,
        string? previous = null)
    {
        var xml = WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "FeatureCollection", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, Wfs20Utilities.GmlNamespace);
            writer.WriteAttributeString("xmlns", FeatureNamespacePrefix, null, FeatureNamespaceUri);
            writer.WriteAttributeString("xmlns", "xsi", null, Wfs20Utilities.XsiNamespace);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberMatched", totalMatched.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("numberReturned", "0");
            if (!string.IsNullOrWhiteSpace(schemaLocation))
            {
                writer.WriteAttributeString("xsi", "schemaLocation", Wfs20Utilities.XsiNamespace, schemaLocation);
            }

            WritePagingAttributes(writer, next, previous);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        });

        return Results.Content(xml, MediaTypes.Gml, Encoding.UTF8);
    }

    private static IResult CreateEmptyValueCollectionResult(string normalizedFormat)
    {
        if (string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.GeoJson, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase))
        {
            var payload = OgcGeoJsonFeatureBuilder.CreateCollection(Array.Empty<GeoJsonFeature>(), 0);

            var contentType = string.Equals(normalizedFormat, Wfs20Utilities.OutputFormats.Json, StringComparison.OrdinalIgnoreCase)
                ? MediaTypes.Json
                : MediaTypes.GeoJson;

            return Results.Json(payload, OgcJsonContext.Default.FeatureCollection, contentType: contentType);
        }

        return CreateEmptyValueCollectionXmlResult();
    }

    private static IResult CreateEmptyValueCollectionXmlResult()
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
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false
        };

        using var stream = new MemoryStream();
        using var writer = XmlWriter.Create(stream, settings);
        writeAction(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePagingAttributes(XmlWriter writer, string? next, string? previous)
    {
        if (!string.IsNullOrWhiteSpace(next))
        {
            writer.WriteAttributeString("next", next);
        }

        if (!string.IsNullOrWhiteSpace(previous))
        {
            writer.WriteAttributeString("previous", previous);
        }
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

    private static bool IsAnonymousWriteAllowed(
        HttpContext context,
        LayerDefinition layer,
        ServiceDefinition? service)
    {
        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        var anonymousPrincipal = new ClaimsPrincipal(new ClaimsIdentity());
        var decision = evaluator.Evaluate(
            anonymousPrincipal,
            layer.Metadata?.AccessPolicy,
            service?.Metadata?.AccessPolicy,
            AccessScope.Write);

        return decision.IsAllowed;
    }

    private static string[] ParseQualifiedList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            DateTimeOffset dateTimeOffset => FormatXmlDateTimeOffset(dateTimeOffset),
            DateTime dateTime => FormatXmlDateTime(dateTime),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => ConvertStructuredValueToJson(value)
        };
    }

    private static string ConvertValueReferenceToInvariantString(object? value, ValueReferenceResolution valueReference)
        => valueReference.Field is { } field
            ? ConvertFieldValueToInvariantString(value, field)
            : ConvertToInvariantString(value);

    private static string ConvertFieldValueToInvariantString(object? value, FieldDefinition field)
    {
        if (value is string text)
        {
            return field.Type switch
            {
                FieldType.DateTime when DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateTimeOffset) => FormatXmlDateTimeOffset(dateTimeOffset),
                FieldType.Date when DateOnly.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dateOnly) => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FieldType.Time when TimeOnly.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timeOnly) => timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                _ => text
            };
        }

        return ConvertToInvariantString(value);
    }

    private static string FormatXmlDateTime(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return FormatXmlDateTimeOffset(new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero));
    }

    private static string FormatXmlDateTimeOffset(DateTimeOffset value)
    {
        var format = value.Millisecond == 0
            ? "yyyy-MM-dd'T'HH:mm:sszzz"
            : "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
        return value.ToString(format, CultureInfo.InvariantCulture);
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
        string LocalName,
        string NamespacePrefix,
        string NamespaceUri);

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

    private readonly record struct ResourceIdResolution(
        ImmutableArray<long>? ObjectIds,
        bool MatchesNothing);

    private readonly record struct PagingLinks(
        string? Next,
        string? Previous);

    private readonly record struct FeatureCollectionResponseMetadata(
        string SchemaLocation,
        PagingLinks PagingLinks);

    private readonly record struct PagedGetFeatureResult(
        IResult Result,
        int ReturnedCount,
        string NumberMatchedSummary);

    private readonly record struct ValueReferenceResolution(
        string RequestedName,
        string CanonicalName,
        bool IsGeometry,
        bool IsFeatureId,
        FieldDefinition? Field);

    private sealed class WfsQueryException(
        string exceptionCode,
        string message,
        string? locator = null) : Exception(message)
    {
        public string ExceptionCode { get; } = exceptionCode;

        public string? Locator { get; } = locator;
    }
}
