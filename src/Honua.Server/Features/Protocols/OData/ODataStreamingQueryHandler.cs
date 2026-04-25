// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.Server.Features.Protocols.OData.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.OData;

/// <summary>
/// Handler for OData streaming query operations for large-scale feature queries.
/// Uses streaming JSON writing to reduce memory pressure for large result sets.
/// </summary>
internal sealed partial class ODataStreamingQueryHandler(
    ODataQueryDependencies dependencies,
    IStreamingFeatureStore streamingFeatureStore,
    ILogger<ODataStreamingQueryHandler> logger)
{
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly IGeometryService _geometryService = dependencies.GeometryService;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly IStreamingFeatureStore _streamingFeatureStore = streamingFeatureStore ?? throw new ArgumentNullException(nameof(streamingFeatureStore));
    private readonly ODataValidationService _validationService = dependencies.ValidationService;
    private readonly ODataQuerySearchService _querySearchService = dependencies.QuerySearchService;
    private readonly ILogger<ODataStreamingQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private const int StreamingThreshold = 1000;
    private const int FlushInterval = 32;
    private const string InvalidLayerIdFilterMessage = "LayerId filter must be a valid OData expression that resolves to a single layer.";

    /// <summary>
    /// Handles OData features collection request with streaming for large result sets
    /// </summary>
    public async Task<IResult> HandleGetFeaturesAsync(
        HttpContext context,
        int? layerId,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] string? top = null,
        [FromQuery(Name = "$skip")] string? skip = null,
        [FromQuery(Name = "$skiptoken")] string? skiptoken = null,
        [FromQuery(Name = "$count")] string? count = null,
        [FromQuery(Name = "$expand")] string? expand = null,
        [FromQuery(Name = "$compute")] string? compute = null,
        [FromQuery(Name = "$search")] string? search = null,
        [FromQuery(Name = "$apply")] string? apply = null,
        [FromQuery(Name = "$deltatoken")] string? deltatoken = null,
        [FromQuery(Name = "$format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;
        try
        {
            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.Features);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(select))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$select contains an empty field expression.");
            }

            if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(expand))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$expand contains an empty navigation expression.");
            }

            var applyTrackChangesPreference = ODataUtilityService.HasTrackChangesPreference(
                context.Request.Headers["Prefer"].ToString());
            var trackChangesRequested = ODataUtilityService.RequestsTrackChanges(context.Request);

            DateTimeOffset? deltaSince = null;
            int? deltaLayerId = null;
            ODataDeltaService.DeltaQueryState? deltaState = null;
            if (!string.IsNullOrWhiteSpace(deltatoken))
            {
                var deltaValidation = ValidateDeltaRequestQuery(context.Request.Query.Keys);
                if (deltaValidation != null)
                {
                    return ODataUtilityService.CreateODataError(
                        context,
                        "InvalidQueryOption",
                        deltaValidation);
                }

                if (!ODataDeltaService.TryDecode(deltatoken, out deltaState, out var deltaError))
                {
                    return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", deltaError!);
                }

                deltaSince = deltaState.Timestamp;
                deltaLayerId = deltaState.LayerId;
                filter = deltaState.Filter;
                select = deltaState.Select;
                orderby = deltaState.OrderBy;
                expand = deltaState.Expand;
                compute = deltaState.Compute;
                format = deltaState.Format;
                count = deltaState.Count?.ToString()?.ToLowerInvariant();
                trackChangesRequested = true;
            }

            if (trackChangesRequested &&
                (!string.IsNullOrWhiteSpace(apply) || !string.IsNullOrWhiteSpace(search)))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "Change tracking is not supported with $search or $apply.");
            }

            var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
            if (formatValidation != null)
            {
                return formatValidation;
            }

            var pagingError = ODataRequestValidation.TryGetPagingValues(
                context,
                _validationService,
                top,
                skip,
                skiptoken,
                count,
                filter,
                orderby,
                out var pagination,
                out var topValue,
                out var skipValue,
                out var countValue,
                out var useSkipToken);
            if (pagingError != null)
            {
                return pagingError;
            }

            if (!ODataComputeService.TryParse(compute, out var computeExpressions, out var computeError))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    computeError ?? "Invalid $compute expression.");
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

            if (!string.IsNullOrWhiteSpace(apply) && !string.IsNullOrWhiteSpace(search))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$apply cannot be combined with $search.");
            }

            if (!string.IsNullOrWhiteSpace(apply))
            {
                if (!computeExpressions.IsDefaultOrEmpty)
                {
                    return ODataUtilityService.CreateODataError(
                        context,
                        "InvalidQueryOption",
                        "$compute cannot be combined with $apply.");
                }

                var resolvedLayerId = layerId;
                if (!resolvedLayerId.HasValue)
                {
                    if (!TryResolveLayerIdFromFilter(filter, out var layerResolution))
                    {
                        return ODataUtilityService.CreateODataError(
                            context,
                            "InvalidQueryOption",
                            layerResolution.ErrorMessage ?? "LayerId filter is required for $apply.",
                            400);
                    }

                    resolvedLayerId = layerResolution.LayerId;
                }

                var applyLayerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                    context,
                    resolvedLayerId.Value,
                    LayerValidationHelpers.ValidationProtocol.OData,
                    cancellationToken: effectiveToken);
                if (!applyLayerValidation.IsValid)
                {
                    return applyLayerValidation.ErrorResult!;
                }

                var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
                var result = await _querySearchService.HandleApplyAsync(
                    resolvedLayerId.Value,
                    apply,
                    filter,
                    baseUrl,
                    effectiveToken);
                if (!ODataUtilityService.ShouldIncludeContext(context.Request, format))
                {
                    result = new ODataAggregationResult
                    {
                        Context = null,
                        Value = result.Value
                    };
                }

                ODataUtilityService.SetODataHeaders(context);
                return Results.Json(result, ODataJsonContext.Default.ODataAggregationResult,
                    contentType: ODataUtilityService.GetODataContentType(context.Request, format));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                if (!computeExpressions.IsDefaultOrEmpty)
                {
                    return ODataUtilityService.CreateODataError(
                        context,
                        "InvalidQueryOption",
                        "$compute cannot be combined with $search.");
                }

                var resolvedLayerId = layerId;
                if (!resolvedLayerId.HasValue)
                {
                    if (!TryResolveLayerIdFromFilter(filter, out var layerResolution))
                    {
                        return ODataUtilityService.CreateODataError(
                            context,
                            "InvalidQueryOption",
                            layerResolution.ErrorMessage ?? "LayerId filter is required for $search.",
                            400);
                    }

                    resolvedLayerId = layerResolution.LayerId;
                }

                var searchLayerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                    context,
                    resolvedLayerId.Value,
                    LayerValidationHelpers.ValidationProtocol.OData,
                    cancellationToken: effectiveToken);
                if (!searchLayerValidation.IsValid)
                {
                    return searchLayerValidation.ErrorResult!;
                }

                var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
                var result = await _querySearchService.HandleSearchAsync(
                    resolvedLayerId.Value,
                    search,
                    baseUrl,
                    topValue,
                    skipValue,
                    countValue,
                    filter,
                    orderby,
                    select,
                    expand,
                    context,
                    effectiveToken);
                if (!ODataUtilityService.ShouldIncludeContext(context.Request, format))
                {
                    result = new ODataSearchResult
                    {
                        Context = null,
                        Count = result.Count,
                        Value = result.Value
                    };
                }

                ODataUtilityService.SetODataHeaders(context);
                return Results.Json(result, ODataJsonContext.Default.ODataSearchResult,
                    contentType: ODataUtilityService.GetODataContentType(context.Request, format));
            }

            if (!layerId.HasValue)
            {
                if (!TryResolveLayerIdFromFilter(filter, out var layerResolution))
                {
                    return ODataUtilityService.CreateODataError(
                        context,
                        "InvalidQueryOption",
                        layerResolution.ErrorMessage ?? "LayerId filter is required for Features collection.",
                        400);
                }

                layerId = layerResolution.LayerId;
            }

            // Validate that the delta token's layer ID matches the requested layer
            if (deltaLayerId.HasValue && layerId.HasValue && deltaLayerId.Value != layerId.Value)
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    $"$deltatoken was issued for layer {deltaLayerId.Value} but the request targets layer {layerId.Value}.");
            }

            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId.Value,
                LayerValidationHelpers.ValidationProtocol.OData,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var deltaDefinition = deltaState ?? new ODataDeltaService.DeltaQueryState
            {
                Timestamp = DateTimeOffset.UtcNow,
                LayerId = layerId.Value,
                Filter = filter,
                Select = select,
                OrderBy = orderby,
                Expand = expand,
                Compute = compute,
                Format = format,
                Count = countValue
            };
            var effectiveFilter = deltaSince.HasValue
                ? ODataDeltaService.BuildDeltaFilter(filter, deltaSince.Value)
                : filter;

            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.Value.ToString(CultureInfo.InvariantCulture));

            // Build feature query using query service
            var (featureQuery, queryError) = await _querySearchService.BuildFeatureQueryAsync(
                effectiveFilter,
                orderby,
                pagination.Limit,
                pagination.Offset,
                layer,
                select,
                expand,
                countValue,
                compute,
                format,
                effectiveToken);

            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            // Determine if we should use streaming
            bool useStreaming = pagination.Limit > StreamingThreshold && string.IsNullOrWhiteSpace(expand);

            if (!useStreaming)
            {
                // For small result sets or when $expand is used, delegate to non-streaming handler
                var queryHandler = context.RequestServices.GetRequiredService<ODataQueryHandler>();
                if (applyTrackChangesPreference)
                {
                    ODataUtilityService.ApplyTrackChangesPreference(context);
                }

                return await queryHandler.HandleGetFeaturesNonStreamingAsync(
                    context,
                    layerId.Value,
                    filter,
                    select,
                    orderby,
                    topValue,
                    skipValue,
                    countValue,
                    expand,
                    useSkipToken,
                    compute,
                    format,
                    deltaSince,
                    trackChangesRequested,
                    deltatoken,
                    deltaDefinition,
                    cancellationToken);
            }

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                HonuaTelemetry.Protocols.OData,
                layerId.Value.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);

            long? totalCount = null;
            var streamQuery = featureQuery;
            if (countValue == true)
            {
                totalCount = await _featureReader.CountAsync(layerId.Value, featureQuery, effectiveToken);
            }
            else if (streamQuery.Limit.HasValue && streamQuery.Limit.Value < int.MaxValue)
            {
                // Fetch one extra row to compute @odata.nextLink without executing COUNT(*).
                streamQuery = streamQuery with { Limit = streamQuery.Limit.Value + 1 };
            }

            var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
                _crsRegistry,
                layer.SpatialReference.ToSrid(),
                effectiveToken);

            // Set up streaming response
            context.Response.ContentType = ODataUtilityService.GetODataContentType(context.Request, format);
            context.Response.Headers["Transfer-Encoding"] = "chunked";
            ODataUtilityService.SetODataHeaders(context);
            if (applyTrackChangesPreference)
            {
                ODataUtilityService.ApplyTrackChangesPreference(context);
            }

            var selectedFields = ODataUtilityService.ParseSelect(select);

            // Stream the OData response
            await StreamODataFeaturesAsync(
                (IAsyncEnumerable<Feature>)_streamingFeatureStore.StreamFeaturesAsync(layerId.Value, streamQuery, effectiveToken),
                context,
                layerId.Value,
                layer.SpatialReference.ToSrid(),
                axisOrder,
                pagination,
                selectedFields,
                filter,
                select,
                orderby,
                countValue,
                expand,
                useSkipToken,
                compute,
                format,
                trackChangesRequested,
                deltatoken,
                deltaDefinition,
                totalCount,
                ODataUtilityService.ShouldIncludeContext(context.Request, format),
                computeExpressions,
                _geometryService,
                effectiveToken);

            HonuaTelemetry.SetSuccess(featureActivity);
            return Results.Empty;
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidFeaturesQuery(_logger, layerId ?? 0, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            HonuaTelemetry.RecordException(featureActivity, ex);
            if (context.Response.HasStarted)
            {
                return Results.Empty;
            }

            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.FeaturesQueryFailed(_logger, layerId ?? 0, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            if (context.Response.HasStarted)
            {
                return Results.Empty;
            }

            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    /// <summary>
    /// Streams OData features response to reduce memory pressure
    /// </summary>
    private static async Task StreamODataFeaturesAsync(
        IAsyncEnumerable<Feature> features,
        HttpContext context,
        int layerId,
        int? layerSrid,
        AxisOrder axisOrder,
        PaginationValues pagination,
        HashSet<string>? selectedFields,
        string? filter,
        string? select,
        string? orderby,
        bool? count,
        string? expand,
        bool useSkipToken,
        string? compute,
        string? format,
        bool trackChangesRequested,
        string? deltatoken,
        ODataDeltaService.DeltaQueryState deltaState,
        long? totalCount,
        bool includeContext,
        System.Collections.Immutable.ImmutableArray<ODataComputeExpression> computeExpressions,
        IGeometryService geometryService,
        CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(context.Response.BodyWriter, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        // Start OData response
        writer.WriteStartObject();

        if (count == true && totalCount.HasValue)
        {
            writer.WriteNumber("@odata.count", totalCount.Value);
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        if (includeContext)
        {
            writer.WriteString("@odata.context", ODataUtilityService.BuildContextUrl(baseUrl, "Features", select: select, expand: expand));
        }

        // Start value array
        writer.WriteStartArray("value");

        var streamedCount = 0;
        var hasMoreResults = false;
        var featuresSinceFlush = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            if (streamedCount >= pagination.Limit)
            {
                hasMoreResults = true;
                break;
            }

            await WriteODataFeatureAsync(
                writer,
                feature,
                layerId,
                layerSrid,
                axisOrder,
                selectedFields,
                computeExpressions,
                geometryService,
                cancellationToken);
            streamedCount++;

            if (++featuresSinceFlush >= FlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                featuresSinceFlush = 0;
            }
        }

        // End value array
        writer.WriteEndArray();

        var shouldPaginate = totalCount.HasValue
            ? ODataUtilityService.ShouldPaginate(streamedCount, pagination.Offset, totalCount.Value, pagination.Limit)
            : hasMoreResults;

        if (shouldPaginate)
        {
            var nextSkip = ODataUtilityService.CalculateNextSkip(pagination.Offset, pagination.Limit);
            var nextLink = !string.IsNullOrWhiteSpace(deltatoken)
                ? ODataUtilityService.GenerateDeltaNextLink(
                    context.Request,
                    deltatoken,
                    nextSkip,
                    pagination.Limit,
                    useSkipToken,
                    deltaState.Filter,
                    deltaState.OrderBy)
                : ODataUtilityService.GenerateNextLink(
                    context.Request,
                    nextSkip,
                    pagination.Limit,
                    filter,
                    select,
                    orderby,
                    count,
                    expand,
                    useSkipToken,
                    compute,
                    format,
                    trackChangesRequested);
            writer.WriteString("@odata.nextLink", nextLink);
        }
        else if (trackChangesRequested)
        {
            var deltaLink = ODataUtilityService.GenerateDeltaLink(
                context.Request,
                deltaState with
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    LayerId = layerId,
                    Count = count
                });
            writer.WriteString("@odata.deltaLink", deltaLink);
        }

        // End OData response
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
    }

    private static string? ValidateDeltaRequestQuery(IEnumerable<string> queryKeys)
    {
        foreach (var key in queryKeys)
        {
            if (key.Equals("$deltatoken", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("$top", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("$skip", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("$skiptoken", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return "Delta links are opaque. Do not append additional query options.";
        }

        return null;
    }

    /// <summary>
    /// Writes a single feature in OData format
    /// </summary>
    private static async Task WriteODataFeatureAsync(
        Utf8JsonWriter writer,
        Feature feature,
        int layerId,
        int? layerSrid,
        AxisOrder axisOrder,
        HashSet<string>? select,
        System.Collections.Immutable.ImmutableArray<ODataComputeExpression> computeExpressions,
        IGeometryService geometryService,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        var requiresCompute = !computeExpressions.IsDefaultOrEmpty;
        HashSet<string>? writtenProperties = requiresCompute
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;

        // Core OData feature properties
        writer.WriteNumber("ObjectId", feature.Id);
        writer.WriteNumber("LayerId", layerId);
        if (writtenProperties is not null)
        {
            _ = writtenProperties.Add("ObjectId");
            _ = writtenProperties.Add("LayerId");
        }

        if (select == null || select.Contains("Geometry"))
        {
            var geometry = ODataGeometryConverter.ConvertWkbToGeometry(geometryService, feature.Geometry, layerSrid, axisOrder);
            if (geometry != null)
            {
                writer.WritePropertyName("Geometry");
                JsonSerializer.Serialize(writer, geometry, ODataJsonContext.Default.ODataSpatialGeometry);
            }
            else
            {
                writer.WriteNull("Geometry");
            }

            if (writtenProperties is not null)
            {
                _ = writtenProperties.Add("Geometry");
            }
        }

        Dictionary<string, object?>? computeSource = null;
        if (feature.Attributes != null)
        {
            var normalized = ODataAttributeSerializer.NormalizeAttributes(feature.Attributes);
            if (requiresCompute)
            {
                computeSource = new Dictionary<string, object?>(normalized, StringComparer.OrdinalIgnoreCase)
                {
                    ["ObjectId"] = feature.Id,
                    ["LayerId"] = layerId
                };
            }

            foreach (var kvp in normalized)
            {
                if (ODataUtilityService.IsReservedFeatureProperty(kvp.Key))
                {
                    continue;
                }

                if (select == null || select.Contains(kvp.Key))
                {
                    if (writtenProperties == null || writtenProperties.Add(kvp.Key))
                    {
                        await WriteODataJsonValueAsync(writer, kvp.Key, kvp.Value, cancellationToken);
                    }
                }
            }
        }
        else if (requiresCompute)
        {
            computeSource = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ObjectId"] = feature.Id,
                ["LayerId"] = layerId
            };
        }

        if (requiresCompute && computeSource != null)
        {
            ODataComputeService.ApplyCompute(computeSource, computeExpressions);
            foreach (var expression in computeExpressions)
            {
                if (select != null && !select.Contains(expression.Alias))
                {
                    continue;
                }

                if (writtenProperties is HashSet<string> writtenPropertiesSet &&
                    !writtenPropertiesSet.Add(expression.Alias))
                {
                    continue;
                }

                if (computeSource!.TryGetValue(expression.Alias, out var computedValue))
                {
                    await WriteODataJsonValueAsync(writer, expression.Alias, computedValue, cancellationToken);
                }
                else
                {
                    writer.WriteNull(expression.Alias);
                }
            }
        }

        writer.WriteEndObject(); // End feature

        // Allow for cancellation during processing
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Writes OData JSON values with proper type handling
    /// </summary>
    private static async Task WriteODataJsonValueAsync(
        Utf8JsonWriter writer,
        string propertyName,
        object? value,
        CancellationToken cancellationToken)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                break;
            case string s:
                writer.WriteString(propertyName, s);
                break;
            case int i:
                writer.WriteNumber(propertyName, i);
                break;
            case long l:
                writer.WriteNumber(propertyName, l);
                break;
            case double d:
                writer.WriteNumber(propertyName, d);
                break;
            case float f:
                writer.WriteNumber(propertyName, f);
                break;
            case decimal dec:
                writer.WriteNumber(propertyName, dec);
                break;
            case bool b:
                writer.WriteBoolean(propertyName, b);
                break;
            case DateTime dt:
                writer.WriteString(propertyName, dt.ToString("O")); // ISO 8601 format
                break;
            case DateTimeOffset dto:
                writer.WriteString(propertyName, dto.ToString("O")); // ISO 8601 format
                break;
            default:
                // For complex objects, serialize using OData JSON context
                writer.WritePropertyName(propertyName);
                JsonSerializer.Serialize(writer, value, ODataJsonContext.Default.Object);
                break;
        }

        // Allow for cancellation during long attribute writing
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public async Task<IResult> HandleGetFeaturesCountAsync(
        HttpContext context,
        int? layerId,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;
        try
        {
            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.FeaturesCount);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
            if (formatValidation != null)
            {
                return formatValidation;
            }

            if (!layerId.HasValue)
            {
                if (!TryResolveLayerIdFromFilter(filter, out var layerResolution))
                {
                    return ODataUtilityService.CreateODataError(
                        context,
                        "InvalidQueryOption",
                        layerResolution.ErrorMessage ?? "LayerId filter is required for Features $count.",
                        400);
                }

                layerId = layerResolution.LayerId;
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId.Value,
                LayerValidationHelpers.ValidationProtocol.OData,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;

            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.Value.ToString(CultureInfo.InvariantCulture));

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "count",
                HonuaTelemetry.Protocols.OData,
                layerId.Value.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);

            var (featureQuery, queryError) = await _querySearchService.BuildFeatureQueryAsync(
                filter,
                null,
                null,
                null,
                layer,
                cancellationToken: effectiveToken);
            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            var count = await _featureReader.CountAsync(layerId.Value, featureQuery, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            HonuaTelemetry.SetSuccess(featureActivity, (int)Math.Min(count, int.MaxValue));
            return Results.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain");
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidFeaturesQuery(_logger, layerId ?? 0, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            HonuaTelemetry.RecordException(featureActivity, ex);
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.FeaturesQueryFailed(_logger, layerId ?? 0, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    private static bool TryResolveLayerIdFromFilter(string? filter, out (int LayerId, string? ErrorMessage) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(filter))
        {
            result = (0, "LayerId filter is required.");
            return false;
        }

        try
        {
            var parser = new Honua.Core.Queries.Filters.OData.ODataFilterParser();
            var expression = parser.Parse(filter);
            var layerIds = new HashSet<int>();
            if (!TryCollectLayerIds(expression, layerIds) || layerIds.Count != 1)
            {
                result = (0, "LayerId filter must specify a single layer.");
                return false;
            }

            result = (layerIds.First(), null);
            return true;
        }
        catch (Exception)
        {
            result = (0, InvalidLayerIdFilterMessage);
            return false;
        }
    }

    private static bool TryCollectLayerIds(
        Honua.Core.Queries.Filters.FilterExpression expression,
        HashSet<int> layerIds)
    {
        switch (expression)
        {
            case Honua.Core.Queries.Filters.BinaryExpression binary:
                if (binary.Operator is Honua.Core.Queries.Filters.BinaryOperator.And)
                {
                    return TryCollectLayerIds(binary.Left, layerIds) &&
                           TryCollectLayerIds(binary.Right, layerIds);
                }

                if (binary.Operator is Honua.Core.Queries.Filters.BinaryOperator.Or)
                {
                    return false;
                }

                if (binary.Operator is Honua.Core.Queries.Filters.BinaryOperator.Equal)
                {
                    if (TryExtractLayerId(binary.Left, binary.Right, layerIds))
                    {
                        return true;
                    }

                    if (TryExtractLayerId(binary.Right, binary.Left, layerIds))
                    {
                        return true;
                    }

                    return true;
                }

                return true;

            default:
                return true;
        }
    }

    private static bool TryExtractLayerId(
        Honua.Core.Queries.Filters.FilterExpression left,
        Honua.Core.Queries.Filters.FilterExpression right,
        HashSet<int> layerIds)
    {
        if (left is Honua.Core.Queries.Filters.PropertyReference property &&
            property.PropertyName.Equals("LayerId", StringComparison.OrdinalIgnoreCase) &&
            right is Honua.Core.Queries.Filters.Literal literal &&
            TryParseLayerId(literal.Value, out var layerId))
        {
            layerIds.Add(layerId);
            return true;
        }

        return false;
    }

    private static bool TryParseLayerId(object? value, out int layerId)
    {
        layerId = default;
        if (value is null)
        {
            return false;
        }

        if (value is int intValue)
        {
            layerId = intValue;
            return true;
        }

        if (value is long longValue)
        {
            if (longValue < int.MinValue || longValue > int.MaxValue)
            {
                return false;
            }

            layerId = (int)longValue;
            return true;
        }

        if (value is double doubleValue)
        {
            if (doubleValue % 1 != 0)
            {
                return false;
            }

            if (doubleValue < int.MinValue || doubleValue > int.MaxValue)
            {
                return false;
            }

            layerId = (int)doubleValue;
            return true;
        }

        return false;
    }


    /// <summary>
    /// Logging methods for OData streaming query operations.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 3020, Level = LogLevel.Warning, Message = "Invalid OData streaming features query for layer {LayerId}.")]
        public static partial void InvalidFeaturesQuery(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3021, Level = LogLevel.Error, Message = "OData streaming features query failed for layer {LayerId}.")]
        public static partial void FeaturesQueryFailed(ILogger logger, int layerId, Exception exception);
    }
}
