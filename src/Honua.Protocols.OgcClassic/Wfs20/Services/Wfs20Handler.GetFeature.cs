// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// WFS 2.0 GetFeature operation handling. Split from <see cref="Wfs20Handler"/> as part of audit #1144.
/// </summary>
internal sealed partial class Wfs20Handler
{
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
                    descriptor,
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
                    featureType,
                    xmlQuery.PropertyName,
                    xmlQuery.SortBy ?? sortBy,
                    bbox,
                    xmlQuery.Filter,
                    resourceId,
                    xmlQuery.SrsName ?? fallbackSrsName,
                    enforceResourceIdTypeMatch: true,
                    requireResourceIdQualifier: selectedTypes.Length > 1,
                    cancellationToken: cancellationToken);

                var layerMatched = await _featureReader.CountAsync(featureType.StorageLayerId, query, cancellationToken);
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
}
