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
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// Handler for OData streaming query operations for large-scale feature queries.
/// Uses streaming JSON writing to reduce memory pressure for large result sets.
/// </summary>
internal sealed partial class ODataStreamingQueryHandler(
    ODataQueryDependencies dependencies,
    IStreamingFeatureStore streamingFeatureStore,
    ILogger<ODataStreamingQueryHandler> logger)
{
    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly IGeometryService _geometryService = dependencies.GeometryService;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly IStreamingFeatureStore _streamingFeatureStore = streamingFeatureStore ?? throw new ArgumentNullException(nameof(streamingFeatureStore));
    private readonly ODataValidationService _validationService = dependencies.ValidationService;
    private readonly ODataQuerySearchService _querySearchService = dependencies.QuerySearchService;
    private readonly ILogger<ODataStreamingQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private const int StreamingThreshold = 1000;

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
        [FromQuery(Name = "$count")] string? count = null,
        [FromQuery(Name = "$expand")] string? expand = null,
        [FromQuery(Name = "$search")] string? search = null,
        [FromQuery(Name = "$apply")] string? apply = null,
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

            var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
            if (formatValidation != null)
            {
                return formatValidation;
            }

            if (!ODataRequestValidation.TryParsePaging(
                context,
                _validationService,
                top,
                skip,
                count,
                out var paging,
                out var pagingError))
            {
                return pagingError!;
            }

            var pagination = paging!.Pagination;
            var topValue = paging.Top;
            var skipValue = paging.Skip;
            var countValue = paging.Count;
            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);

            if (!string.IsNullOrWhiteSpace(apply))
            {
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

                var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
                var result = await _querySearchService.HandleApplyAsync(
                    resolvedLayerId.Value,
                    apply,
                    filter,
                    baseUrl,
                    effectiveToken);

                ODataUtilityService.SetODataHeaders(context);
                return Results.Json(result, ODataJsonContext.Default.ODataAggregationResult,
                    contentType: ODataUtilityService.GetODataContentType());
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
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

                var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
                var result = await _querySearchService.HandleSearchAsync(
                    resolvedLayerId.Value,
                    search,
                    baseUrl,
                    topValue,
                    skipValue,
                    countValue,
                    effectiveToken);

                ODataUtilityService.SetODataHeaders(context);
                return Results.Json(result, ODataJsonContext.Default.ODataSearchResult,
                    contentType: ODataUtilityService.GetODataContentType());
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

            // Verify layer exists
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId.Value, effectiveToken);
            if (!layerResult.IsValid)
            {
                var errorMessage = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
                var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? 400 : 404;
                var errorCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? "InvalidRequest" : "ResourceNotFound";
                return ODataUtilityService.CreateODataError(context, errorCode, errorMessage, statusCode);
            }

            var layer = layerResult.Resource!;
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.Value.ToString(CultureInfo.InvariantCulture));

            // Build feature query using query service
            var featureQuery = _querySearchService.BuildFeatureQuery(
                filter, orderby, pagination.Limit,
                pagination.Offset, layer, out var queryError);

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
                return await queryHandler.HandleGetFeaturesNonStreamingAsync(
                    context, layerId.Value, filter, select, orderby, topValue, skipValue, countValue, expand, cancellationToken);
            }

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                HonuaTelemetry.Protocols.OData,
                layerId.Value.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);

            // Get total count if requested
            long? totalCount = null;
            if (countValue == true)
            {
                totalCount = await _featureReader.CountAsync(layerId.Value, featureQuery, effectiveToken);
            }

            var axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
                _crsRegistry,
                layer.SpatialReference.ToSrid(),
                effectiveToken);

            // Set up streaming response
            context.Response.ContentType = ODataUtilityService.GetODataContentType();
            context.Response.Headers["Transfer-Encoding"] = "chunked";
            ODataUtilityService.SetODataHeaders(context);

            var selectedFields = ODataUtilityService.ParseSelect(select);

            // Stream the OData response
            await StreamODataFeaturesAsync(
                (IAsyncEnumerable<Feature>)_streamingFeatureStore.StreamFeaturesAsync(layerId.Value, featureQuery, cancellationToken),
                context,
                layerId.Value,
                layer.SpatialReference.ToSrid(),
                axisOrder,
                selectedFields,
                expand,
                totalCount,
                _geometryService,
                cancellationToken);

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

    /// <summary>
    /// Streams OData features response to reduce memory pressure
    /// </summary>
    private static async Task StreamODataFeaturesAsync(
        IAsyncEnumerable<Feature> features,
        HttpContext context,
        int layerId,
        int? layerSrid,
        AxisOrder axisOrder,
        HashSet<string>? select,
        string? expand,
        long? totalCount,
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

        if (totalCount.HasValue)
        {
            writer.WriteNumber("@odata.count", totalCount.Value);
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        writer.WriteString("@odata.context", ODataUtilityService.BuildContextUrl(baseUrl, "Features", select: select != null ? string.Join(",", select) : null, expand: expand));

        // Start value array
        writer.WriteStartArray("value");

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            await WriteODataFeatureAsync(
                writer,
                feature,
                layerId,
                layerSrid,
                axisOrder,
                select,
                geometryService,
                cancellationToken);

            // Flush periodically for better streaming
            await writer.FlushAsync(cancellationToken);
        }

        // End value array
        writer.WriteEndArray();

        // End OData response
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
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
        IGeometryService geometryService,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();

        // Core OData feature properties
        writer.WriteNumber("ObjectId", feature.Id);
        writer.WriteNumber("LayerId", layerId);

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
        }

        if (feature.Attributes != null)
        {
            var normalized = ODataAttributeSerializer.NormalizeAttributes(feature.Attributes);
            foreach (var kvp in normalized)
            {
                if (ODataUtilityService.IsReservedFeatureProperty(kvp.Key))
                {
                    continue;
                }

                if (select == null || select.Contains(kvp.Key))
                {
                    await WriteODataJsonValueAsync(writer, kvp.Key, kvp.Value, cancellationToken);
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
            var layerResult = await _resourceValidator.ValidateLayerAsync(layerId.Value, effectiveToken);
            if (!layerResult.IsValid)
            {
                var errorMessage = layerResult.ErrorMessage ?? $"Layer {layerId} not found";
                var statusCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? 400 : 404;
                var errorCode = layerResult.ErrorCode == ResourceValidationError.InvalidIdentifier ? "InvalidRequest" : "ResourceNotFound";
                return ODataUtilityService.CreateODataError(context, errorCode, errorMessage, statusCode);
            }

            var layer = layerResult.Resource!;
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.Value.ToString(CultureInfo.InvariantCulture));

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "count",
                HonuaTelemetry.Protocols.OData,
                layerId.Value.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);

            var featureQuery = _querySearchService.BuildFeatureQuery(
                filter, null, null, null, layer, out var queryError);
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
        catch (Exception ex)
        {
            result = (0, ex.Message);
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
                }

                return false;

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
        [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Invalid OData streaming features query for layer {LayerId}.")]
        public static partial void InvalidFeaturesQuery(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "OData streaming features query failed for layer {LayerId}.")]
        public static partial void FeaturesQueryFailed(ILogger logger, int layerId, Exception exception);
    }
}
